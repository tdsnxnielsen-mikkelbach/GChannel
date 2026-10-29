using GChannel.Core.Estate.ReadModels;
using GChannel.Shared.Contracts;

namespace GChannel.Core.Estate;

/// <summary>
/// In-memory rollups over projected <see cref="EntitlementRecord"/> documents. The estate is at most a few
/// thousand entitlements, so grouping in memory is cheap and keeps the query side free of SQL GROUP BY.
/// </summary>
public static class EstateRollups
{
    /// <summary>Per-customer figures shown on the customers list / detail pages.</summary>
    public sealed record CustomerRollup(
        long SeatCount,
        int Active,
        int Suspended,
        decimal? MonthlyTotal,
        string? Currency,
        DateTimeOffset? NextRenewal,
        string? NextRenewalOffer,
        bool? NextRenewalAutoRenew);

    public static bool IsActive(EntitlementRecord e) => string.Equals(e.State, "ACTIVE", StringComparison.OrdinalIgnoreCase);
    public static bool IsSuspended(EntitlementRecord e) => string.Equals(e.State, "SUSPENDED", StringComparison.OrdinalIgnoreCase);

    /// <summary>Groups live entitlements by customer and computes seats, state counts, estimated monthly value and next renewal.</summary>
    public static Dictionary<string, CustomerRollup> ByCustomer(IEnumerable<EntitlementRecord> entitlements, DateTimeOffset now)
    {
        return entitlements
            .Where(e => !e.IsDeleted)
            .GroupBy(e => e.CustomerId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g =>
            {
                var active = g.Count(IsActive);
                var suspended = g.Count(IsSuspended);
                var seats = g.Where(IsActive).Sum(e => e.Seats);

                // Estimated monthly value: Σ over active priced entitlements of unit price × billable seats ×
                // (1 + markup%), in the customer's dominant currency.
                var priced = g.Where(e => IsActive(e) && e.UnitPrice > 0 && e.BillableSeats > 0).ToList();
                decimal? monthly = null;
                string? currency = null;
                if (priced.Count > 0)
                {
                    var dominant = priced.GroupBy(e => e.Currency ?? "")
                        .OrderByDescending(cg => cg.Sum(e => e.UnitPrice * e.BillableSeats))
                        .First();
                    monthly = dominant.Sum(e => e.UnitPrice * e.BillableSeats * (1 + (e.RepricingPercent / 100m)));
                    currency = string.IsNullOrEmpty(dominant.Key) ? null : dominant.Key;
                }

                var nextRenewal = g
                    .Where(e => IsActive(e) && e.CommitmentEndTime != null && e.CommitmentEndTime > now)
                    .OrderBy(e => e.CommitmentEndTime)
                    .FirstOrDefault();

                return new CustomerRollup(seats, active, suspended, monthly, currency,
                    nextRenewal?.CommitmentEndTime, nextRenewal?.OfferName, nextRenewal?.RenewalEnabled);
            }, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Per-currency wholesale / repriced revenue / margin over a set of active entitlements (dominant currency first).</summary>
    public static List<ResellerEstateValueCurrency> ValueByCurrency(IEnumerable<EntitlementRecord> active)
        => active
            .Where(e => e.UnitPrice > 0 && e.Currency != null)
            .GroupBy(e => e.Currency!)
            .Select(g =>
            {
                var wholesale = g.Sum(e => (e.IonCostPerSeat ?? e.UnitPrice) * e.BillableSeats);
                var revenue = g.Sum(e => e.UnitPrice * e.BillableSeats * (1 + (e.RepricingPercent / 100m)));
                return new ResellerEstateValueCurrency
                {
                    Currency = g.Key,
                    WholesaleMonthly = decimal.Round(wholesale, 2),
                    RevenueMonthly = decimal.Round(revenue, 2),
                    MarginMonthly = decimal.Round(revenue - wholesale, 2),
                    PricedEntitlementCount = g.Count(),
                    ActiveSeats = g.Sum(e => e.BillableSeats),
                };
            })
            .OrderByDescending(x => x.WholesaleMonthly)
            .ToList();

    /// <summary>Friendly reseller label for a link: primary domain, else reseller cloud id, else the link id.</summary>
    public static string LinkLabel(ResellerLinkRecord l) => l.PrimaryDomain ?? l.ResellerCloudId ?? l.LinkId;
}
