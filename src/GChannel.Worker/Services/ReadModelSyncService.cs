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

                var cycleStart = Stopwatch.GetTimestamp();
                await RunCycleAsync(client, opts, stoppingToken);

                // Cooldown re-arm: a heavy cycle can outlast the lock's interval TTL, which would let
                // the next tick (or another replica) start back-to-back and saturate the shared
                // Channel API quota — starving interactive calls. If the cycle ran at least a full
                // interval, hold the lock for one more interval so there is a real gap before the
                // next sync.
                if (!stoppingToken.IsCancellationRequested
                    && Stopwatch.GetElapsedTime(cycleStart) >= interval)
                {
                    await db.StringSetAsync(SyncLockKey, Environment.MachineName, interval);
                }
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
        var now = DateTimeOffset.UtcNow;
        // Each save-unit below runs on its OWN short-lived DbContext (via WithDbAsync). A single
        // context shared across the whole multi-minute cycle accumulated thousands of tracked entities
        // (every direct customer + link + fanned-out customer + entitlement); a cancelled or failed
        // SaveChanges left a Detached entry that poisoned every subsequent save with
        // "Unexpected entry.EntityState: Detached", aborting the cycle and bloating memory. Fresh
        // per-unit contexts keep the tracker tiny and isolate failures.

        // §11 pricing + display names: resolve the account's offer list once per cycle (one quota-light
        // offers.list pass, a different quota bucket from customers/entitlements). Yields per-offer
        // wholesale price plus offer/SKU display names, all denormalised onto each entitlement so the
        // entitlement list and dashboard cost roll-up render from SQL with no live catalog fan-out.
        var offerCatalog = await BuildOfferCatalogAsync(client, ct);

        // Friendly product display names, resolved once per cycle (one quota-light products.list pass,
        // a different quota bucket from customers/entitlements) and denormalised onto each entitlement
        // so the dashboard product-mix renders from SQL without any live catalog call.
        var productNames = await BuildProductNamesAsync(client, ct);

        // Direct customers — refreshed every cycle (one cheap ListCustomers pass). Isolated so a failure
        // here (e.g. a ListCustomers 429 or a transient SaveChanges fault) does NOT abort the indirect
        // fan-out below: the reseller-owned estate must keep syncing even if the direct pass hiccups.
        // NOTE: only customer *metadata* is upserted here — entitlement syncing happens once, in a
        // single staleness-rotated pass at the end of the cycle (see below), so the contended
        // ListEntitlements quota is shared fairly across the whole estate instead of being drained by
        // the direct customers before the indirect fan-out ever runs.
        var directCount = 0;
        try
        {
            var direct = await client.ListCustomersAsync(ct);
            directCount = direct.Customers.Count;
            await WithDbAsync(db => UpsertCustomersAsync(db, direct.Customers, owningLinkId: null, now, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Read-model direct-customer sync failed this cycle; continuing with the indirect fan-out.");
        }

        // Link roster — refreshed every cycle (account-level, quota-light).
        var links = await client.ListChannelPartnerLinksAsync(ct);
        await WithDbAsync(db => UpsertLinksAsync(db, links.Links, now, ct));

        // Pick the stalest ACTIVE links and refresh their downstream customers, capped per cycle so we
        // stay within the ListCustomers quota; the whole estate is covered over several cycles. Only
        // customer *metadata* and the link's customer count are written here — entitlements follow in
        // the unified pass below. This means partner-link customer counts and the indirect estate
        // populate as soon as a link is fanned out, independent of the (slower, contended) entitlement
        // quota.
        var activeIds = links.Links
            .Where(l => string.Equals(l.LinkState, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var stalest = await WithDbAsync(db => db.ResellerLinks
            .Where(r => activeIds.Contains(r.LinkId))
            .OrderBy(r => r.LastSyncedUtc)
            .Take(Math.Max(1, opts.ReadModelLinksPerCycle))
            .Select(r => r.LinkId)
            .ToListAsync(ct));

        foreach (var linkId in stalest)
        {
            try
            {
                var customers = await client.ListChannelPartnerCustomersAsync(linkId, ct);
                await WithDbAsync(async db =>
                {
                    await UpsertCustomersAsync(db, customers.Customers, owningLinkId: linkId, now, ct);
                    var link = await db.ResellerLinks.FindAsync([linkId], ct);
                    if (link is not null)
                    {
                        link.CustomerCount = customers.Customers.Count;
                        link.LastSyncedUtc = DateTimeOffset.UtcNow;
                        link.SyncError = null;
                        await db.SaveChangesAsync(ct);
                    }
                });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Record the failure on a FRESH context — the one that threw may have a poisoned
                // change tracker, so reusing it would re-throw and abort the entire cycle.
                try
                {
                    await WithDbAsync(async db =>
                    {
                        var link = await db.ResellerLinks.FindAsync([linkId], ct);
                        if (link is not null)
                        {
                            link.LastSyncedUtc = DateTimeOffset.UtcNow;
                            var detail = Flatten(ex);
                            link.SyncError = detail.Length > 500 ? detail[..500] : detail;
                            await db.SaveChangesAsync(ct);
                        }
                    });
                }
                catch (Exception saveEx)
                {
                    logger.LogWarning(saveEx, "Failed to record sync error for link {Link}.", linkId);
                }
                logger.LogWarning(ex, "Read-model sync failed for link {Link}.", linkId);
            }
        }

        // Unified entitlement sync pass — the single consumer of the contended ListEntitlements quota.
        // Refresh the stalest customers across the WHOLE estate (direct + indirect), capped per cycle,
        // so quota is shared fairly and every part of the estate progresses each cycle. Each customer's
        // LastSyncedUtc is stamped after its entitlements are synced (or skipped), so the rotation
        // advances and a single throttled customer can't block the queue.
        var due = await WithDbAsync(db => db.CustomerRecords
            .Where(c => !c.IsDeleted)
            .OrderBy(c => c.LastSyncedUtc)
            .Take(Math.Max(1, opts.ReadModelCustomersPerCycle))
            .Select(c => new { c.CustomerId, c.OwningLinkId })
            .ToListAsync(ct));

        // §6 reseller-wide mark-up is resolved once per link (a channel-partner-granularity repricing
        // config applies to every downstream entitlement under the link with no per-entitlement
        // override). Cache per cycle so a link shared by many customers in this slice is fetched once.
        var linkPercentCache = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var pricingDiag = new PricingDiagnostics();
        var syncedCount = 0;
        foreach (var c in due)
        {
            var linkFallbackPercent = 0m;
            if (!string.IsNullOrEmpty(c.OwningLinkId))
            {
                if (!linkPercentCache.TryGetValue(c.OwningLinkId, out linkFallbackPercent))
                {
                    linkFallbackPercent = await ResolveChannelPartnerPercentAsync(client, c.OwningLinkId, now, ct);
                    linkPercentCache[c.OwningLinkId] = linkFallbackPercent;
                }
            }

            await WithDbAsync(db => SyncCustomerEntitlementsAsync(
                db, client, c.CustomerId, c.OwningLinkId, offerCatalog, productNames, linkFallbackPercent, pricingDiag, now, ct));
            syncedCount++;
        }

        // Surface WHY active entitlements went unpriced this cycle so unmatched offers can be chased down.
        if (pricingDiag.ActiveUnpriced > 0)
        {
            static string Sample(Dictionary<string, int> d) => d.Count == 0
                ? "none"
                : string.Join(", ", d.OrderByDescending(kv => kv.Value).Take(15).Select(kv => $"{kv.Key}\u00d7{kv.Value}"));
            logger.LogWarning(
                "Read-model pricing this cycle: {Priced} active priced, {Unpriced} active unpriced — " +
                "{Missing} distinct offer(s) not in offers.list, {NoPrice} listed offer(s) without a SEAT/base price, " +
                "{NoId} active entitlement(s) with no offer id. Not-in-catalog: [{MissingSample}]. Listed-without-price: [{NoPriceSample}].",
                pricingDiag.ActivePriced, pricingDiag.ActiveUnpriced,
                pricingDiag.MissingFromCatalog.Count, pricingDiag.ListedWithoutPrice.Count, pricingDiag.ActiveWithoutOfferId,
                Sample(pricingDiag.MissingFromCatalog), Sample(pricingDiag.ListedWithoutPrice));
        }

        await WithDbAsync(async db =>
        {
            var cursor = await db.SyncCursors.FindAsync(["links"], ct) ?? new SyncCursor { Scope = "links" };
            if (db.Entry(cursor).State == EntityState.Detached)
            {
                db.SyncCursors.Add(cursor);
            }
            cursor.LastCycleUtc = DateTimeOffset.UtcNow;
            if (stalest.Count >= activeIds.Count) { cursor.LastFullPassUtc = DateTimeOffset.UtcNow; }
            await db.SaveChangesAsync(ct);
        });

        logger.LogInformation(
            "Read-model cycle: {Direct} direct customers, {Links} links, refreshed {Stale} reseller(s), synced {Synced} customer entitlement set(s) in {Secs}s.",
            directCount, links.Links.Count, stalest.Count, syncedCount,
            (int)Stopwatch.GetElapsedTime(startedAt).TotalSeconds);
    }

    // Runs a unit of read-model work on a fresh, short-lived DbContext scope so each save batch has
    // its own small change tracker — a failure (or cancellation) in one unit can't corrupt or abort
    // the rest of the cycle, and memory stays bounded over a long sync pass.
    private async Task WithDbAsync(Func<GChannelDbContext, Task> work)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GChannelDbContext>();
        await work(db);
    }

    private async Task<T> WithDbAsync<T>(Func<GChannelDbContext, Task<T>> work)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GChannelDbContext>();
        return await work(db);
    }

    // Flattens an exception chain into a single string so a recorded SyncError shows the actual cause
    // (e.g. the SQL duplicate-key detail) instead of EF's opaque outer "See the inner exception" wrapper.
    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (!parts.Contains(e.Message))
            {
                parts.Add(e.Message);
            }
        }
        return string.Join(" -> ", parts);
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
                existing[l.Id] = row; // guard against duplicate ids in the same list (would double-add the PK)
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
    // Only customer *metadata* is written here; LastSyncedUtc tracks when the customer's ENTITLEMENTS
    // were last synced (stamped by the unified entitlement pass), so it is left untouched for existing
    // rows and seeded to MinValue for new rows — putting never-synced customers at the head of the
    // entitlement rotation queue.
    private static async Task UpsertCustomersAsync(
        GChannelDbContext db, IReadOnlyList<Customer> customers, string? owningLinkId, DateTimeOffset now, CancellationToken ct)
    {
        var seen = customers.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stored = await db.CustomerRecords
            .Where(r => r.OwningLinkId == owningLinkId)
            .ToListAsync(ct);
        var byId = stored.ToDictionary(r => r.CustomerId, StringComparer.OrdinalIgnoreCase);

        // CustomerId is the primary key, but a customer can move between owners — a reseller transfer,
        // or a customer that turns up under a different link than before (or direct ↔ indirect). Such a
        // row already exists under a DIFFERENT OwningLinkId and is NOT in the owner-scoped query above,
        // so a naive Add() would insert a duplicate PK and throw a DbUpdateException that fails the whole
        // link fan-out (0 customers, no indirect resellers). Fetch any colliding rows and re-home them
        // in place instead of inserting.
        var incomingIds = customers.Select(c => c.Id).ToList();
        var reHomed = await db.CustomerRecords
            .Where(r => r.OwningLinkId != owningLinkId && incomingIds.Contains(r.CustomerId))
            .ToListAsync(ct);
        foreach (var r in reHomed)
        {
            byId[r.CustomerId] = r;
        }

        foreach (var c in customers)
        {
            if (!byId.TryGetValue(c.Id, out var row))
            {
                row = new CustomerRecord { CustomerId = c.Id, LastSyncedUtc = DateTimeOffset.MinValue };
                db.CustomerRecords.Add(row);
                byId[c.Id] = row; // guard against duplicate ids in the same list (would double-add the PK)
            }
            row.OrgName = c.OrgDisplayName;
            row.Domain = c.Domain;
            row.CloudIdentityId = c.CloudIdentityId;
            row.OwningLinkId = owningLinkId;
            row.CreateTime = c.CreateTime;
            row.IsDeleted = false;
            // LastSyncedUtc intentionally NOT reset here — it reflects entitlement-sync freshness.
        }

        foreach (var row in stored.Where(r => !seen.Contains(r.CustomerId) && !r.IsDeleted))
        {
            row.IsDeleted = true;
        }

        await db.SaveChangesAsync(ct);
    }

    // Upserts one customer's entitlements, soft-deletes vanished ones, and denormalises the customer's
    // active seat total onto CustomerRecord for fast reseller ranking. Tolerates a single customer's
    // read failing (the customer simply keeps its previous seat/product rows). Also denormalises §11
    // pricing (offer wholesale price) and the §6 repricing mark-up onto each entitlement so the
    // dashboard can roll up estimated cost/revenue/margin from SQL without any live API fan-out.
    private async Task SyncCustomerEntitlementsAsync(
        GChannelDbContext db, GoogleChannelClient client, string customerId, string? owningLinkId,
        OfferCatalog offerCatalog,
        IReadOnlyDictionary<string, string> productNames, decimal linkFallbackPercent,
        PricingDiagnostics diag,
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
            // Stamp the customer as visited so the staleness rotation advances past a throttled customer
            // instead of retrying it ahead of everyone else next cycle (keeps the queue moving).
            var skipped = await db.CustomerRecords.FindAsync([customerId], ct);
            if (skipped is not null)
            {
                skipped.LastSyncedUtc = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            return;
        }

        // Per-entitlement repricing overrides for this customer (best-effort; a failure just falls back
        // to the link-wide percent). Keyed by entitlement id.
        var entitlementPercents = await ResolveCustomerEntitlementPercentsAsync(client, customerId, now, ct);

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
                byId[e.Id] = row; // guard against duplicate ids in the same list (would double-add the PK)
            }
            row.CustomerId = customerId;
            row.OwningLinkId = owningLinkId;
            row.ProductId = e.ProductId;
            row.ProductName = e.ProductId is not null && productNames.TryGetValue(e.ProductId, out var pn) ? pn : null;
            row.SkuId = e.SkuId;
            row.SkuName = e.SkuId is not null && offerCatalog.SkuNames.TryGetValue(e.SkuId, out var sn) ? sn : null;
            row.OfferId = e.OfferId;
            row.OfferName = e.OfferId is not null && offerCatalog.OfferNames.TryGetValue(e.OfferId, out var on) ? on : null;
            row.State = e.ProvisioningState ?? "UNSPECIFIED";
            row.Seats = seats;
            row.IsTrial = e.IsTrial;
            row.CreateTime = e.CreateTime;
            row.CommitmentEndTime = e.Commitment?.EndTime;
            row.PlanDescription = BuildPlanDescription(
                e.Commitment,
                e.OfferId is not null && offerCatalog.Plans.TryGetValue(e.OfferId, out var plan) ? plan : default);
            if (e.OfferId is not null && offerCatalog.Pricing.TryGetValue(e.OfferId, out var price))
            {
                row.UnitPrice = price.Unit;
                row.Currency = price.Currency;
                if (isActive) { diag.ActivePriced++; }
            }
            else
            {
                row.UnitPrice = 0m;
                row.Currency = null;
                if (isActive)
                {
                    diag.ActiveUnpriced++;
                    if (e.OfferId is null)
                    {
                        diag.ActiveWithoutOfferId++;
                    }
                    else if (offerCatalog.OfferNames.ContainsKey(e.OfferId))
                    {
                        // Offer IS in the account's offers.list but exposes no SEAT/base price.
                        diag.ListedWithoutPrice[e.OfferId] = diag.ListedWithoutPrice.GetValueOrDefault(e.OfferId) + 1;
                    }
                    else
                    {
                        // Offer the entitlement references is absent from the account's offers.list (churn/legacy/sub-reseller).
                        diag.MissingFromCatalog[e.OfferId] = diag.MissingFromCatalog.GetValueOrDefault(e.OfferId) + 1;
                    }
                }
            }
            // Per-entitlement override wins; otherwise the link-wide channel-partner mark-up (0 for direct).
            row.RepricingPercent = entitlementPercents.TryGetValue(e.Id, out var pct) ? pct : linkFallbackPercent;
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
            // Stamp entitlement-sync freshness so the staleness rotation advances to the next customer.
            customer.LastSyncedUtc = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    private static long SeatsOf(Entitlement e)
    {
        var raw = e.Parameters.FirstOrDefault(p =>
            string.Equals(p.Name, "num_units", StringComparison.OrdinalIgnoreCase))?.Value;
        return long.TryParse(raw, out var n) ? n : 0;
    }

    // §11: build an offerId → (effective unit price, currency) lookup from the account's sellable
    // offers. SEAT pricing is preferred (the seat count we store multiplies against it); otherwise the
    // first priced resource is used. Best-effort: any failure yields an empty lookup (entitlements then
    // cost as 0 and are reported as "unpriced"). The same offers.list pass also yields offer/SKU display
    // names (a CatalogOffer carries both), so the entitlement list renders friendly names from SQL.
    private async Task<OfferCatalog> BuildOfferCatalogAsync(
        GoogleChannelClient client, CancellationToken ct)
    {
        var pricing = new Dictionary<string, (decimal, string)>(StringComparer.OrdinalIgnoreCase);
        var offerNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var skuNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var plans = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var offers = await client.ListOffersAsync(ct);
            foreach (var offer in offers.Offers)
            {
                if (!string.IsNullOrEmpty(offer.OfferId))
                {
                    if (!string.IsNullOrWhiteSpace(offer.DisplayName))
                    {
                        offerNames[offer.OfferId] = offer.DisplayName!;
                    }
                    if (!string.IsNullOrWhiteSpace(offer.PaymentPlan) || !string.IsNullOrWhiteSpace(offer.PaymentCycle))
                    {
                        plans[offer.OfferId] = (offer.PaymentPlan, offer.PaymentCycle);
                    }
                    if (offer.Pricing.Count > 0)
                    {
                        var seat = offer.Pricing.FirstOrDefault(p =>
                            string.Equals(p.ResourceType, "SEAT", StringComparison.OrdinalIgnoreCase)) ?? offer.Pricing[0];
                        var money = seat.EffectivePrice ?? seat.BasePrice;
                        if (money is not null)
                        {
                            pricing[offer.OfferId] = (money.Value, money.CurrencyCode);
                        }
                    }
                }
                if (!string.IsNullOrEmpty(offer.SkuId) && !string.IsNullOrWhiteSpace(offer.SkuDisplayName))
                {
                    skuNames[offer.SkuId] = offer.SkuDisplayName!;
                }
            }

            // Catalog-size telemetry: if {Priced} is far below {Offers} (or {Offers} is small), the
            // account's offers.list itself lacks priced offers — the root cause of "unpriced" estate rows.
            logger.LogInformation(
                "Read-model offer catalog: {Offers} offer(s) listed, {Priced} with wholesale pricing, {Named} with display names.",
                offers.Offers.Count, pricing.Count, offerNames.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Read-model offer catalog lookup failed this cycle; entitlements will be reported as unpriced/unnamed.");
        }
        return new OfferCatalog(pricing, offerNames, skuNames, plans);
    }

    /// <summary>Per-cycle offer catalog: wholesale pricing plus offer/SKU display names, all keyed by id.</summary>
    private readonly record struct OfferCatalog(
        IReadOnlyDictionary<string, (decimal Unit, string Currency)> Pricing,
        IReadOnlyDictionary<string, string> OfferNames,
        IReadOnlyDictionary<string, string> SkuNames,
        IReadOnlyDictionary<string, (string? PaymentPlan, string? PaymentCycle)> Plans);

    /// <summary>
    /// Accumulates, across one sync cycle's entitlement pass, why ACTIVE entitlements end up unpriced so
    /// the cause can be diagnosed from logs: offers absent from the account's offers.list vs offers that
    /// are listed but carry no SEAT/base price vs entitlements with no offer id at all.
    /// </summary>
    private sealed class PricingDiagnostics
    {
        public int ActivePriced;
        public int ActiveUnpriced;
        public int ActiveWithoutOfferId;
        public readonly Dictionary<string, int> MissingFromCatalog = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> ListedWithoutPrice = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Builds a human-friendly plan summary (e.g. "Annual Plan (Monthly Payment)") from the entitlement's
    /// commitment term and the offer's payment plan/cycle. The commitment term (Annual/Monthly/N-Year)
    /// is derived from the commitment start→end span when present; otherwise the offer's payment plan
    /// (Commitment/Flexible/…) is used. Returns null when nothing is known.
    /// </summary>
    private static string? BuildPlanDescription(EntitlementCommitment? commitment, (string? PaymentPlan, string? PaymentCycle) plan)
    {
        string? term = null;
        if (commitment?.StartTime is { } start && commitment.EndTime is { } end && end > start)
        {
            var months = (int)Math.Round((end - start).TotalDays / 30.44, MidpointRounding.AwayFromZero);
            term = months switch
            {
                <= 0 => null,
                1 => "Monthly",
                12 => "Annual",
                _ when months % 12 == 0 => $"{months / 12}-Year",
                _ => $"{months}-Month",
            };
        }

        term ??= plan.PaymentPlan?.ToUpperInvariant() switch
        {
            "COMMITMENT" => "Commitment",
            "FLEXIBLE" => "Flexible",
            "TRIAL" => "Trial",
            "FREE" => "Free",
            "OFFLINE" => "Offline",
            _ => null,
        };

        var payment = string.IsNullOrWhiteSpace(plan.PaymentCycle) ? null : plan.PaymentCycle;
        return (term, payment) switch
        {
            (not null, not null) => $"{term} Plan ({payment} Payment)",
            (not null, null) => $"{term} Plan",
            (null, not null) => $"{payment} Payment",
            _ => null,
        };
    }

    // Friendly product display names keyed by product id (one quota-light products.list pass). Best-effort.
    private async Task<IReadOnlyDictionary<string, string>> BuildProductNamesAsync(
        GoogleChannelClient client, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var products = await client.ListProductsAsync(ct);
            foreach (var p in products.Products)
            {
                if (!string.IsNullOrEmpty(p.Id) && !string.IsNullOrWhiteSpace(p.DisplayName))
                {
                    map[p.Id] = p.DisplayName!;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Read-model product-name lookup failed this cycle; product mix will fall back to product ids.");
        }
        return map;
    }

    // §6: per-entitlement repricing mark-ups for one customer, keyed by entitlement id. Best-effort.
    private async Task<IReadOnlyDictionary<string, decimal>> ResolveCustomerEntitlementPercentsAsync(
        GoogleChannelClient client, string customerId, DateTimeOffset now, CancellationToken ct)
    {
        var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var configs = await client.ListCustomerRepricingConfigsAsync(customerId, ct);
            foreach (var group in configs.Configs
                .Where(c => !string.IsNullOrEmpty(c.EntitlementId))
                .GroupBy(c => c.EntitlementId!, StringComparer.OrdinalIgnoreCase))
            {
                var effective = SelectEffectiveConfig(group, now);
                if (effective is not null)
                {
                    map[group.Key] = effective.PercentageAdjustment;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Read-model customer repricing lookup skipped for customer {Customer}.", customerId);
        }
        return map;
    }

    // §6: the reseller-wide (channel-partner granularity) mark-up for a link, or 0 when none applies.
    private async Task<decimal> ResolveChannelPartnerPercentAsync(
        GoogleChannelClient client, string linkId, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            var configs = await client.ListChannelPartnerRepricingConfigsAsync(linkId, ct);
            var partnerConfigs = configs.Configs.Where(c =>
                string.Equals(c.Granularity, RepricingGranularities.ChannelPartner, StringComparison.OrdinalIgnoreCase));
            return SelectEffectiveConfig(partnerConfigs, now)?.PercentageAdjustment ?? 0m;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Read-model channel-partner repricing lookup skipped for link {Link}.", linkId);
            return 0m;
        }
    }

    // Picks the config currently in force: the latest whose effective invoice month is on or before the
    // current month, else the earliest future one (so a freshly-created future config still previews).
    private static RepricingConfig? SelectEffectiveConfig(IEnumerable<RepricingConfig> configs, DateTimeOffset now)
    {
        var currentKey = (now.Year * 12) + now.Month;
        RepricingConfig? best = null;
        var bestKey = int.MinValue;
        RepricingConfig? earliestFuture = null;
        var earliestFutureKey = int.MaxValue;
        foreach (var c in configs)
        {
            var key = (c.EffectiveInvoiceYear * 12) + c.EffectiveInvoiceMonth;
            if (key <= currentKey)
            {
                if (key > bestKey) { bestKey = key; best = c; }
            }
            else if (key < earliestFutureKey)
            {
                earliestFutureKey = key; earliestFuture = c;
            }
        }
        return best ?? earliestFuture;
    }
}
