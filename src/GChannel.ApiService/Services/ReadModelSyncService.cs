using System.Diagnostics;
using GChannel.ApiService.Configuration;
using GChannel.ApiService.Data;
using GChannel.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GChannel.ApiService.Services;

/// <summary>
/// §10 read-model sync. Incrementally materialises the estate (channel partner links + direct and
/// indirect customers) into SQL so the dashboard/estate views read from durable, indexed tables
/// instead of a live Channel API fan-out per request. Each cycle refreshes the account's direct
/// customers and the link roster, then a budgeted, staleness-ordered slice of the active links'
/// downstream customers — so a single cycle stays within the ListCustomers quota regardless of how
/// many resellers exist (a "rolling refresh"). Disabled unless <see cref="GoogleChannelOptions.UseReadModel"/>
/// plus a service account + impersonation user are configured.
/// </summary>
public sealed class ReadModelSyncService(
    IOptions<GoogleChannelOptions> options,
    IServiceScopeFactory scopeFactory,
    IConnectionMultiplexer redis,
    ILoggerFactory loggerFactory,
    ILogger<ReadModelSyncService> logger) : BackgroundService
{
    // Cluster-wide single-flight guard so only one replica syncs per interval.
    private const string SyncLockKey = "readmodel:sync:lock";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.ReadModelSyncEnabled)
        {
            logger.LogInformation("Read-model sync is disabled (UseReadModel off or no service account); dashboard uses the live path.");
            return;
        }

        var credentialSource = new ServiceAccountCredentialSource(opts);
        var client = new GoogleChannelClient(credentialSource, options, loggerFactory.CreateLogger<GoogleChannelClient>());

        var interval = TimeSpan.FromSeconds(opts.BackgroundRefreshSeconds);
        var db = redis.GetDatabase();
        using var timer = new PeriodicTimer(interval);

        logger.LogInformation(
            "Read-model sync enabled; cycle every {Interval}s, up to {Links} links/cycle.",
            opts.BackgroundRefreshSeconds, opts.ReadModelLinksPerCycle);

        do
        {
            try
            {
                var acquired = await db.StringSetAsync(SyncLockKey, Environment.MachineName, interval, when: When.NotExists);
                if (!acquired)
                {
                    logger.LogDebug("Another replica already synced the read-model this interval; skipping.");
                    continue;
                }

                await RunCycleAsync(client, opts, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Read-model sync cycle failed; will retry next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunCycleAsync(GoogleChannelClient client, GoogleChannelOptions opts, CancellationToken ct)
    {
        var startedAt = Stopwatch.GetTimestamp();
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<GChannelDbContext>();
        var now = DateTimeOffset.UtcNow;

        // Direct customers — refreshed every cycle (one cheap ListCustomers pass).
        var direct = await client.ListCustomersAsync(ct);
        await UpsertCustomersAsync(dbContext, direct.Customers, owningLinkId: null, now, ct);
        foreach (var c in direct.Customers)
        {
            await SyncCustomerEntitlementsAsync(dbContext, client, c.Id, owningLinkId: null, now, ct);
        }

        // Link roster — refreshed every cycle (account-level, quota-light).
        var links = await client.ListChannelPartnerLinksAsync(ct);
        await UpsertLinksAsync(dbContext, links.Links, now, ct);

        // Pick the stalest ACTIVE links and refresh their downstream customers, capped per cycle so we
        // stay within the ListCustomers quota; the whole estate is covered over several cycles.
        var activeIds = links.Links
            .Where(l => string.Equals(l.LinkState, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var stalest = await dbContext.ResellerLinks
            .Where(r => activeIds.Contains(r.LinkId))
            .OrderBy(r => r.LastSyncedUtc)
            .Take(Math.Max(1, opts.ReadModelLinksPerCycle))
            .Select(r => r.LinkId)
            .ToListAsync(ct);

        foreach (var linkId in stalest)
        {
            try
            {
                var customers = await client.ListChannelPartnerCustomersAsync(linkId, ct);
                await UpsertCustomersAsync(dbContext, customers.Customers, owningLinkId: linkId, now, ct);
                foreach (var c in customers.Customers)
                {
                    await SyncCustomerEntitlementsAsync(dbContext, client, c.Id, owningLinkId: linkId, now, ct);
                }
                var link = await dbContext.ResellerLinks.FindAsync([linkId], ct);
                if (link is not null)
                {
                    link.CustomerCount = customers.Customers.Count;
                    link.LastSyncedUtc = DateTimeOffset.UtcNow;
                    link.SyncError = null;
                }
                await dbContext.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                var link = await dbContext.ResellerLinks.FindAsync([linkId], ct);
                if (link is not null)
                {
                    link.LastSyncedUtc = DateTimeOffset.UtcNow;
                    link.SyncError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                    await dbContext.SaveChangesAsync(ct);
                }
                logger.LogWarning(ex, "Read-model sync failed for link {Link}.", linkId);
            }
        }

        var cursor = await dbContext.SyncCursors.FindAsync(["links"], ct) ?? new SyncCursor { Scope = "links" };
        if (dbContext.Entry(cursor).State == EntityState.Detached)
        {
            dbContext.SyncCursors.Add(cursor);
        }
        cursor.LastCycleUtc = DateTimeOffset.UtcNow;
        if (stalest.Count < activeIds.Count) { /* still mid-rotation */ }
        else { cursor.LastFullPassUtc = DateTimeOffset.UtcNow; }
        await dbContext.SaveChangesAsync(ct);

        logger.LogInformation(
            "Read-model cycle: {Direct} direct customers, {Links} links, refreshed {Stale} reseller(s) in {Secs}s.",
            direct.Customers.Count, links.Links.Count, stalest.Count,
            (int)Stopwatch.GetElapsedTime(startedAt).TotalSeconds);
    }

    private static async Task UpsertLinksAsync(
        GChannelDbContext db, IReadOnlyList<ChannelPartnerLink> links, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await db.ResellerLinks.ToDictionaryAsync(r => r.LinkId, ct);
        foreach (var l in links)
        {
            if (!existing.TryGetValue(l.Id, out var row))
            {
                row = new ResellerLinkRecord { LinkId = l.Id, LastSyncedUtc = now };
                db.ResellerLinks.Add(row);
            }
            row.ResellerCloudId = l.ResellerCloudIdentityId;
            row.PrimaryDomain = l.ChannelPartner?.PrimaryDomain;
            row.LinkState = l.LinkState ?? "UNSPECIFIED";
            row.CreateTime = l.CreateTime;
            // Don't reset LastSyncedUtc here — the customer fan-out below stamps it so staleness ordering works.
        }
        await db.SaveChangesAsync(ct);
    }

    // Upserts a scope's customers and soft-deletes any previously-stored ones that vanished from the list.
    private static async Task UpsertCustomersAsync(
        GChannelDbContext db, IReadOnlyList<Customer> customers, string? owningLinkId, DateTimeOffset now, CancellationToken ct)
    {
        var seen = customers.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stored = await db.CustomerRecords
            .Where(r => r.OwningLinkId == owningLinkId)
            .ToListAsync(ct);
        var byId = stored.ToDictionary(r => r.CustomerId, StringComparer.OrdinalIgnoreCase);

        foreach (var c in customers)
        {
            if (!byId.TryGetValue(c.Id, out var row))
            {
                row = new CustomerRecord { CustomerId = c.Id };
                db.CustomerRecords.Add(row);
            }
            row.OrgName = c.OrgDisplayName;
            row.Domain = c.Domain;
            row.CloudIdentityId = c.CloudIdentityId;
            row.OwningLinkId = owningLinkId;
            row.CreateTime = c.CreateTime;
            row.LastSyncedUtc = now;
            row.IsDeleted = false;
        }

        foreach (var row in stored.Where(r => !seen.Contains(r.CustomerId) && !r.IsDeleted))
        {
            row.IsDeleted = true;
        }

        await db.SaveChangesAsync(ct);
    }

    // Upserts one customer's entitlements, soft-deletes vanished ones, and denormalises the customer's
    // active seat total onto CustomerRecord for fast reseller ranking. Tolerates a single customer's
    // read failing (the customer simply keeps its previous seat/product rows).
    private async Task SyncCustomerEntitlementsAsync(
        GChannelDbContext db, GoogleChannelClient client, string customerId, string? owningLinkId,
        DateTimeOffset now, CancellationToken ct)
    {
        IReadOnlyList<Entitlement> entitlements;
        try
        {
            var result = await client.ListEntitlementsForSyncAsync(customerId, ct);
            entitlements = result.Entitlements;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Read-model entitlement sync skipped for customer {Customer}.", customerId);
            return;
        }

        var seen = entitlements.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stored = await db.EntitlementRecords
            .Where(r => r.CustomerId == customerId)
            .ToListAsync(ct);
        var byId = stored.ToDictionary(r => r.EntitlementId, StringComparer.OrdinalIgnoreCase);

        long activeSeats = 0;
        foreach (var e in entitlements)
        {
            var seats = SeatsOf(e);
            var isActive = string.Equals(e.ProvisioningState, "ACTIVE", StringComparison.OrdinalIgnoreCase);
            if (isActive) { activeSeats += seats; }

            if (!byId.TryGetValue(e.Id, out var row))
            {
                row = new EntitlementRecord { EntitlementId = e.Id };
                db.EntitlementRecords.Add(row);
            }
            row.CustomerId = customerId;
            row.OwningLinkId = owningLinkId;
            row.ProductId = e.ProductId;
            row.SkuId = e.SkuId;
            row.OfferId = e.OfferId;
            row.State = e.ProvisioningState ?? "UNSPECIFIED";
            row.Seats = seats;
            row.IsTrial = e.IsTrial;
            row.LastSyncedUtc = now;
            row.IsDeleted = false;
        }

        foreach (var row in stored.Where(r => !seen.Contains(r.EntitlementId) && !r.IsDeleted))
        {
            row.IsDeleted = true;
        }

        var customer = await db.CustomerRecords.FindAsync([customerId], ct);
        if (customer is not null)
        {
            customer.SeatCount = activeSeats;
        }

        await db.SaveChangesAsync(ct);
    }

    private static long SeatsOf(Entitlement e)
    {
        var raw = e.Parameters.FirstOrDefault(p =>
            string.Equals(p.Name, "num_units", StringComparison.OrdinalIgnoreCase))?.Value;
        return long.TryParse(raw, out var n) ? n : 0;
    }
}
