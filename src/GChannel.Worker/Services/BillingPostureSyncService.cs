using GChannel.ApiService.Configuration;
using GChannel.ApiService.Data;
using GChannel.ApiService.Services;
using GChannel.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GChannel.Worker.Services;

/// <summary>
/// Nightly billing IAM security-posture scan. Walks every reseller billing (sub-)account, reads its IAM
/// policy (direct + master-inherited members), resolves each distinct member's identity / 2SV /
/// Advanced Protection / key counts through the shared <see cref="MemberIdentityResolver"/>, and
/// materialises the result into BillingPostureAccounts / BillingPostureMembers for the posture page.
/// Runs once per day at <see cref="GoogleBillingOptions.PostureScanHourUtc"/> (plus a catch-up run on
/// startup when the last scan is stale), under a Redis single-flight lock so only one replica scans.
/// </summary>
public sealed class BillingPostureSyncService(
    IOptions<GoogleChannelOptions> channelOptions,
    IOptions<GoogleBillingOptions> billingOptions,
    IBillingBudgetsService billing,
    MemberIdentityResolver resolver,
    IMarketplaceOrdersService marketplace,
    IServiceScopeFactory scopeFactory,
    IConnectionMultiplexer redis,
    ILoggerFactory loggerFactory,
    ILogger<BillingPostureSyncService> logger) : BackgroundService
{
    private const string LockKey = "billing:posture:lock";
    private const string CursorScope = "billing-posture";
    private const string OrdersCursorScope = "marketplace-orders";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = billingOptions.Value;
        var channel = channelOptions.Value;
        if (!opts.PostureScanEnabled)
        {
            logger.LogInformation("Billing posture scan is disabled (GoogleBilling:PostureScanEnabled=false).");
            return;
        }
        if (!channel.HasServiceAccountCredential || string.IsNullOrWhiteSpace(channel.ImpersonateUser))
        {
            logger.LogWarning("Billing posture scan needs GoogleChannel:ServiceAccountKeyJson + ImpersonateUser; skipping.");
            return;
        }

        var redisDb = redis.GetDatabase();
        try { await redisDb.KeyDeleteAsync(LockKey); } catch (Exception ex) { logger.LogDebug(ex, "Could not clear posture lock on startup."); }

        var hour = Math.Clamp(opts.PostureScanHourUtc, 0, 23);
        logger.LogInformation("Billing posture scan enabled; runs daily at {Hour:00}:00 UTC.", hour);

        // Catch-up: run now if the last completed scan is older than the stale window (or never ran).
        var last = await WithDbAsync(db => db.SyncCursors.AsNoTracking()
            .Where(c => c.Scope == CursorScope).Select(c => c.LastFullPassUtc).FirstOrDefaultAsync(stoppingToken));
        var runNow = last is null || DateTimeOffset.UtcNow - last.Value > TimeSpan.FromHours(Math.Max(1, opts.PostureScanStaleHours));

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!runNow)
            {
                var delay = UntilNext(hour);
                logger.LogDebug("Next billing posture scan in {Delay}.", delay);
                try { await Task.Delay(delay, stoppingToken); } catch (OperationCanceledException) { break; }
            }
            runNow = false;

            try
            {
                // Lock for 23h: a second replica waking at the same scheduled hour finds it and skips.
                if (!await redisDb.StringSetAsync(LockKey, Environment.MachineName, TimeSpan.FromHours(23), when: When.NotExists))
                {
                    logger.LogDebug("Another replica already ran the posture scan today; skipping.");
                    continue;
                }

                await RunScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Billing posture scan failed; will retry at the next scheduled hour.");
                await RecordRunAsync(completed: false, ex.Message, stoppingToken);
            }
        }
    }

    private static TimeSpan UntilNext(int hourUtc)
    {
        var now = DateTimeOffset.UtcNow;
        var next = new DateTimeOffset(now.Year, now.Month, now.Day, hourUtc, 0, 0, TimeSpan.Zero);
        if (next <= now) { next = next.AddDays(1); }
        return next - now;
    }

    private async Task RunScanAsync(CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var channelClient = new GoogleChannelClient(
            new ServiceAccountCredentialSource(channelOptions.Value), channelOptions, loggerFactory.CreateLogger<GoogleChannelClient>());

        // 1) Every billing account the service account can see (masters + their sub-accounts).
        var accounts = await billing.ListBillingAccountsAsync(ct);
        if (!accounts.DiscoveryAvailable)
        {
            throw new InvalidOperationException($"Billing account discovery unavailable: {accounts.DiscoveryError}");
        }
        var open = accounts.Accounts.Where(a => a.Open).ToList();
        logger.LogInformation("Posture scan: {Count} open billing accounts ({Subs} sub-accounts).", open.Count, open.Count(a => a.IsSubaccount));

        // 2) IAM members per account (paced inside the billing service). Masters are read too so their
        // own direct members are assessed; sub-accounts get master members flagged Inherited.
        var memberRows = new List<(BillingAccountInfo Account, BillingAccountMembersResult Members)>();
        foreach (var account in open)
        {
            ct.ThrowIfCancellationRequested();
            var result = await billing.GetBillingAccountMembersAsync(account.Id, ct);
            memberRows.Add((account, result));
            if (!result.Available)
            {
                logger.LogWarning("Posture scan: IAM policy unreadable for {Account}: {Error}", account.Id, result.Error);
            }
        }

        // 3) Resolve each distinct user/group email once. Refresh=true so the nightly scan always reads
        // live rather than inheriting whatever the UI cached during the day.
        var emails = memberRows
            .Where(r => r.Members.Available)
            .SelectMany(r => r.Members.Direct.Concat(r.Members.Inherited))
            .Where(m => m.MemberType is "user" or "group")
            .Select(m => m.Email)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Dictionary<string, MemberIdentityInfo> identities;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GChannelDbContext>();
            var resolved = await resolver.ResolveAsync(emails, channelClient, db, refresh: true, ct);
            identities = resolved.Members.ToDictionary(m => m.Email, StringComparer.OrdinalIgnoreCase);
            if (resolved.DirectoryError is not null)
            {
                logger.LogWarning("Posture scan: Admin SDK reported '{Error}'.", resolved.DirectoryError);
            }
        }

        // Sub-account -> Channel customer via the §15 spend mapping (best-effort enrichment).
        var customerBySub = await WithDbAsync(db => db.CustomerSpend.AsNoTracking()
            .Where(s => s.CustomerId != null)
            .GroupBy(s => s.BillingSubAccountId)
            .Select(g => new { Sub = g.Key, CustomerId = g.Max(s => s.CustomerId) })
            .ToDictionaryAsync(x => x.Sub, x => x.CustomerId!, StringComparer.OrdinalIgnoreCase, ct));

        // 4) Upsert the read-model. Members are replaced per account; accounts with an unreadable policy
        // keep their previous members and just record the error.
        var now = DateTimeOffset.UtcNow;
        var totalMembers = 0;
        await WithDbAsync(async db =>
        {
            var existingAccounts = await db.BillingPostureAccounts.ToDictionaryAsync(a => a.BillingAccountId, StringComparer.OrdinalIgnoreCase, ct);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (account, members) in memberRows)
            {
                seen.Add(account.Id);
                if (!existingAccounts.TryGetValue(account.Id, out var row))
                {
                    row = new BillingPostureAccountRecord { BillingAccountId = account.Id };
                    db.BillingPostureAccounts.Add(row);
                }
                row.DisplayName = account.DisplayName;
                row.MasterBillingAccountId = account.MasterBillingAccountId;
                row.Open = account.Open;
                row.CustomerId = customerBySub.GetValueOrDefault(account.Id);
                row.Error = members.Available ? null : members.Error;
                row.LastSyncedUtc = now;

                if (!members.Available) { continue; }

                var old = await db.BillingPostureMembers.Where(m => m.BillingAccountId == account.Id).ToListAsync(ct);
                db.BillingPostureMembers.RemoveRange(old);

                foreach (var m in members.Direct.Concat(members.Inherited))
                {
                    identities.TryGetValue(m.Email, out var id);
                    db.BillingPostureMembers.Add(ToRecord(account.Id, m, id, now));
                    totalMembers++;
                }
            }

            // Accounts that disappeared (closed / no longer visible) are dropped with their members.
            foreach (var gone in existingAccounts.Values.Where(a => !seen.Contains(a.BillingAccountId)).ToList())
            {
                var oldMembers = await db.BillingPostureMembers.Where(m => m.BillingAccountId == gone.BillingAccountId).ToListAsync(ct);
                db.BillingPostureMembers.RemoveRange(oldMembers);
                db.BillingPostureAccounts.Remove(gone);
            }

            await db.SaveChangesAsync(ct);
        });

        await RecordRunAsync(completed: true, null, ct);
        logger.LogInformation(
            "Posture scan complete in {Elapsed:0}s: {Accounts} accounts, {Members} member grants, {Identities} distinct identities resolved.",
            (DateTimeOffset.UtcNow - started).TotalSeconds, memberRows.Count, totalMembers, identities.Count);

        // Marketplace orders ride along on the same nightly account enumeration. Failures are per
        // account (permission / API not enabled) and never fail the posture scan.
        if (billingOptions.Value.MarketplaceOrdersEnabled && marketplace.IsConfigured)
        {
            await SyncMarketplaceOrdersAsync(open, customerBySub, ct);
        }
    }

    private async Task SyncMarketplaceOrdersAsync(
        IReadOnlyList<BillingAccountInfo> accounts,
        IReadOnlyDictionary<string, string> customerBySub,
        CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var all = new List<MarketplaceOrder>();
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var account in accounts)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var orders = await marketplace.ListOrdersAsync(account.Id, ct);
                all.AddRange(orders.Select(o => o with { CustomerId = customerBySub.GetValueOrDefault(account.Id) }));
                scanned.Add(account.Id);
            }
            catch (Exception ex)
            {
                // Keep the first ~150 chars so the cursor Notes (512) can hold a few accounts' errors.
                var msg = ex.Message.Length > 150 ? ex.Message[..150] : ex.Message;
                errors[account.Id] = msg;
                logger.LogWarning("Marketplace orders unavailable for {Account}: {Error}", account.Id, msg);
            }
        }

        var now = DateTimeOffset.UtcNow;
        await WithDbAsync(async db =>
        {
            // Replace orders only for accounts we successfully listed; keep the previous snapshot otherwise.
            var stale = await db.MarketplaceOrders.Where(o => scanned.Contains(o.BillingAccountId)).ToListAsync(ct);
            db.MarketplaceOrders.RemoveRange(stale);
            foreach (var o in all)
            {
                db.MarketplaceOrders.Add(new MarketplaceOrderRecord
                {
                    Name = o.Name,
                    BillingAccountId = o.BillingAccountId,
                    CustomerId = o.CustomerId,
                    DisplayName = o.DisplayName,
                    Provider = o.Provider,
                    ProductsCsv = string.Join('|', o.Products),
                    OffersCsv = string.Join('|', o.Offers),
                    CreateTime = o.CreateTime,
                    UpdateTime = o.UpdateTime,
                    Active = o.Active,
                    LastSyncedUtc = now
                });
            }

            var cursor = await db.SyncCursors.FirstOrDefaultAsync(c => c.Scope == OrdersCursorScope, ct);
            if (cursor is null) { cursor = new SyncCursor { Scope = OrdersCursorScope }; db.SyncCursors.Add(cursor); }
            cursor.LastCycleUtc = now;
            if (scanned.Count > 0) { cursor.LastFullPassUtc = now; }
            var notes = string.Join('\n', errors.Select(kv => $"{kv.Key}={kv.Value}"));
            cursor.Notes = notes.Length == 0 ? null : notes.Length > 512 ? notes[..512] : notes;
            await db.SaveChangesAsync(ct);
        });

        logger.LogInformation(
            "Marketplace orders scan complete in {Elapsed:0}s: {Orders} orders across {Accounts} accounts ({Errors} accounts unreadable).",
            (DateTimeOffset.UtcNow - started).TotalSeconds, all.Count, scanned.Count, errors.Count);
    }

    private static BillingPostureMemberRecord ToRecord(string accountId, BillingAccountMember m, MemberIdentityInfo? id, DateTimeOffset now) => new()
    {
        BillingAccountId = accountId,
        Email = m.Email,
        Role = m.Role,
        Inherited = m.Inherited,
        MemberType = m.MemberType,
        RoleDisplayName = m.RoleDisplayName,
        AccountKind = (int)(id?.AccountKind ?? GoogleAccountKind.Unknown),
        WorkspaceCustomerId = id?.WorkspaceCustomerId,
        SecurityVisible = id?.SecurityVisible ?? false,
        TwoSvEnrolled = id?.TwoStepVerificationEnrolled,
        TwoSvEnforced = id?.TwoStepVerificationEnforced,
        AdvancedProtection = (int)(id?.AdvancedProtection ?? AdvancedProtectionStatus.Unknown),
        AdvancedProtectionAsOf = id?.AdvancedProtectionAsOf,
        SecurityKeys = id?.SecurityKeys,
        Passkeys = id?.Passkeys,
        KeysAsOf = id?.KeysAsOf,
        IsAdmin = id?.IsAdmin,
        Suspended = id?.Suspended,
        LastSyncedUtc = now
    };

    private async Task RecordRunAsync(bool completed, string? error, CancellationToken ct)
    {
        try
        {
            await WithDbAsync(async db =>
            {
                var cursor = await db.SyncCursors.FirstOrDefaultAsync(c => c.Scope == CursorScope, ct);
                if (cursor is null)
                {
                    cursor = new SyncCursor { Scope = CursorScope };
                    db.SyncCursors.Add(cursor);
                }
                cursor.LastCycleUtc = DateTimeOffset.UtcNow;
                if (completed) { cursor.LastFullPassUtc = cursor.LastCycleUtc; }
                cursor.Notes = error is null ? null : error.Length > 512 ? error[..512] : error;
                await db.SaveChangesAsync(ct);
            });
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not record posture scan cursor.");
        }
    }

    private async Task<T> WithDbAsync<T>(Func<GChannelDbContext, Task<T>> work)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GChannelDbContext>();
        return await work(db);
    }

    private async Task WithDbAsync(Func<GChannelDbContext, Task> work)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GChannelDbContext>();
        await work(db);
    }
}
