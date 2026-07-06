using GChannel.ApiService.Data;
using GChannel.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace GChannel.ApiService.Services;

/// <summary>
/// §10 read-model <b>projection</b> — the single source of truth for materialising one customer (and its
/// entitlements) into the SQL read-model. It is shared by three callers so the denormalised fields stay
/// identical everywhere:
/// <list type="bullet">
///   <item>the background <c>ReadModelSyncService</c> (bulk rolling reconciliation, passes a pre-built
///   per-cycle offer catalog);</item>
///   <item>the mutation endpoints (<b>write-through</b>: after a successful Channel API create/import/
///   update/delete they upsert the one changed <see cref="CustomerRecord"/> immediately so the UI reflects
///   it without waiting for the next sync cycle — closes the "shows — until next sync" gap);</item>
///   <item>the Pub/Sub <c>ChannelNotificationsService</c> (<b>event-driven projection</b>: a change event
///   triggers a targeted refresh of just that customer, with the poll left as a reconciliation backstop).</item>
/// </list>
/// Projections are <b>idempotent</b> (upsert by primary key) and re-read live state rather than applying an
/// event payload, so duplicate or out-of-order events converge on the current truth. The write-through
/// methods take the mutation result directly and make <b>no</b> extra Channel API call.
/// </summary>
public sealed class ReadModelProjector(ILogger<ReadModelProjector> logger)
{
    /// <summary>An empty offer catalog for single-resource projection: with no pre-built catalog every
    /// entitlement resolves its price and display names via the per-entitlement <c>lookupOffer</c> fallback
    /// baked into <see cref="SyncCustomerEntitlementsAsync"/> (the same source the entitlement detail page
    /// uses), so a targeted projection is fully priced/named without a catalog fan-out.</summary>
    public static readonly OfferCatalog EmptyOfferCatalog = new(
        new Dictionary<string, (decimal, string)>(),
        new Dictionary<string, string>(),
        new Dictionary<string, string>(),
        new Dictionary<string, (string?, string?)>(),
        new Dictionary<string, string>());

    private static readonly IReadOnlyDictionary<string, string> EmptyNames = new Dictionary<string, string>();

