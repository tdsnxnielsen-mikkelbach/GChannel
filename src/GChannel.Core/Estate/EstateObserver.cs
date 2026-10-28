using GChannel.Core.Estate.Events;
using GChannel.Core.Estate.ReadModels;
using GChannel.Core.GoogleApis;
using GChannel.Core.Ion;
using GChannel.Core.Persistence.Documents;
using GChannel.Shared.Contracts;
using Marten;
using Microsoft.Extensions.Logging;

namespace GChannel.Core.Estate;

/// <summary>
/// The estate <b>observer</b>: compares freshly fetched Channel API state with the projected read model
/// and appends the minimal set of events describing what changed (discovery, detail/ownership/seat/state
/// changes, removals, enrichment such as catalog names, pricing and Ion cost). Google remains the system
/// of record; the event streams are the app's durable, queryable history of what it observed and did.
/// <para>
/// Every method appends to / stores through the supplied <see cref="IDocumentSession"/> and leaves the
/// commit to the caller (a Wolverine handler's transactional middleware or an explicit
/// <c>SaveChangesAsync</c>), so one unit of work is one transaction. Observations are idempotent: a
/// pass that sees no difference appends nothing, so duplicate or out-of-order triggers converge.
/// </para>
/// </summary>
public sealed class EstateObserver(ILogger<EstateObserver> logger, IIonGatewayClient ion)
{
    /// <summary>An empty offer catalog: every entitlement resolves names/prices via the per-entitlement
    /// <c>lookupOffer</c> fallback (the same source the entitlement detail page uses).</summary>
    public static readonly OfferCatalog EmptyOfferCatalog = new(
        new Dictionary<string, (decimal, string)>(),
        new Dictionary<string, string>(),
        new Dictionary<string, string>(),
        new Dictionary<string, (string?, string?)>(),
        new Dictionary<string, string>());

    private static readonly IReadOnlyDictionary<string, string> EmptyNames = new Dictionary<string, string>();

    // -------------------------------------------------------------------------------------------------
    // Customers
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Observes one customer's metadata (from a list pass, a mutation result or a targeted refresh):
    /// starts the stream on first sight, otherwise appends detail / ownership / restore events only for
    /// what differs. Also upserts the customer's <see cref="CustomerSyncState"/> (new customers start at
    /// <c>LastSyncedUtc = MinValue</c> so the entitlement rotation picks them up first).
    /// </summary>
    public async Task<bool> ObserveCustomerAsync(
        IDocumentSession session, Customer customer, string? owningLinkId, ObservationSource source,
        DateTimeOffset now, CancellationToken ct)
    {
        var existing = await session.LoadAsync<CustomerRecord>(EstateStreams.Customer(customer.Id), ct);
        return await ObserveCustomerCoreAsync(session, existing, customer, owningLinkId, source, now, ct);
    }

    /// <summary>
    /// Observes a whole scope's customer list (the account's direct customers, or one channel partner
    /// link's customers): discovers/updates each listed customer, re-homes customers that moved between
    /// owners, and marks customers that vanished from this scope's list as removed.
    /// </summary>
    public async Task ObserveCustomerListAsync(
        IDocumentSession session, IReadOnlyList<Customer> customers, string? owningLinkId,
        ObservationSource source, DateTimeOffset now, CancellationToken ct)
    {
        var seen = customers.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stored = owningLinkId is null
            ? await session.Query<CustomerRecord>().Where(r => r.OwningLinkId == null).ToListAsync(ct)
            : await session.Query<CustomerRecord>().Where(r => r.OwningLinkId == owningLinkId).ToListAsync(ct);
        var byId = stored.ToDictionary(r => r.CustomerId, StringComparer.OrdinalIgnoreCase);

        // A customer can move between owners (reseller transfer, direct ↔ indirect): such a record exists
        // under a DIFFERENT owner and is not in the scope query above, so load it and re-home it in place.
        var missingKeys = customers.Where(c => !byId.ContainsKey(c.Id)).Select(c => EstateStreams.Customer(c.Id)).ToArray();
        if (missingKeys.Length > 0)
        {
            foreach (var r in await session.LoadManyAsync<CustomerRecord>(ct, missingKeys))
            {
                byId[r.CustomerId] = r;
            }
        }

        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in customers)
        {
            if (!handled.Add(c.Id))
            {
                continue; // duplicate id within one list response
            }
            byId.TryGetValue(c.Id, out var existing);
            await ObserveCustomerCoreAsync(session, existing, c, owningLinkId, source, now, ct);
        }

