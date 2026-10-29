using GChannel.Core.Estate.ReadModels;
using GChannel.Core.Persistence.Documents;
using GChannel.Shared.Contracts;
using Marten;
using System.Globalization;

namespace GChannel.Core.Estate;

/// <summary>
/// Builds the home-dashboard figures from the projected read model (no Channel API calls). Used by the
/// API's dashboard endpoints (short Redis cache) and by the worker's snapshot job. The entitlement KPIs
/// span the WHOLE estate — direct (a reseller buying for its own use, <c>IsResellerSelf</c>) plus the
/// resellers' end customers (indirect) — so they line up with the estate-value panel.
/// </summary>
public static class DashboardReadModel
{
    public static async Task<DashboardSummary> BuildSummaryAsync(IQuerySession session, CancellationToken ct)
    {
        var customers = await session.Query<CustomerRecord>().Where(c => !c.IsDeleted).ToListAsync(ct);
        var entitlements = await session.Query<EntitlementRecord>().Where(e => !e.IsDeleted).ToListAsync(ct);
        var links = await session.Query<ResellerLinkRecord>().Where(l => !l.IsDeleted).ToListAsync(ct);
        return BuildSummary(customers, entitlements, links);
    }

    public static async Task<DashboardOverview> BuildOverviewAsync(IQuerySession session, CancellationToken ct)
    {
        var customers = await session.Query<CustomerRecord>().Where(c => !c.IsDeleted).ToListAsync(ct);
        var links = await session.Query<ResellerLinkRecord>().Where(l => !l.IsDeleted).ToListAsync(ct);
        return BuildOverview(customers, links);
    }

    public static DashboardSummary BuildSummary(
        IReadOnlyList<CustomerRecord> customers, IReadOnlyList<EntitlementRecord> entitlements, IReadOnlyList<ResellerLinkRecord> links)
    {
        var customerCount = customers.Count(c => c.IsResellerSelf);
        var indirectCount = customers.Count(c => !c.IsResellerSelf);
        var activeEntitlements = entitlements.Where(EstateRollups.IsActive).ToList();
        var active = activeEntitlements.Count(e => !e.IsTrial);
        var trials = entitlements.Count(e => e.IsTrial);
        var suspended = entitlements.Count(EstateRollups.IsSuspended);
        var seats = activeEntitlements.Sum(e => e.Seats);

        // Product mix: a product's name resolves only while one of its offers is still listed, so back-fill a
        // null name from a sibling entitlement on the same product.
        var nameByProductId = activeEntitlements
            .Where(e => e.ProductId != null && !string.IsNullOrWhiteSpace(e.ProductName))
            .GroupBy(e => e.ProductId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().ProductName!, StringComparer.OrdinalIgnoreCase);

        string Label(EntitlementRecord e) =>
            !string.IsNullOrWhiteSpace(e.ProductName) ? e.ProductName!
            : e.ProductId != null && nameByProductId.TryGetValue(e.ProductId, out var resolved) ? resolved
            : e.ProductId ?? "Other";

        static List<DashboardProductSlice> BuildMix(IEnumerable<string> labels) =>
            labels.GroupBy(x => x)
                .Select(g => new DashboardProductSlice { Product = g.Key, Count = g.Count() })
                .OrderByDescending(s => s.Count)
                .Take(8)
                .ToList();

        var mix = BuildMix(activeEntitlements.Select(Label));
        var directMix = BuildMix(activeEntitlements.Where(e => e.IsResellerSelf).Select(Label));
        var indirectMix = BuildMix(activeEntitlements.Where(e => !e.IsResellerSelf).Select(Label));

        var linkById = links.ToDictionary(l => l.LinkId, StringComparer.OrdinalIgnoreCase);

        var onboardedByReseller = customers
            .Where(c => c.CreateTime is { Year: >= 2000 })
            .Select(c => new DashboardOnboardedCustomer
            {
                MonthKey = c.CreateTime!.Value.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                CustomerName = c.OrgName ?? c.CustomerId,
                ResellerLinkId = c.OwningLinkId,
                Reseller = c.OwningLinkId != null && linkById.TryGetValue(c.OwningLinkId, out var l)
                    ? EstateRollups.LinkLabel(l)
                    : c.OwningLinkId ?? "Direct",
            })
            .ToList();

        var establishedLinks = links
            .Where(l => l.CreateTime is { Year: >= 2000 })
            .Select(l => new DashboardEstablishedLink
            {
                MonthKey = l.CreateTime!.Value.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                LinkId = l.LinkId,
                Reseller = EstateRollups.LinkLabel(l),
                LinkState = l.LinkState,
            })
            .ToList();

        // Top indirect resellers ranked by active seats, with per-reseller wholesale/margin.
        var seatsByCustomer = activeEntitlements
            .GroupBy(e => e.CustomerId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Seats), StringComparer.OrdinalIgnoreCase);
        var pricingByLink = activeEntitlements
            .Where(e => e.OwningLinkId != null && e.UnitPrice > 0)
            .GroupBy(e => e.OwningLinkId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (
                Wholesale: g.Sum(e => (e.IonCostPerSeat ?? e.UnitPrice) * e.BillableSeats),
                Revenue: g.Sum(e => e.UnitPrice * e.BillableSeats * (1 + (e.RepricingPercent / 100m)))),
                StringComparer.OrdinalIgnoreCase);
        var topResellers = customers
            .Where(c => c.OwningLinkId != null)
            .GroupBy(c => c.OwningLinkId!, StringComparer.OrdinalIgnoreCase)
            .Where(g => linkById.ContainsKey(g.Key))
            .Select(g => new
            {
                Link = linkById[g.Key],
                Customers = g.Count(),
                Seats = g.Sum(c => seatsByCustomer.GetValueOrDefault(c.CustomerId)),
            })
            .OrderByDescending(x => x.Seats)
            .ThenByDescending(x => x.Customers)
            .Take(15)
            .Select(r =>
            {
                pricingByLink.TryGetValue(r.Link.LinkId, out var p);
                return new DashboardResellerCustomers
                {
                    Reseller = EstateRollups.LinkLabel(r.Link),
                    CustomerCount = r.Customers,
                    SeatCount = r.Seats,
                    WholesaleMonthly = decimal.Round(p.Wholesale, 2),
                    MarginMonthly = decimal.Round(p.Revenue - p.Wholesale, 2),
                };
            })
            .ToList();

