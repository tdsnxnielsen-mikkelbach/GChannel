using GChannel.Core.Configuration;
using GChannel.Core.GoogleApis;
using GChannel.Core.Messaging;
using GChannel.Core.Persistence.Documents;
using GChannel.Shared.Contracts;
using GChannel.Worker.Infrastructure;
using Marten;
using Microsoft.Extensions.Options;
using Wolverine;

namespace GChannel.Worker.Handlers;

/// <summary>
/// Nightly billing IAM security-posture scan (the <c>RunBillingPostureScan</c> recurring tick): enumerates the
/// billing accounts the service account can see, reads each account's IAM members, resolves every distinct
/// identity (consumer vs managed, 2SV, Advanced Protection, keys) and replaces the BillingPostureAccount /
/// BillingPostureMember documents. Marketplace orders ride along on the same account enumeration.
/// </summary>
public sealed class BillingPostureScanHandler(
    IOptions<GoogleBillingOptions> billingOptions,
    ServiceAccountChannelClientProvider clients,
    IBillingBudgetsService billing,
    MemberIdentityResolver resolver,
    IMarketplaceOrdersService marketplace,
    IDocumentStore store,
    RecurringJobCatalog catalog,
    ILogger<BillingPostureScanHandler> logger)
{
    public const string CursorScope = "billing-posture";
    public const string OrdersCursorScope = "marketplace-orders";

    public async Task<OutgoingMessages> Handle(RunBillingPostureScan tick, IDocumentSession session, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        await RecurringJobRunner.RunAsync(tick, catalog[RecurringJobs.BillingPostureScan], session, outgoing, logger, async c =>
        {
            try
            {
                await RunScanAsync(c);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await RecordRunAsync(completed: false, ex.Message, c);
                throw;
            }
        }, ct);
        return outgoing;
    }

    private async Task RunScanAsync(CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var channelClient = clients.Client ?? throw new InvalidOperationException("No service-account credential configured.");

        // 1) Every billing account the service account can see (masters + their sub-accounts).
        var accounts = await billing.ListBillingAccountsAsync(ct);
        if (!accounts.DiscoveryAvailable)
        {
            throw new InvalidOperationException($"Billing account discovery unavailable: {accounts.DiscoveryError}");
        }
        var open = accounts.Accounts.Where(a => a.Open).ToList();
        logger.LogInformation("Posture scan: {Count} open billing accounts ({Subs} sub-accounts).", open.Count, open.Count(a => a.IsSubaccount));

        // 2) IAM members per account (paced inside the billing service).
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

        // 3) Resolve each distinct user/group email once (refresh=true: the nightly scan always reads live).
        var emails = memberRows
            .Where(r => r.Members.Available)
            .SelectMany(r => r.Members.Direct.Concat(r.Members.Inherited))
            .Where(m => m.MemberType is "user" or "group")
            .Select(m => m.Email)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Dictionary<string, MemberIdentityInfo> identities;
        await using (var query = store.QuerySession())
        {
            var resolved = await resolver.ResolveAsync(emails, channelClient, query, refresh: true, ct);
            identities = resolved.Members.ToDictionary(m => m.Email, StringComparer.OrdinalIgnoreCase);
            if (resolved.DirectoryError is not null)
            {
                logger.LogWarning("Posture scan: Admin SDK reported '{Error}'.", resolved.DirectoryError);
            }
        }

        // Sub-account -> Channel customer via the spend mapping (best-effort enrichment).
        Dictionary<string, string> customerBySub;
        await using (var query = store.QuerySession())
        {
            var spend = await query.Query<CustomerSpendRecord>().Where(s => s.CustomerId != null).ToListAsync(ct);
            customerBySub = spend
                .GroupBy(s => s.BillingSubAccountId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Max(s => s.CustomerId)!, StringComparer.OrdinalIgnoreCase);
        }

        // 4) Replace the read model. Members are replaced per account; accounts with an unreadable policy keep
        //    their previous members and just record the error.
        var now = DateTimeOffset.UtcNow;
        var totalMembers = 0;
        await using (var session = store.LightweightSession())
        {
            var existingAccounts = (await session.Query<BillingPostureAccountRecord>().ToListAsync(ct))
                .ToDictionary(a => a.BillingAccountId, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (account, members) in memberRows)
            {
                seen.Add(account.Id);
                if (!existingAccounts.TryGetValue(account.Id, out var row))
                {
                    row = new BillingPostureAccountRecord { BillingAccountId = account.Id };
                }
                row.DisplayName = account.DisplayName;
                row.MasterBillingAccountId = account.MasterBillingAccountId;
                row.Open = account.Open;
                row.CustomerId = customerBySub.GetValueOrDefault(account.Id);
                row.Error = members.Available ? null : members.Error;
                row.LastSyncedUtc = now;
                session.Store(row);

                if (!members.Available) { continue; }

                var accountId = account.Id;
                session.DeleteWhere<BillingPostureMemberRecord>(m => m.BillingAccountId == accountId);
                foreach (var m in members.Direct.Concat(members.Inherited))
                {
                    identities.TryGetValue(m.Email, out var id);
                    session.Store(ToRecord(account.Id, m, id, now));
                    totalMembers++;
                }
            }

            // Accounts that disappeared (closed / no longer visible) are dropped with their members.
            foreach (var gone in existingAccounts.Values.Where(a => !seen.Contains(a.BillingAccountId)).ToList())
            {
                var goneId = gone.BillingAccountId;
                session.DeleteWhere<BillingPostureMemberRecord>(m => m.BillingAccountId == goneId);
                session.Delete(gone);
            }

            await session.SaveChangesAsync(ct);
        }

        await RecordRunAsync(completed: true, null, ct);
        logger.LogInformation(
            "Posture scan complete in {Elapsed:0}s: {Accounts} accounts, {Members} member grants, {Identities} distinct identities resolved.",
            (DateTimeOffset.UtcNow - started).TotalSeconds, memberRows.Count, totalMembers, identities.Count);

        if (billingOptions.Value.MarketplaceOrdersEnabled && marketplace.IsConfigured)
        {
            await SyncMarketplaceOrdersAsync(open, customerBySub, ct);
        }
    }

    private async Task SyncMarketplaceOrdersAsync(IReadOnlyList<BillingAccountInfo> accounts, IReadOnlyDictionary<string, string> customerBySub, CancellationToken ct)
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
                var msg = ex.Message.Length > 150 ? ex.Message[..150] : ex.Message;
                errors[account.Id] = msg;
                logger.LogWarning("Marketplace orders unavailable for {Account}: {Error}", account.Id, msg);
            }
        }

        var now = DateTimeOffset.UtcNow;
        await using var session = store.LightweightSession();
        // Replace orders only for accounts we successfully listed; keep the previous snapshot otherwise.
        var scannedList = scanned.ToList();
        session.DeleteWhere<MarketplaceOrderRecord>(o => o.BillingAccountId.IsOneOf(scannedList));
        foreach (var o in all)
        {
            session.Store(new MarketplaceOrderRecord
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

        var cursor = await session.LoadAsync<SyncCursor>(OrdersCursorScope, ct) ?? new SyncCursor { Id = OrdersCursorScope };
        cursor.LastCycleUtc = now;
        if (scanned.Count > 0) { cursor.LastFullPassUtc = now; }
        var notes = string.Join('\n', errors.Select(kv => $"{kv.Key}={kv.Value}"));
        cursor.Notes = notes.Length == 0 ? null : notes.Length > 512 ? notes[..512] : notes;
        session.Store(cursor);
        await session.SaveChangesAsync(ct);

        logger.LogInformation(
            "Marketplace orders scan complete in {Elapsed:0}s: {Orders} orders across {Accounts} accounts ({Errors} accounts unavailable).",
            (DateTimeOffset.UtcNow - started).TotalSeconds, all.Count, scanned.Count, errors.Count);
    }

    private static BillingPostureMemberRecord ToRecord(string accountId, BillingAccountMember m, MemberIdentityInfo? id, DateTimeOffset now) => new()
    {
        Id = BillingPostureMemberRecord.MakeId(accountId, m.Email, m.Role, m.Inherited),
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
            await using var session = store.LightweightSession();
            var cursor = await session.LoadAsync<SyncCursor>(CursorScope, ct) ?? new SyncCursor { Id = CursorScope };
            cursor.LastCycleUtc = DateTimeOffset.UtcNow;
            if (completed) { cursor.LastFullPassUtc = cursor.LastCycleUtc; }
            cursor.Notes = error is null ? null : error.Length > 512 ? error[..512] : error;
            session.Store(cursor);
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not record posture scan cursor.");
        }
    }
}