        foreach (var row in stored.Where(r => !seen.Contains(r.CustomerId) && !r.IsDeleted))
        {
            await ObserveCustomerRemovedAsync(session, row.CustomerId, "Missing from the owner's customer list", source, ct);
        }
    }

    /// <summary>
    /// Marks a customer (and all of its entitlements) as removed — after a successful delete through the
    /// app, or when it vanished from a fresh list pass. Idempotent: a no-op if already removed or unknown.
    /// </summary>
    public async Task ObserveCustomerRemovedAsync(
        IDocumentSession session, string customerId, string reason, ObservationSource source, CancellationToken ct)
    {
        var record = await session.LoadAsync<CustomerRecord>(EstateStreams.Customer(customerId), ct);
        if (record is null || record.IsDeleted)
        {
            return;
        }

        session.Events.Append(record.Id, new CustomerRemovedObserved(customerId, reason, source));

        var entitlements = await session.Query<EntitlementRecord>()
            .Where(e => e.CustomerId == customerId && !e.IsDeleted)
            .ToListAsync(ct);
        foreach (var e in entitlements)
        {
            session.Events.Append(e.Id, new EntitlementRemovedObserved(e.EntitlementId, customerId, "Customer removed", source));
        }

        var state = await session.LoadAsync<CustomerSyncState>(customerId, ct) ?? new CustomerSyncState { Id = customerId };
        state.IsDeleted = true;
        state.LastObservedUtc = DateTimeOffset.UtcNow;
        session.Store(state);
    }

    /// <summary>Records a mutation the app issued for a customer (audit trail); no-op for customers never observed.</summary>
    public async Task RecordCustomerMutationAsync(
        IDocumentSession session, string customerId, string action, string? requestedBy, string? operationName, CancellationToken ct)
    {
        var key = EstateStreams.Customer(customerId);
        if (await StreamExistsAsync<CustomerRecord>(session, key, ct))
        {
            session.Events.Append(key, new CustomerMutationRequested(customerId, action, requestedBy, operationName));
        }
    }

    /// <summary>True when the stream has been persisted or was started/appended earlier in this same session (not yet saved).</summary>
    private static async Task<bool> StreamExistsAsync<TRecord>(IDocumentSession session, string key, CancellationToken ct) where TRecord : class
    {
        if (session.PendingChanges.Streams().Any(s => string.Equals(s.Key, key, StringComparison.Ordinal)))
        {
            return true;
        }
        return await session.LoadAsync<TRecord>(key, ct) is not null;
    }

    private async Task<bool> ObserveCustomerCoreAsync(
        IDocumentSession session, CustomerRecord? existing, Customer customer, string? owningLinkId,
        ObservationSource source, DateTimeOffset now, CancellationToken ct)
    {
        var key = EstateStreams.Customer(customer.Id);
        var cloudIdentityDomain = customer.CloudIdentity?.PrimaryDomain;
        var changed = false;

        if (existing is null)
        {
            session.Events.StartStream<CustomerRecord>(key, new CustomerDiscovered(
                customer.Id, customer.OrgDisplayName, customer.Domain, customer.CloudIdentityId,
                cloudIdentityDomain, owningLinkId, customer.CreateTime, source));
            changed = true;
        }
        else
        {
            var events = new List<object>();
            if (existing.IsDeleted)
            {
                events.Add(new CustomerRestoredObserved(customer.Id, source));
            }
            if (!Same(existing.OrgName, customer.OrgDisplayName)
                || !Same(existing.Domain, customer.Domain)
                || !Same(existing.CloudIdentityId, customer.CloudIdentityId)
                || !Same(existing.CloudIdentityDomain, cloudIdentityDomain)
                || existing.CreateTime != customer.CreateTime)
            {
                events.Add(new CustomerDetailsChanged(
                    customer.Id, customer.OrgDisplayName, customer.Domain, customer.CloudIdentityId,
                    cloudIdentityDomain, customer.CreateTime, source));
            }
            if (!Same(existing.OwningLinkId, owningLinkId))
            {
                events.Add(new CustomerOwnershipChanged(customer.Id, existing.OwningLinkId, owningLinkId, source));
            }
            if (events.Count > 0)
            {
                session.Events.Append(key, events.ToArray());
                changed = true;
            }
        }

        var state = await session.LoadAsync<CustomerSyncState>(customer.Id, ct) ?? new CustomerSyncState { Id = customer.Id };
        state.OwningLinkId = owningLinkId;
        state.IsDeleted = false;
        state.LastObservedUtc = now;
        session.Store(state);
        return changed;
    }

    // -------------------------------------------------------------------------------------------------
    // Channel partner links
    // -------------------------------------------------------------------------------------------------

    /// <summary>Observes the account's full link roster: discovers/updates each link and marks vanished ones removed.</summary>
    public async Task ObserveLinksAsync(
        IDocumentSession session, IReadOnlyList<ChannelPartnerLink> links, ObservationSource source,
        DateTimeOffset now, CancellationToken ct)
    {
        var stored = await session.Query<ResellerLinkRecord>().ToListAsync(ct);
        var byId = stored.ToDictionary(r => r.LinkId, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var l in links)
        {
            if (!seen.Add(l.Id))
            {
                continue;
            }
            ObserveLink(session, byId.GetValueOrDefault(l.Id), l, source);
            var state = await session.LoadAsync<LinkSyncState>(l.Id, ct);
            if (state is null)
            {
                session.Store(new LinkSyncState { Id = l.Id, LastSyncedUtc = DateTimeOffset.MinValue });
            }
        }

        foreach (var row in stored.Where(r => !seen.Contains(r.LinkId) && !r.IsDeleted))
        {
            session.Events.Append(row.Id, new LinkRemovedObserved(row.LinkId, source));
        }
    }

    /// <summary>Observes one link (e.g. a create/patch result).</summary>
    public async Task ObserveLinkAsync(IDocumentSession session, ChannelPartnerLink link, ObservationSource source, CancellationToken ct)
    {
        var existing = await session.LoadAsync<ResellerLinkRecord>(EstateStreams.Link(link.Id), ct);
        ObserveLink(session, existing, link, source);
        if (await session.LoadAsync<LinkSyncState>(link.Id, ct) is null)
        {
            session.Store(new LinkSyncState { Id = link.Id, LastSyncedUtc = DateTimeOffset.MinValue });
        }
    }

    /// <summary>Records a mutation the app issued for a link (audit trail); no-op for links never observed.</summary>
    public async Task RecordLinkMutationAsync(IDocumentSession session, string linkId, string action, string? requestedBy, CancellationToken ct)
    {
        var key = EstateStreams.Link(linkId);
        if (await StreamExistsAsync<ResellerLinkRecord>(session, key, ct))
        {
            session.Events.Append(key, new LinkMutationRequested(linkId, action, requestedBy));
        }
    }

    private static void ObserveLink(IDocumentSession session, ResellerLinkRecord? existing, ChannelPartnerLink l, ObservationSource source)
    {
        var key = EstateStreams.Link(l.Id);
        var state = l.LinkState ?? "UNSPECIFIED";
        var primaryDomain = l.ChannelPartner?.PrimaryDomain;
        if (existing is null)
        {
            session.Events.StartStream<ResellerLinkRecord>(key, new LinkDiscovered(
                l.Id, l.ResellerCloudIdentityId, primaryDomain, state, l.CreateTime, source));
            return;
        }

        var events = new List<object>();
        if (existing.IsDeleted)
        {
            events.Add(new LinkRestoredObserved(l.Id, source));
        }
        if (!Same(existing.ResellerCloudId, l.ResellerCloudIdentityId)
            || !Same(existing.PrimaryDomain, primaryDomain)
            || existing.CreateTime != l.CreateTime)
        {
            events.Add(new LinkDetailsChanged(l.Id, l.ResellerCloudIdentityId, primaryDomain, l.CreateTime, source));
        }
        if (!string.Equals(existing.LinkState, state, StringComparison.Ordinal))
        {
            events.Add(new LinkStateChanged(l.Id, existing.LinkState, state, source));
        }
        if (events.Count > 0)
        {
            session.Events.Append(key, events.ToArray());
        }
    }

    // -------------------------------------------------------------------------------------------------
    // Reseller-self classification
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Classifies every live customer as a reseller buying for its own use (its Cloud Identity id or domain
    /// matches a channel partner link — a DIRECT sale from us) vs a reseller's end customer, appending
    /// <see cref="CustomerClassified"/> / <see cref="EntitlementOwnershipChanged"/> only where the flag flips.
    /// Returns (total, direct) counts for logging.
    /// </summary>
    public async Task<(int Total, int Direct)> ClassifyCustomersAsync(IDocumentSession session, CancellationToken ct)
    {
        var links = await session.Query<ResellerLinkRecord>().Where(l => !l.IsDeleted).ToListAsync(ct);
        var resellerCids = links.Where(l => !string.IsNullOrEmpty(l.ResellerCloudId))
            .Select(l => l.ResellerCloudId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resellerDomains = links.Where(l => !string.IsNullOrEmpty(l.PrimaryDomain))
            .Select(l => l.PrimaryDomain!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var customers = await session.Query<CustomerRecord>().Where(c => !c.IsDeleted).ToListAsync(ct);
        var direct = 0;
        var flipped = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in customers)
        {
            var isSelf = (c.CloudIdentityId != null && resellerCids.Contains(c.CloudIdentityId))
                || (c.Domain != null && resellerDomains.Contains(c.Domain));
            if (isSelf) { direct++; }
            if (c.IsResellerSelf != isSelf)
            {
                session.Events.Append(c.Id, new CustomerClassified(c.CustomerId, isSelf));
                flipped[c.CustomerId] = isSelf;
            }
        }

        if (flipped.Count > 0)
        {
            var ids = flipped.Keys.ToList();
            var entitlements = await session.Query<EntitlementRecord>()
                .Where(e => e.CustomerId.IsOneOf(ids))
                .ToListAsync(ct);
            foreach (var e in entitlements)
            {
                var isSelf = flipped[e.CustomerId];
                if (e.IsResellerSelf != isSelf)
                {
                    session.Events.Append(e.Id, new EntitlementOwnershipChanged(e.EntitlementId, e.CustomerId, e.OwningLinkId, isSelf));
                }
            }
        }

        return (customers.Count, direct);
    }

    // -------------------------------------------------------------------------------------------------
    // Entitlements (the contended ListEntitlements quota) — targeted and bulk paths.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Targeted refresh of one customer: best-effort re-observes its metadata, then re-syncs all of its
    /// entitlements (names/prices resolved per entitlement via <c>lookupOffer</c>). Re-reads live state, so
    /// it is safe against duplicate/out-of-order triggers; the owning link is preserved from the record.
    /// Deletions are NOT inferred from a metadata fetch miss (a transient 404 must never remove a customer).
    /// </summary>
    public async Task ProjectCustomerAsync(
        IDocumentSession session, IGoogleChannelClient client, string customerId, ObservationSource source,
        DateTimeOffset now, CancellationToken ct)
    {
        var existing = await session.LoadAsync<CustomerRecord>(EstateStreams.Customer(customerId), ct);
        var owningLinkId = existing?.OwningLinkId;

        try
        {
            var customer = await client.GetCustomerAsync(customerId, ct);
            await ObserveCustomerCoreAsync(session, existing, customer, owningLinkId, source, now, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Projection: customer metadata refresh skipped for {Customer}; refreshing entitlements only.", customerId);
        }

        var linkPercent = string.IsNullOrEmpty(owningLinkId)
            ? 0m
            : await ResolveChannelPartnerPercentAsync(client, owningLinkId, now, ct);
        var diag = new PricingDiagnostics();
        await SyncCustomerEntitlementsAsync(
            session, client, customerId, owningLinkId, EmptyOfferCatalog, EmptyNames, linkPercent, diag, source, now, ct);
    }

    /// <summary>
    /// Observes one customer's entitlements: lists them live, appends discovery / seat / state / offer /
    /// commitment / ownership / billing diffs, resolves enrichment (catalog names, per-month wholesale
    /// price with the <c>lookupOffer</c> fallback, repricing mark-up, Ion cost) and appends enrichment
    /// events only when a resolved value differs, marks vanished entitlements removed, and stamps the
    /// customer's <see cref="CustomerSyncState.LastSyncedUtc"/> so the staleness rotation advances. A 429
    /// leaves the stamp untouched and flags <see cref="PricingDiagnostics.RateLimited"/> so the caller can
    /// stop the cycle; any other failure stamps the customer as visited so one bad customer can't stall the queue.
    /// </summary>
    public async Task SyncCustomerEntitlementsAsync(
        IDocumentSession session, IGoogleChannelClient client, string customerId, string? owningLinkId,
        OfferCatalog offerCatalog, IReadOnlyDictionary<string, string> productNames, decimal linkFallbackPercent,
        PricingDiagnostics diag, ObservationSource source, DateTimeOffset now, CancellationToken ct)
    {
        var state = await session.LoadAsync<CustomerSyncState>(customerId, ct) ?? new CustomerSyncState { Id = customerId, OwningLinkId = owningLinkId };

        IReadOnlyList<Entitlement> entitlements;
        try
        {
            var result = await client.ListEntitlementsForSyncAsync(customerId, ct);
            entitlements = result.Entitlements;
        }
        catch (Exception ex)
        {
            if (IsRateLimited(ex))
            {
                diag.RateLimited = true;
                logger.LogDebug(ex, "Entitlement sync throttled (429) for customer {Customer}; will retry next cycle.", customerId);
                return;
            }
            logger.LogWarning(ex, "Entitlement sync skipped for customer {Customer}.", customerId);
            state.LastSyncedUtc = DateTimeOffset.UtcNow;
            state.LastError = Truncate(Flatten(ex), 500);
            session.Store(state);
            return;
        }

        var entitlementPercents = await ResolveCustomerEntitlementPercentsAsync(client, customerId, now, ct);

        var seen = entitlements.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stored = await session.Query<EntitlementRecord>()
            .Where(r => r.CustomerId == customerId)
            .ToListAsync(ct);
        var byId = stored.ToDictionary(r => r.EntitlementId, StringComparer.OrdinalIgnoreCase);

        var customer = await session.LoadAsync<CustomerRecord>(EstateStreams.Customer(customerId), ct);
        var isResellerSelf = customer?.IsResellerSelf ?? false;
        var ionBySku = await BuildIonCostMapAsync(session, customer, diag, ct);

        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entitlements)
        {
            if (!handled.Add(e.Id))
            {
                continue;
            }

            var key = EstateStreams.Entitlement(e.Id);
            var seats = SeatsOf(e);
            var billable = NumUnitsOf(e);
            var provisioningState = e.ProvisioningState ?? "UNSPECIFIED";
            var isActive = string.Equals(provisioningState, "ACTIVE", StringComparison.OrdinalIgnoreCase);
            byId.TryGetValue(e.Id, out var row);

            // Auto-renew: entitlements.list omits renewalSettings for commitment offers, so fall back to a
            // lean entitlements.get for active commitment entitlements whose flag is still unknown.
            var renewal = e.Commitment?.RenewalEnabled;
            if (isActive && renewal is null && e.Commitment is { EndTime: not null })
            {
                try
                {
                    renewal = await client.GetEntitlementRenewalEnabledAsync(customerId, e.Id, ct) ?? row?.RenewalEnabled;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Auto-renew fallback fetch failed for entitlement {Entitlement}; keeping prior value.", e.Id);
                    renewal = row?.RenewalEnabled;
                }
            }

            var events = new List<object>();
            if (row is null)
            {
                session.Events.StartStream<EntitlementRecord>(key, new EntitlementDiscovered(
                    e.Id, customerId, owningLinkId, e.ProductId, e.SkuId, e.OfferId, provisioningState, seats, billable,
                    e.IsTrial, e.CreateTime, e.Commitment?.EndTime, renewal, e.BillingAccount, e.ProvisioningId, source));
                if (isResellerSelf)
                {
                    events.Add(new EntitlementOwnershipChanged(e.Id, customerId, owningLinkId, true));
                }
            }
            else
            {
                if (row.IsDeleted)
                {
                    events.Add(new EntitlementRestoredObserved(e.Id, customerId, source));
                }
                if (row.Seats != seats || row.BillableSeats != billable)
                {
                    events.Add(new EntitlementSeatsChanged(e.Id, customerId, row.Seats, seats, row.BillableSeats, billable, source));
                }
                if (!string.Equals(row.State, provisioningState, StringComparison.Ordinal) || row.IsTrial != e.IsTrial)
                {
                    events.Add(new EntitlementStateChanged(e.Id, customerId, row.State, provisioningState, e.IsTrial, source));
                }
                if (!Same(row.ProductId, e.ProductId) || !Same(row.SkuId, e.SkuId) || !Same(row.OfferId, e.OfferId))
                {
                    events.Add(new EntitlementOfferChanged(e.Id, customerId, e.ProductId, e.SkuId, row.OfferId, e.OfferId, source));
                }
                if (row.CommitmentEndTime != e.Commitment?.EndTime || row.RenewalEnabled != renewal)
                {
                    events.Add(new EntitlementCommitmentChanged(e.Id, customerId, e.Commitment?.EndTime, renewal, source));
                }
                if (!Same(row.OwningLinkId, owningLinkId) || row.IsResellerSelf != isResellerSelf)
                {
                    events.Add(new EntitlementOwnershipChanged(e.Id, customerId, owningLinkId, isResellerSelf));
                }
                if (!Same(row.BillingAccount, e.BillingAccount) || !Same(row.ProvisioningId, e.ProvisioningId))
                {
                    events.Add(new EntitlementBillingChanged(e.Id, customerId, e.BillingAccount, e.ProvisioningId));
                }
            }

            // ---- Enrichment: catalog names + plan description --------------------------------------
            // Names come from the account's sellable catalog; when that misses (education/legacy/churned
            // offers aren't sellable) KEEP any name resolved on a prior pass instead of resetting to null.
            var productName = e.ProductId is not null
                && (productNames.TryGetValue(e.ProductId, out var pn) || offerCatalog.ProductNames.TryGetValue(e.ProductId, out pn))
                ? pn : row?.ProductName;
            var skuName = e.SkuId is not null && offerCatalog.SkuNames.TryGetValue(e.SkuId, out var sn) ? sn : row?.SkuName;
            var offerName = e.OfferId is not null && offerCatalog.OfferNames.TryGetValue(e.OfferId, out var on) ? on : row?.OfferName;
            var planDescription = BuildPlanDescription(
                e.Commitment,
                e.OfferId is not null && offerCatalog.Plans.TryGetValue(e.OfferId, out var plan) ? plan : default);

            // ---- Enrichment: wholesale per-month price ---------------------------------------------
            var unitPrice = 0m;
            string? currency = null;
            CatalogOffer? lookedUp = null;
            if (e.OfferId is not null && offerCatalog.Pricing.TryGetValue(e.OfferId, out var price))
            {
                // Offer prices are quoted per PAYMENT CYCLE; UnitPrice is PER MONTH, so divide by the cycle.
                var cycleMonths = offerCatalog.Plans.TryGetValue(e.OfferId, out var pricedPlan) ? MonthsInCycle(pricedPlan.PaymentCycle) : 1;
                unitPrice = cycleMonths > 1 ? price.Unit / cycleMonths : price.Unit;
                currency = price.Currency;
                if (isActive) { diag.ActivePriced++; }
            }
            else if (isActive && row is { UnitPrice: > 0m })
            {
                // Resolved on a prior pass via the lookupOffer fallback; keep it instead of re-fetching.
                unitPrice = row.UnitPrice;
                currency = row.Currency;
                diag.ActivePriced++;
            }
            else if (isActive)
            {
                // The sellable offers.list can't price this active entitlement; entitlements.lookupOffer (the
                // detail page's source) usually can. One-time, bounded: only unpriced actives with seats > 0.
                if (e.OfferId is not null && seats > 0)
                {
                    try
                    {
                        lookedUp = await client.LookupEntitlementOfferAsync(customerId, e.Id, ct);
                        var seat = lookedUp.Pricing.FirstOrDefault(p =>
                            string.Equals(p.ResourceType, "SEAT", StringComparison.OrdinalIgnoreCase))
                            ?? lookedUp.Pricing.FirstOrDefault();
                        if ((seat?.EffectivePrice ?? seat?.BasePrice) is { } money)
                        {
                            var cycleMonths = MonthsInCycle(lookedUp.PaymentCycle);
                            unitPrice = cycleMonths > 1 ? money.Value / cycleMonths : money.Value;
                            currency = money.CurrencyCode;
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Offer-price fallback (lookupOffer) failed for entitlement {Entitlement}; leaving unpriced.", e.Id);
                    }
                }

                if (unitPrice > 0m)
                {
                    diag.ActivePriced++;
                }
                else if (e.OfferId is null)
                {
                    diag.ActiveUnpriced++;
                    diag.ActiveWithoutOfferId++;
                }
                else if (offerCatalog.OfferNames.ContainsKey(e.OfferId))
                {
                    diag.ActiveUnpriced++;
                    diag.ListedWithoutPrice[e.OfferId] = diag.ListedWithoutPrice.GetValueOrDefault(e.OfferId) + 1;
                }
                else
                {
                    diag.ActiveUnpriced++;
                    diag.MissingFromCatalog[e.OfferId] = diag.MissingFromCatalog.GetValueOrDefault(e.OfferId) + 1;
                }
            }
            else if (row is not null)
            {
                // Inactive: keep whatever was last resolved so history isn't rewritten by a suspension.
                unitPrice = row.UnitPrice;
                currency = row.Currency;
            }

            // ---- Enrichment: friendly-name fallback via the entitlement's OWN offer ------------------
            if (e.OfferId is not null &&
                (string.IsNullOrEmpty(offerName) || string.IsNullOrEmpty(skuName) || string.IsNullOrEmpty(productName)))
            {
                try
                {
                    lookedUp ??= await client.LookupEntitlementOfferAsync(customerId, e.Id, ct);
                    if (string.IsNullOrEmpty(offerName)) { offerName = lookedUp.DisplayName; }
                    if (string.IsNullOrEmpty(skuName)) { skuName = lookedUp.SkuDisplayName; }
                    if (string.IsNullOrEmpty(productName)) { productName = lookedUp.ProductDisplayName; }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Offer-name fallback (lookupOffer) failed for entitlement {Entitlement}; leaving raw ids.", e.Id);
                }
            }

            var repricingPercent = entitlementPercents.TryGetValue(e.Id, out var pct) ? pct : linkFallbackPercent;

            if (row is null || !Same(row.ProductName, productName) || !Same(row.SkuName, skuName)
                || !Same(row.OfferName, offerName) || !Same(row.PlanDescription, planDescription))
            {
                if (row is not null || productName is not null || skuName is not null || offerName is not null || planDescription is not null)
                {
                    events.Add(new EntitlementCatalogResolved(e.Id, customerId, productName, skuName, offerName, planDescription));
                }
            }
            if (row is null ? (unitPrice != 0m || repricingPercent != 0m || currency is not null)
                : (row.UnitPrice != unitPrice || !Same(row.Currency, currency) || row.RepricingPercent != repricingPercent))
            {
                events.Add(new EntitlementPriced(e.Id, customerId, unitPrice, currency, repricingPercent));
            }

            // ---- Enrichment: Ion cost ------------------------------------------------------------
            if (ion.IsConfigured)
            {
                var nameKey = IonGatewayClient.NormalizeSkuName(skuName ?? e.SkuDisplayName ?? e.SkuId);
                var ionHit = nameKey.Length > 0 && ionBySku.TryGetValue(nameKey, out var h) ? h : null;
                if (ionHit is not null) { diag.IonEnriched++; }
                else if (ionBySku.Count > 0 && diag.IonUnmatchedEntitlementSkus.Count < 50)
                {
                    diag.IonUnmatchedEntitlementSkus.Add(skuName ?? e.SkuDisplayName ?? e.SkuId ?? "(unknown)");
                }
                var cost = ionHit?.Cost;
                var margin = ionHit?.MarginPercent;
                var ionCurrency = ionHit?.Currency;
                if (row is null ? ionHit is not null
                    : (row.IonCostPerSeat != cost || row.IonMarginPercent != margin || !Same(row.IonCurrency, ionCurrency)))
                {
                    events.Add(new EntitlementIonCostResolved(e.Id, customerId, cost, margin, ionCurrency));
                }
            }

            if (events.Count > 0)
            {
                session.Events.Append(key, events.ToArray());
            }
        }

        foreach (var row in stored.Where(r => !seen.Contains(r.EntitlementId) && !r.IsDeleted))
        {
            session.Events.Append(row.Id, new EntitlementRemovedObserved(row.EntitlementId, customerId, "Missing from the customer's entitlement list", source));
        }

        state.OwningLinkId = owningLinkId;
        state.IsDeleted = false;
        state.LastSyncedUtc = DateTimeOffset.UtcNow;
        state.LastError = null;
        session.Store(state);
    }

    /// <summary>Records a lifecycle command the app issued for an entitlement; starts a minimal stream when the entitlement is not yet known.</summary>
    public async Task RecordEntitlementChangeRequestedAsync(
        IDocumentSession session, string customerId, string entitlementId, string action, string? requestedBy,
        string? operationName, string? details, CancellationToken ct)
    {
        var key = EstateStreams.Entitlement(entitlementId);
        var evt = new EntitlementChangeRequested(entitlementId, customerId, action, requestedBy, operationName, details);
        if (!await StreamExistsAsync<EntitlementRecord>(session, key, ct))
        {
            // Not yet observed by the sync: a targeted projection will fill in the facts; keep the audit trail now.
            logger.LogDebug("Change requested for not-yet-observed entitlement {Entitlement}; recording on the customer stream only.", entitlementId);
            await RecordCustomerMutationAsync(session, customerId, $"{action} entitlement {entitlementId}", requestedBy, operationName, ct);
            return;
        }
        session.Events.Append(key, evt);
    }

    // -------------------------------------------------------------------------------------------------
    // Helpers (ported verbatim from the former ReadModelProjector).
    // -------------------------------------------------------------------------------------------------

    private async Task<Dictionary<string, IonGoogleCost>> BuildIonCostMapAsync(
        IDocumentSession session, CustomerRecord? customer, PricingDiagnostics diag, CancellationToken ct)
    {
        var map = new Dictionary<string, IonGoogleCost>(StringComparer.Ordinal);
        if (!ion.IsConfigured || customer is null)
        {
            return map;
        }

        // Reseller domain: the owning channel-partner link's primary domain, or the customer's own domain
        // for a reseller-self customer (buying for its own use).
        string? resellerDomain = null;
        if (!string.IsNullOrEmpty(customer.OwningLinkId))
        {
            var link = await session.LoadAsync<ResellerLinkRecord>(EstateStreams.Link(customer.OwningLinkId), ct);
            resellerDomain = link?.PrimaryDomain;
        }
        resellerDomain ??= customer.Domain;
        if (string.IsNullOrWhiteSpace(resellerDomain))
        {
            return map;
        }

        foreach (var cost in await ion.GetResellerGoogleCostsAsync(resellerDomain, ct))
        {
            map[cost.SkuNameKey] = cost;
        }
        if (map.Count > 0)
        {
            diag.IonResellersMatched.Add(resellerDomain);
            foreach (var c in map.Values)
            {
                if (diag.IonAvailableSkuNames.Count < 50 && !string.IsNullOrEmpty(c.SkuName)) { diag.IonAvailableSkuNames.Add(c.SkuName); }
            }
        }
        return map;
    }

    /// <summary>Seat footprint: num_units, falling back to max_units (flexible/usage plans store their cap there).</summary>
    public static long SeatsOf(Entitlement e)
    {
        var raw = e.Parameters.FirstOrDefault(p => string.Equals(p.Name, "num_units", StringComparison.OrdinalIgnoreCase))?.Value
            ?? e.Parameters.FirstOrDefault(p => string.Equals(p.Name, "max_units", StringComparison.OrdinalIgnoreCase))?.Value;
        return long.TryParse(raw, out var n) ? n : 0;
    }

    /// <summary>Committed/billable seats (num_units only) — what pricing multiplies by.</summary>
    public static long NumUnitsOf(Entitlement e)
    {
        var raw = e.Parameters.FirstOrDefault(p => string.Equals(p.Name, "num_units", StringComparison.OrdinalIgnoreCase))?.Value;
        return long.TryParse(raw, out var n) ? n : 0;
    }

    /// <summary>Per-cycle offer catalog: wholesale pricing plus offer/SKU/product display names and plans, keyed by id.</summary>
    public readonly record struct OfferCatalog(
        IReadOnlyDictionary<string, (decimal Unit, string Currency)> Pricing,
        IReadOnlyDictionary<string, string> OfferNames,
        IReadOnlyDictionary<string, string> SkuNames,
        IReadOnlyDictionary<string, (string? PaymentPlan, string? PaymentCycle)> Plans,
        IReadOnlyDictionary<string, string> ProductNames);

    /// <summary>Accumulates, across one pass, why ACTIVE entitlements end up unpriced plus Ion enrichment tallies.</summary>
    public sealed class PricingDiagnostics
    {
        public int ActivePriced;
        public int ActiveUnpriced;
        public int ActiveWithoutOfferId;
        public readonly Dictionary<string, int> MissingFromCatalog = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> ListedWithoutPrice = new(StringComparer.OrdinalIgnoreCase);
        public int IonEnriched;
        public readonly HashSet<string> IonResellersMatched = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> IonUnmatchedEntitlementSkus = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> IonAvailableSkuNames = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Set when a Channel API call was throttled (429) so the caller can stop the cycle.</summary>
        public bool RateLimited;
    }

    /// <summary>True when the exception (or an inner one) is a Channel API quota/429.</summary>
    public static bool IsRateLimited(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is global::Google.GoogleApiException g && g.HttpStatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Flattens an exception chain into one message so a recorded error shows the actual cause.</summary>
    public static string Flatten(Exception ex)
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

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>Human-friendly plan summary (e.g. "Annual Plan (Monthly Payment)") from the commitment term and the offer's plan/cycle.</summary>
    public static string? BuildPlanDescription(EntitlementCommitment? commitment, (string? PaymentPlan, string? PaymentCycle) plan)
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

    /// <summary>Months an offer's payment cycle spans: Monthly/Daily → 1, Annual/Yearly → 12, "N-monthly"/"N-yearly" parsed, else 1.</summary>
    public static int MonthsInCycle(string? paymentCycle)
    {
        var c = paymentCycle?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(c)) return 1;
        if (c is "monthly" or "daily") return 1;
        if (c is "annual" or "yearly") return 12;
        var dash = c.IndexOf('-');
        if (dash > 0 && int.TryParse(c[..dash], out var n) && n > 0)
        {
            if (c.Contains("year")) return n * 12;
            if (c.Contains("month")) return n;
        }
        return 1;
    }

    private async Task<IReadOnlyDictionary<string, decimal>> ResolveCustomerEntitlementPercentsAsync(
        IGoogleChannelClient client, string customerId, DateTimeOffset now, CancellationToken ct)
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
            logger.LogDebug(ex, "Customer repricing lookup skipped for customer {Customer}.", customerId);
        }
        return map;
    }

    /// <summary>The reseller-wide (channel-partner granularity) mark-up for a link, or 0 when none applies.</summary>
    public async Task<decimal> ResolveChannelPartnerPercentAsync(
        IGoogleChannelClient client, string linkId, DateTimeOffset now, CancellationToken ct)
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
            logger.LogDebug(ex, "Channel-partner repricing lookup skipped for link {Link}.", linkId);
            return 0m;
        }
    }

    // The config currently in force: the latest whose effective invoice month is on or before the current
    // month, else the earliest future one (so a freshly created future config still previews).
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