        return new DashboardSummary
        {
            CustomerCount = customerCount,
            IndirectCustomerCount = indirectCount,
            ActiveEntitlementCount = active,
            TrialEntitlementCount = trials,
            SuspendedEntitlementCount = suspended,
            ActiveSeats = seats,
            SkippedCustomerCount = 0,
            IncompleteReason = null,
            CustomersOnboarded = BuildMonthlySeries(
                customers.Where(c => c.IsResellerSelf).Select(c => c.CreateTime),
                customers.Where(c => !c.IsResellerSelf).Select(c => c.CreateTime)),
            ProductMix = mix,
            DirectProductMix = directMix,
            IndirectProductMix = indirectMix,
            OnboardedByReseller = onboardedByReseller,
            EstablishedLinks = establishedLinks,
            TopIndirectResellers = topResellers,
            EstateValue = ComputeEstateValue(activeEntitlements),
        };
    }

    public static DashboardOverview BuildOverview(IReadOnlyList<CustomerRecord> customers, IReadOnlyList<ResellerLinkRecord> links)
    {
        var linkStates = links
            .GroupBy(l => l.LinkState)
            .Select(g => new DashboardChannelLinkState { State = g.Key, Count = g.Count() })
            .OrderByDescending(s => s.Count)
            .ToList();

        return new DashboardOverview
        {
            CustomerCount = customers.Count(c => c.IsResellerSelf),
            ChannelLinkCount = links.Count,
            ChannelLinkStates = linkStates,
            CustomersOnboarded = BuildMonthlySeries(
                customers.Where(c => c.IsResellerSelf).Select(c => c.CreateTime),
                customers.Where(c => !c.IsResellerSelf).Select(c => c.CreateTime)),
        };
    }

    /// <summary>
    /// Estimated monthly estate value across all active, priced entitlements (direct + indirect), reported in
    /// the estate's dominant currency with a per-currency breakdown split by source. Null when nothing is priced.
    /// </summary>
    public static DashboardEstateValue? ComputeEstateValue(IReadOnlyList<EntitlementRecord> activeEntitlements)
    {
        var priced = activeEntitlements.Where(e => e.UnitPrice > 0 && e.Currency != null).ToList();
        if (priced.Count == 0)
        {
            return null;
        }

        static DashboardEstateValueScope Scope(IEnumerable<EntitlementRecord> rows)
        {
            var list = rows.ToList();
            var wholesale = list.Sum(e => (e.IonCostPerSeat ?? e.UnitPrice) * e.BillableSeats);
            var revenue = list.Sum(e => e.UnitPrice * e.BillableSeats * (1 + (e.RepricingPercent / 100m)));
            return new DashboardEstateValueScope
            {
                WholesaleMonthly = decimal.Round(wholesale, 2),
                RevenueMonthly = decimal.Round(revenue, 2),
                MarginMonthly = decimal.Round(revenue - wholesale, 2),
                PricedEntitlementCount = list.Count,
            };
        }

        var currencies = priced
            .GroupBy(e => e.Currency!)
            .Select(g =>
            {
                var wholesale = g.Sum(e => (e.IonCostPerSeat ?? e.UnitPrice) * e.BillableSeats);
                var revenue = g.Sum(e => e.UnitPrice * e.BillableSeats * (1 + (e.RepricingPercent / 100m)));
                return new DashboardEstateValueCurrency
                {
                    Currency = g.Key,
                    WholesaleMonthly = decimal.Round(wholesale, 2),
                    RevenueMonthly = decimal.Round(revenue, 2),
                    MarginMonthly = decimal.Round(revenue - wholesale, 2),
                    PricedEntitlementCount = g.Count(),
                    Direct = Scope(g.Where(e => e.IsResellerSelf)),
                    Indirect = Scope(g.Where(e => !e.IsResellerSelf)),
                };
            })
            .OrderByDescending(x => x.WholesaleMonthly)
            .ToList();

        var dominant = currencies[0];
        return new DashboardEstateValue
        {
            Currency = dominant.Currency,
            WholesaleMonthly = dominant.WholesaleMonthly,
            RevenueMonthly = dominant.RevenueMonthly,
            MarginMonthly = dominant.MarginMonthly,
            MixedCurrencies = currencies.Count > 1,
            PricedEntitlementCount = currencies.Sum(c => c.PricedEntitlementCount),
            UnpricedEntitlementCount = activeEntitlements.Count(e => e.UnitPrice <= 0),
            Direct = dominant.Direct,
            Indirect = dominant.Indirect,
            Currencies = currencies,
        };
    }

    /// <summary>
    /// Buckets customer create-times across the full available history (earliest month → now), split into
    /// direct and indirect so the chart renders a line per source. Implausible dates (year &lt; 2000) are ignored.
    /// </summary>
    public static List<DashboardMonthlyPoint> BuildMonthlySeries(IEnumerable<DateTimeOffset?> directDates, IEnumerable<DateTimeOffset?> indirectDates)
    {
        static List<DateTimeOffset> Clean(IEnumerable<DateTimeOffset?> dates) =>
            dates.Where(t => t is { Year: >= 2000 }).Select(t => t!.Value).ToList();

        var direct = Clean(directDates);
        var indirect = Clean(indirectDates);
        var all = direct.Concat(indirect).ToList();

        var now = DateTimeOffset.UtcNow;
        var lastMonth = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset firstMonth;
        if (all.Count > 0)
        {
            var earliest = all.Min();
            firstMonth = new DateTimeOffset(earliest.Year, earliest.Month, 1, 0, 0, 0, TimeSpan.Zero);
        }
        else
        {
            firstMonth = lastMonth.AddMonths(-5);
        }

        var points = new List<DashboardMonthlyPoint>();
        for (var m = firstMonth; m <= lastMonth; m = m.AddMonths(1))
        {
            var monthEnd = m.AddMonths(1);
            var d = direct.Count(v => v >= m && v < monthEnd);
            var i = indirect.Count(v => v >= m && v < monthEnd);
            points.Add(new DashboardMonthlyPoint
            {
                MonthKey = m.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                Month = m.ToString("MMM", CultureInfo.InvariantCulture),
                Year = m.Year,
                DirectCustomers = d,
                IndirectCustomers = i,
                Customers = d + i
            });
        }
        return points;
    }

    /// <summary>
    /// When the LIVE aggregation skipped customers (typically 429s), replace the partial direct figures with
    /// read-model totals so the headline KPIs fill in instead of under-reporting. No-op without read-model rows.
    /// </summary>
    public static async Task<DashboardSummary> BackfillFromReadModelAsync(DashboardSummary summary, IQuerySession session, CancellationToken ct)
    {
        if (summary.SkippedCustomerCount <= 0)
        {
            return summary;
        }

        var rows = await session.Query<EntitlementRecord>().Where(e => !e.IsDeleted && e.OwningLinkId == null).ToListAsync(ct);
        if (rows.Count == 0)
        {
            return summary;
        }

        var active = rows.Count(e => EstateRollups.IsActive(e) && !e.IsTrial);
        var trials = rows.Count(e => e.IsTrial);
        var suspended = rows.Count(EstateRollups.IsSuspended);
        var seats = rows.Where(EstateRollups.IsActive).Sum(e => e.Seats);
        var mix = rows.Where(EstateRollups.IsActive)
            .GroupBy(e => e.ProductName ?? e.ProductId ?? "Other")
            .Select(g => new DashboardProductSlice { Product = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .Take(8)
            .ToList();

        return summary with
        {
            ActiveEntitlementCount = Math.Max(summary.ActiveEntitlementCount, active),
            TrialEntitlementCount = Math.Max(summary.TrialEntitlementCount, trials),
            SuspendedEntitlementCount = Math.Max(summary.SuspendedEntitlementCount, suspended),
            ActiveSeats = Math.Max(summary.ActiveSeats, seats),
            SkippedCustomerCount = 0,
            IncompleteReason = null,
            ProductMix = summary.ProductMix is { Count: > 0 } ? summary.ProductMix : mix,
        };
    }
}