    // -------------------------------------------------------------------------------------------------
    // Write-through (used by the mutation endpoints — no live Channel API call; uses the mutation result).
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Write-through upsert of one customer's metadata from a mutation result (create/import/update). Only
    /// metadata is written — seats, pricing and entitlements catch up on the next sync cycle or a change
    /// event. Idempotent: a <c>FindAsync</c> by primary key re-homes a customer that already existed under a
    /// different owner instead of inserting a duplicate. New rows seed <c>LastSyncedUtc = MinValue</c> so the
    /// entitlement rotation picks them up first; existing rows keep their entitlement-freshness stamp.
    /// </summary>
    public async Task UpsertCustomerAsync(
        GChannelDbContext db, Customer customer, string? owningLinkId, DateTimeOffset now, CancellationToken ct)
    {
        var row = await db.CustomerRecords.FindAsync([customer.Id], ct);
        if (row is null)
        {
            row = new CustomerRecord { CustomerId = customer.Id, LastSyncedUtc = DateTimeOffset.MinValue };
            db.CustomerRecords.Add(row);
        }
        row.OrgName = customer.OrgDisplayName;
        row.Domain = customer.Domain;
        row.CloudIdentityId = customer.CloudIdentityId;
        row.OwningLinkId = owningLinkId;
        row.CreateTime = customer.CreateTime;
        row.IsDeleted = false;
        // LastSyncedUtc intentionally NOT reset for existing rows — it reflects entitlement-sync freshness.
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Write-through soft-delete of a customer (and its entitlements) after a successful Channel API delete,
    /// so estate rollups and lists drop it immediately. Idempotent — a no-op if already deleted or absent.
    /// </summary>
    public async Task SoftDeleteCustomerAsync(GChannelDbContext db, string customerId, CancellationToken ct)
    {
        var row = await db.CustomerRecords.FindAsync([customerId], ct);
        if (row is null || row.IsDeleted)
        {
            return;
        }

        row.IsDeleted = true;
        var entitlements = await db.EntitlementRecords
            .Where(e => e.CustomerId == customerId && !e.IsDeleted)
            .ToListAsync(ct);
        foreach (var e in entitlements)
        {
            e.IsDeleted = true;
        }
        await db.SaveChangesAsync(ct);
    }

    // -------------------------------------------------------------------------------------------------
    // Event-driven projection (used by the Pub/Sub notifications service — targeted, live-fetch refresh).
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Targeted projection of one customer: best-effort refreshes its metadata then re-syncs all of its
    /// entitlements (prices/names resolved per-entitlement via <c>lookupOffer</c>, recomputes the seat total,
    /// soft-deletes any that vanished). Re-reads live state so it is safe against duplicate/out-of-order
    /// events. The owning link id is preserved from the existing row (the change event doesn't carry it);
    /// the metadata fetch is best-effort so an indirect customer that 404s on the account-level get still
    /// gets its entitlements refreshed. Deletions are NOT inferred here — they are handled by the delete
    /// write-through and the poll backstop — so a transient fetch miss never falsely removes a customer.
    /// </summary>
    public async Task ProjectCustomerAsync(
        GChannelDbContext db, GoogleChannelClient client, string customerId, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await db.CustomerRecords.FindAsync([customerId], ct);
        var owningLinkId = existing?.OwningLinkId;

        try
        {
            var customer = await client.GetCustomerAsync(customerId, ct);
            await UpsertCustomerAsync(db, customer, owningLinkId, now, ct);
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
            db, client, customerId, owningLinkId, EmptyOfferCatalog, EmptyNames, linkPercent, diag, now, ct);
    }

    // -------------------------------------------------------------------------------------------------
    // Shared projection core (moved from ReadModelSyncService so bulk sync, write-through and event
    // projection all denormalise identical fields).
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Upserts one customer's entitlements, soft-deletes vanished ones, and denormalises the customer's
    /// active seat total onto <see cref="CustomerRecord"/> for fast reseller ranking. Tolerates a single
    /// customer's read failing (the customer simply keeps its previous seat/product rows). Also denormalises
    /// §11 pricing (offer wholesale price) and the §6 repricing mark-up onto each entitlement so the
    /// dashboard can roll up estimated cost/revenue/margin from SQL without any live API fan-out. When the
    /// supplied catalog can't price/name an active entitlement, a per-entitlement <c>lookupOffer</c> fallback
    /// resolves it (so a single-resource projection with an empty catalog is still fully priced/named).
    /// </summary>
    public async Task SyncCustomerEntitlementsAsync(
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
            var billable = NumUnitsOf(e);
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
            // Names come from the account's sellable catalog (offers.list/products.list). When that misses
            // (education/legacy/churned offers a customer already holds aren't in the sellable catalog),
            // KEEP any name resolved on a prior cycle (via the lookupOffer fallback below) instead of
            // resetting to null — mirrors how the price is preserved, so the fallback stays one-time.
            row.ProductName = e.ProductId is not null
                && (productNames.TryGetValue(e.ProductId, out var pn) || offerCatalog.ProductNames.TryGetValue(e.ProductId, out pn))
                ? pn : row.ProductName;
            row.SkuId = e.SkuId;
            row.SkuName = e.SkuId is not null && offerCatalog.SkuNames.TryGetValue(e.SkuId, out var sn) ? sn : row.SkuName;
            row.OfferId = e.OfferId;
            row.OfferName = e.OfferId is not null && offerCatalog.OfferNames.TryGetValue(e.OfferId, out var on) ? on : row.OfferName;
            row.State = e.ProvisioningState ?? "UNSPECIFIED";
            row.Seats = seats;
            row.BillableSeats = billable;
            row.IsTrial = e.IsTrial;
            row.CreateTime = e.CreateTime;
            row.CommitmentEndTime = e.Commitment?.EndTime;
            // Prior stored auto-renew flag (may hold a value fetched via the fallback below on an earlier
            // cycle) — captured before we overwrite it with the list value so a transient fallback failure
            // doesn't clobber a good value with null.
            var existingRenewal = row.RenewalEnabled;
            row.RenewalEnabled = e.Commitment?.RenewalEnabled;
            // entitlements.list omits commitmentSettings.renewalSettings for commitment offers, so the
            // auto-renew flag comes back null even though the commitment end date is present. Fall back to
            // a lean entitlements.get for active commitment entitlements whose renewal flag is still unknown
            // (self-limiting: fires only when list didn't supply it). Best-effort — a failure keeps the
            // previously stored value rather than clobbering it with null.
            if (isActive && row.RenewalEnabled is null && e.Commitment is { EndTime: not null })
            {
                try
                {
                    row.RenewalEnabled = await client.GetEntitlementRenewalEnabledAsync(customerId, e.Id, ct) ?? existingRenewal;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Auto-renew fallback fetch failed for entitlement {Entitlement}; keeping prior value.", e.Id);
                    row.RenewalEnabled = existingRenewal;
                }
            }
            row.PlanDescription = BuildPlanDescription(
                e.Commitment,
                e.OfferId is not null && offerCatalog.Plans.TryGetValue(e.OfferId, out var plan) ? plan : default);
            // Prior stored price — captured before overwrite so a value the lookupOffer fallback resolved
            // on an earlier cycle isn't lost, and so we only pay that per-entitlement call once.
            var existingUnitPrice = row.UnitPrice;
            var existingCurrency = row.Currency;
            CatalogOffer? lookedUp = null; // cached lookupOffer result, reused by the price + name fallbacks
            if (e.OfferId is not null && offerCatalog.Pricing.TryGetValue(e.OfferId, out var price))
            {
                // The offer's effective price is quoted per PAYMENT CYCLE (e.g. an annual offer's price is
                // the yearly per-seat amount). UnitPrice is a PER-MONTH figure (rollups label it "monthly"),
                // so normalise by the cycle length — the same divisor the entitlement detail page applies.
                var cycleMonths = offerCatalog.Plans.TryGetValue(e.OfferId, out var pricedPlan)
                    ? MonthsInCycle(pricedPlan.PaymentCycle) : 1;
                row.UnitPrice = cycleMonths > 1 ? price.Unit / cycleMonths : price.Unit;
                row.Currency = price.Currency;
                if (isActive) { diag.ActivePriced++; }
            }
            else if (isActive && existingUnitPrice > 0m)
            {
                // Already resolved on a prior cycle via the lookupOffer fallback below; keep it instead of
                // re-fetching every cycle (offers.list still can't price this offer — churn/legacy).
                row.UnitPrice = existingUnitPrice;
                row.Currency = existingCurrency;
                diag.ActivePriced++;
            }
            else
            {
                row.UnitPrice = 0m;
                row.Currency = null;
                if (isActive)
                {
                    // Fallback: the account's offers.list can't price this active entitlement even though
                    // entitlements.lookupOffer (the same source the entitlement detail page prices from)
                    // can — the backing offer isn't in the sellable offers.list (churn/legacy/sub-reseller).
                    // Do a best-effort per-entitlement lookupOffer so the price rolls up to the read-model
                    // list/customer/estate views like the detail page. Gated on seats>0 (skips free/0-seat
                    // entitlements like Cloud Identity Free that never roll up a cost) and on the value being
                    // unresolved (guarded above), so it's a one-time, bounded cost per priceable entitlement.
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
                                // Normalise the per-cycle offer price to a per-month figure (see above).
                                var cycleMonths = MonthsInCycle(lookedUp.PaymentCycle);
                                row.UnitPrice = cycleMonths > 1 ? money.Value / cycleMonths : money.Value;
                                row.Currency = money.CurrencyCode;
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug(ex, "Offer-price fallback (lookupOffer) failed for entitlement {Entitlement}; leaving unpriced.", e.Id);
                        }
                    }

                    if (row.UnitPrice > 0m)
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
                        // Offer IS in the account's offers.list but exposes no SEAT/base price (and lookupOffer didn't either).
                        diag.ActiveUnpriced++;
                        diag.ListedWithoutPrice[e.OfferId] = diag.ListedWithoutPrice.GetValueOrDefault(e.OfferId) + 1;
                    }
                    else
                    {
                        // Offer the entitlement references is absent from the account's offers.list (churn/legacy/sub-reseller).
                        diag.ActiveUnpriced++;
                        diag.MissingFromCatalog[e.OfferId] = diag.MissingFromCatalog.GetValueOrDefault(e.OfferId) + 1;
                    }
                }
            }
            // Friendly-name fallback (what the Channel Services console shows): the account's
            // offers.list/products.list only covers the reseller's SELLABLE catalog, so education, legacy
            // or churned offers a customer already holds resolve to raw ids. Resolve the offer/SKU/product
            // display names from the entitlement's OWN offer via lookupOffer — reusing the call the price
            // fallback already made when possible, else one best-effort call. Runs for ANY state (incl.
            // SUSPENDED) and any seat count since names are cosmetic; the preserved names above keep it
            // effectively one-time (only fires while a name is still missing).
            if (e.OfferId is not null &&
                (string.IsNullOrEmpty(row.OfferName) || string.IsNullOrEmpty(row.SkuName) || string.IsNullOrEmpty(row.ProductName)))
            {
                try
                {
                    lookedUp ??= await client.LookupEntitlementOfferAsync(customerId, e.Id, ct);
                    if (string.IsNullOrEmpty(row.OfferName)) { row.OfferName = lookedUp.DisplayName; }
                    if (string.IsNullOrEmpty(row.SkuName)) { row.SkuName = lookedUp.SkuDisplayName; }
                    if (string.IsNullOrEmpty(row.ProductName)) { row.ProductName = lookedUp.ProductDisplayName; }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Offer-name fallback (lookupOffer) failed for entitlement {Entitlement}; leaving raw ids.", e.Id);
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
        // Prefer num_units (commitment/seat offers); fall back to max_units (flexible/usage plans, incl.
        // some free/EDU editions, store their seat cap here). Matches the Web UI's seat helper so the
        // reseller seat ranking and per-customer seat counts aren't undercounted for flexible plans.
        var raw = e.Parameters.FirstOrDefault(p =>
            string.Equals(p.Name, "num_units", StringComparison.OrdinalIgnoreCase))?.Value
            ?? e.Parameters.FirstOrDefault(p =>
            string.Equals(p.Name, "max_units", StringComparison.OrdinalIgnoreCase))?.Value;
        return long.TryParse(raw, out var n) ? n : 0;
    }

    // Committed/billable seats (num_units only). Pricing multiplies by this rather than SeatsOf: a
    // flexible/usage plan stores its seat CAP in max_units, which is not what's billed — multiplying an
    // offer's per-seat price by that cap massively inflates the estate value. Flexible plans with no
    // num_units therefore contribute 0 to the wholesale/revenue rollup (matching their usage-based billing).
    private static long NumUnitsOf(Entitlement e)
    {
        var raw = e.Parameters.FirstOrDefault(p =>
            string.Equals(p.Name, "num_units", StringComparison.OrdinalIgnoreCase))?.Value;
        return long.TryParse(raw, out var n) ? n : 0;
    }

    /// <summary>Per-cycle offer catalog: wholesale pricing plus offer/SKU display names, all keyed by id.</summary>
    public readonly record struct OfferCatalog(
        IReadOnlyDictionary<string, (decimal Unit, string Currency)> Pricing,
        IReadOnlyDictionary<string, string> OfferNames,
        IReadOnlyDictionary<string, string> SkuNames,
        IReadOnlyDictionary<string, (string? PaymentPlan, string? PaymentCycle)> Plans,
        IReadOnlyDictionary<string, string> ProductNames);

    /// <summary>
    /// Accumulates, across one sync cycle's entitlement pass, why ACTIVE entitlements end up unpriced so
    /// the cause can be diagnosed from logs: offers absent from the account's offers.list vs offers that
    /// are listed but carry no SEAT/base price vs entitlements with no offer id at all.
    /// </summary>
    public sealed class PricingDiagnostics
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

    /// <summary>
    /// Number of months an offer's payment cycle spans, used to normalise a per-cycle offer price to a
    /// per-month figure. Mirrors the entitlement detail page: Monthly/Daily → 1, Annual/Yearly → 12,
    /// "N-monthly"/"N-yearly" parsed, everything else → 1.
    /// </summary>
    private static int MonthsInCycle(string? paymentCycle)
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
    public async Task<decimal> ResolveChannelPartnerPercentAsync(
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
