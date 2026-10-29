using GChannel.Core.Configuration;
using GChannel.Core.Estate;
using GChannel.Core.Estate.ReadModels;
using GChannel.Core.GoogleApis;
using GChannel.Core.Messaging;
using GChannel.Core.Persistence.Documents;
using GChannel.Shared.Contracts;
using GChannel.Worker.Infrastructure;
using Marten;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Runtime;

namespace GChannel.Worker.Handlers;

/// <summary>
/// One rolling estate sync cycle (the <c>RunEstateSyncCycle</c> recurring tick). Materialises the estate —
/// channel partner links, direct and indirect customers, and a staleness-rotated slice of entitlements —
/// into the Marten event streams through the <see cref="EstateObserver"/>, so the dashboard / estate views
/// read durable projections instead of a live Channel API fan-out. Each cycle refreshes the direct customers
/// and the link roster, then a budgeted slice of the stalest links' downstream customers, then a single
/// unified entitlement pass over the stalest customers of the WHOLE estate — so one cycle stays within the
/// per-minute quotas regardless of estate size (a "rolling refresh").
/// </summary>
public sealed class EstateSyncHandler(
    IOptions<GoogleChannelOptions> options,
    ServiceAccountChannelClientProvider clients,
    EstateObserver observer,
    IDocumentStore store,
    IDistributedCache cache,
    IWolverineRuntime runtime,
    RecurringJobCatalog catalog,
    ILogger<EstateSyncHandler> logger)
{
    // A rolling cycle is paced by the Channel API per-minute quotas (and stalls further on 429 cooldowns), so a
    // large estate legitimately needs well over Wolverine's 60s default execution budget.
    [MessageTimeout(RecurringJobs.EstateSyncBudgetSeconds)]
    public async Task<OutgoingMessages> Handle(RunEstateSyncCycle tick, IDocumentSession session, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        await RecurringJobRunner.RunAsync(tick, catalog[RecurringJobs.EstateSync], session, outgoing, logger, RunCycleAsync, ct);
        return outgoing;
    }

    /// <summary>Token-bucket-of-1 spacing this cycle's <c>ListCustomers</c> calls (direct list + per-link fan-out share one quota bucket).</summary>
    private sealed class CustomerListPacer(TimeSpan interval)
    {
        private DateTimeOffset _nextSlot = DateTimeOffset.MinValue;

        public Task WaitAsync(CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            var slot = _nextSlot > now ? _nextSlot : now;
            _nextSlot = slot + interval;
            var delay = slot - now;
            return delay > TimeSpan.Zero ? Task.Delay(delay, cancellationToken) : Task.CompletedTask;
        }
    }

    private async Task<string?> RunCycleAsync(CancellationToken ct)
    {
        var client = clients.Client;
        if (client is null)
        {
            return "No service-account credential configured.";
        }

        var opts = options.Value;
        var startedAt = Stopwatch.GetTimestamp();
        var startedUtc = DateTimeOffset.UtcNow;
        var now = startedUtc;
        var bus = new MessageBus(runtime);

        await WriteDashboardStatusAsync(running: true, startedUtc, null, null, ct);

        var customerListPacer = opts.DashboardCustomerListRequestsPerMinute > 0
            ? new CustomerListPacer(TimeSpan.FromSeconds(60.0 / opts.DashboardCustomerListRequestsPerMinute))
            : null;

        // Pricing + display names: resolve the account's offer list and product list once per cycle (quota-light,
        // separate buckets from customers/entitlements) and denormalise them onto each entitlement.
        var offerCatalog = await BuildOfferCatalogAsync(client, ct);
        var productNames = await BuildProductNamesAsync(client, ct);

        // 1) Direct customers — metadata only; entitlements follow in the unified pass. Isolated so a failure
        //    here never aborts the indirect fan-out.
        var directCount = 0;
        try
        {
            if (customerListPacer is not null) { await customerListPacer.WaitAsync(ct); }
            var direct = await client.ListCustomersAsync(ct);
            directCount = direct.Customers.Count;
            await WithSessionAsync(s => observer.ObserveCustomerListAsync(s, direct.Customers, owningLinkId: null, ObservationSource.Sync, now, ct), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Direct-customer sync failed this cycle; continuing with the indirect fan-out.");
        }

        // 2) Link roster — account-level, quota-light.
        var links = await client.ListChannelPartnerLinksAsync(ct);
        await WithSessionAsync(s => observer.ObserveLinksAsync(s, links.Links, ObservationSource.Sync, now, ct), ct);

        // 3) Stalest ACTIVE links → their downstream customers (metadata + the link's customer count).
        var activeIds = links.Links
            .Where(l => string.Equals(l.LinkState, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<string> stalest;
        await using (var s = store.QuerySession())
        {
            var states = await s.Query<LinkSyncState>().ToListAsync(ct);
            var byId = states.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
            stalest = activeIds
                .OrderBy(id => byId.TryGetValue(id, out var st) ? st.LastSyncedUtc : DateTimeOffset.MinValue)
                .Take(Math.Max(1, opts.ReadModelLinksPerCycle))
                .ToList();
        }

        foreach (var linkId in stalest)
        {
            try
            {
                if (customerListPacer is not null) { await customerListPacer.WaitAsync(ct); }
                var customers = await client.ListChannelPartnerCustomersAsync(linkId, ct);
                await WithSessionAsync(async s =>
                {
                    await observer.ObserveCustomerListAsync(s, customers.Customers, linkId, ObservationSource.Sync, now, ct);
                    var state = await s.LoadAsync<LinkSyncState>(linkId, ct) ?? new LinkSyncState { Id = linkId };
                    state.CustomerCount = customers.Customers.Count;
                    state.LastSyncedUtc = DateTimeOffset.UtcNow;
                    state.SyncError = null;
                    s.Store(state);
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                try
                {
                    await WithSessionAsync(async s =>
                    {
                        var state = await s.LoadAsync<LinkSyncState>(linkId, ct) ?? new LinkSyncState { Id = linkId };
                        state.LastSyncedUtc = DateTimeOffset.UtcNow;
                        var detail = EstateObserver.Flatten(ex);
                        state.SyncError = detail.Length > 500 ? detail[..500] : detail;
                        s.Store(state);
                    }, ct);
                }
                catch (Exception recordEx)
                {
                    logger.LogDebug(recordEx, "Could not record the fan-out error for link {Link}.", linkId);
                }
                logger.LogWarning(ex, "Partner customer fan-out failed for link {Link}; recorded and continuing.", linkId);
            }
        }

        // Reseller-self classification (a customer whose Cloud Identity / domain matches a link is one of OUR
        // resellers buying for its own use — a direct sale).
        try
        {
            await WithSessionAsync(async s =>
            {
                var (total, direct) = await observer.ClassifyCustomersAsync(s, ct);
                logger.LogInformation("Reseller-self classification: {Total} customers; {Direct} reseller self-purchase (DIRECT), {Indirect} end customers (INDIRECT).",
                    total, direct, total - direct);
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reseller-self classification failed this cycle.");
        }

        // 4) Unified entitlement pass — the single consumer of the contended ListEntitlements quota. Refresh the
        //    stalest customers across the WHOLE estate (direct + indirect), capped per cycle.
        IReadOnlyList<CustomerSyncState> due;
        await using (var s = store.QuerySession())
        {
            due = await s.Query<CustomerSyncState>()
                .Where(c => !c.IsDeleted)
                .OrderBy(c => c.LastSyncedUtc)
                .Take(Math.Max(1, opts.ReadModelCustomersPerCycle))
                .ToListAsync(ct);
        }

        var linkPercentCache = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var diag = new EstateObserver.PricingDiagnostics();
        var syncedCount = 0;
        foreach (var c in due)
        {
            var linkFallbackPercent = 0m;
            if (!string.IsNullOrEmpty(c.OwningLinkId))
            {
                if (!linkPercentCache.TryGetValue(c.OwningLinkId, out linkFallbackPercent))
                {
                    linkFallbackPercent = await observer.ResolveChannelPartnerPercentAsync(client, c.OwningLinkId, now, ct);
                    linkPercentCache[c.OwningLinkId] = linkFallbackPercent;
                }
            }

            await WithSessionAsync(s => observer.SyncCustomerEntitlementsAsync(
                s, client, c.Id, c.OwningLinkId, offerCatalog, productNames, linkFallbackPercent, diag, ObservationSource.Sync, now, ct), ct);
            syncedCount++;

            // Let the API drop its Redis read cache for this customer (its live-cached entitlement list may be stale).
            try { await bus.PublishAsync(new CustomerReadModelUpdated(c.Id, c.OwningLinkId, "sync")); }
            catch (Exception ex) { logger.LogDebug(ex, "Could not publish CustomerReadModelUpdated for {Customer}.", c.Id); }

            if (diag.RateLimited)
            {
                logger.LogWarning("Channel API quota (429) reached during entitlement sync; stopping early after {Synced} customer(s) — the rest refresh next cycle.", syncedCount);
                break;
            }
        }

        LogPricingDiagnostics(diag);

        await WithSessionAsync(async s =>
        {
            var cursor = await s.LoadAsync<SyncCursor>("links", ct) ?? new SyncCursor { Id = "links" };
            cursor.LastCycleUtc = DateTimeOffset.UtcNow;
            if (stalest.Count >= activeIds.Count) { cursor.LastFullPassUtc = DateTimeOffset.UtcNow; }
            s.Store(cursor);
        }, ct);

        // The read-model dashboard is recomputed on demand (short cache); drop the cached copies so the next
        // page load reflects this cycle, and record the refresh status for the home page.
        await cache.RemoveAsync("dashboard:summary:live", ct);
        await cache.RemoveAsync("dashboard:overview:live", ct);
        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        await WriteDashboardStatusAsync(running: false, startedUtc, DateTimeOffset.UtcNow, (int)Math.Round(elapsed.TotalSeconds), ct);

        logger.LogInformation(
            "Estate sync cycle: {Direct} direct customers, {Links} links, refreshed {Stale} reseller(s), synced {Synced} customer entitlement set(s) in {Secs}s.",
            directCount, links.Links.Count, stalest.Count, syncedCount, (int)elapsed.TotalSeconds);
        return null;
    }

    private async Task WriteDashboardStatusAsync(bool running, DateTimeOffset startedUtc, DateTimeOffset? completedUtc, int? durationSeconds, CancellationToken ct)
    {
        try
        {
            var interval = TimeSpan.FromSeconds(Math.Max(30, options.Value.BackgroundRefreshSeconds));
            await using var s = store.LightweightSession();
            var state = await s.LoadAsync<DashboardRefreshState>(DashboardRefreshState.SingletonId, ct) ?? new DashboardRefreshState();
            var previous = state.Status;
            state.Status = new DashboardRefreshStatus
            {
                Enabled = true,
                IsRunning = running,
                LastStartedUtc = startedUtc,
                LastCompletedUtc = completedUtc ?? previous.LastCompletedUtc,
                LastDurationSeconds = durationSeconds ?? previous.LastDurationSeconds,
                LastSkippedCount = 0,
                NextRefreshUtc = (completedUtc ?? startedUtc) + interval,
            };
            s.Store(state);
            await s.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not write the dashboard refresh status.");
        }
    }

    // Each unit of work runs on its own short-lived session so a failure is isolated and memory stays bounded.
    private async Task WithSessionAsync(Func<IDocumentSession, Task> work, CancellationToken ct)
    {
        await using var session = store.LightweightSession();
        await work(session);
        await session.SaveChangesAsync(ct);
    }

    private void LogPricingDiagnostics(EstateObserver.PricingDiagnostics diag)
    {
        if (diag.ActiveUnpriced > 0)
        {
            static string Sample(Dictionary<string, int> d) => d.Count == 0
                ? "none"
                : string.Join(", ", d.OrderByDescending(kv => kv.Value).Take(15).Select(kv => $"{kv.Key}\u00d7{kv.Value}"));
            logger.LogWarning(
                "Pricing this cycle: {Priced} active priced, {Unpriced} active unpriced — {Missing} distinct offer(s) not in offers.list, " +
                "{NoPrice} listed offer(s) without a SEAT/base price, {NoId} active entitlement(s) with no offer id. Not-in-catalog: [{MissingSample}]. Listed-without-price: [{NoPriceSample}].",
                diag.ActivePriced, diag.ActiveUnpriced, diag.MissingFromCatalog.Count, diag.ListedWithoutPrice.Count, diag.ActiveWithoutOfferId,
                Sample(diag.MissingFromCatalog), Sample(diag.ListedWithoutPrice));
        }

        if (diag.IonEnriched > 0 || diag.IonResellersMatched.Count > 0)
        {
            logger.LogInformation("Ion enrichment this cycle: {Enriched} entitlement(s) decorated with cost/margin across {Resellers} matched reseller(s).",
                diag.IonEnriched, diag.IonResellersMatched.Count);
        }

        if (diag.IonUnmatchedEntitlementSkus.Count > 0)
        {
            logger.LogInformation("Ion SKU-name gap: {Count} entitlement SKU(s) unmatched despite a priced reseller. Ours: [{Ours}]. Ion offers: [{Ion}].",
                diag.IonUnmatchedEntitlementSkus.Count,
                string.Join(", ", diag.IonUnmatchedEntitlementSkus.Take(20)),
                string.Join(", ", diag.IonAvailableSkuNames.Take(20)));
        }
    }

    /// <summary>offerId → (effective unit price, currency) + offer/SKU/product display names + plans from the sellable offer list. Best-effort.</summary>
    private async Task<EstateObserver.OfferCatalog> BuildOfferCatalogAsync(IGoogleChannelClient client, CancellationToken ct)
    {
        var pricing = new Dictionary<string, (decimal, string)>(StringComparer.OrdinalIgnoreCase);
        var offerNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var skuNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var productNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                if (!string.IsNullOrEmpty(offer.ProductId) && !string.IsNullOrWhiteSpace(offer.ProductDisplayName))
                {
                    productNames.TryAdd(offer.ProductId, offer.ProductDisplayName!);
                }
            }

            logger.LogInformation("Offer catalog: {Offers} offer(s) listed, {Priced} with wholesale pricing, {Named} with display names.",
                offers.Offers.Count, pricing.Count, offerNames.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Offer catalog lookup failed this cycle; entitlements will be reported as unpriced/unnamed.");
        }
        return new EstateObserver.OfferCatalog(pricing, offerNames, skuNames, plans, productNames);
    }

    private async Task<IReadOnlyDictionary<string, string>> BuildProductNamesAsync(IGoogleChannelClient client, CancellationToken ct)
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
            logger.LogWarning(ex, "Product-name lookup failed this cycle; product mix will fall back to product ids.");
        }
        return map;
    }
}
