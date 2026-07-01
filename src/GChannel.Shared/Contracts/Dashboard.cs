namespace GChannel.Shared.Contracts;

// Dashboard contracts shared between the Web client and the API service. The home-page figures have
// no single Channel API reporting endpoint (the legacy accounts.reports.* API is deprecated in v1),
// so the API derives them by aggregating the customer (§2) and entitlement (§3) read paths.

/// <summary>Aggregated reseller overview figures shown on the home dashboard.</summary>
public sealed record DashboardSummary
{
    /// <summary>
    /// Direct customers owned by this account (<c>accounts.customers</c>) — same figure as
    /// <see cref="DashboardOverview.CustomerCount"/>.
    /// </summary>
    public int CustomerCount { get; init; }

    /// <summary>
    /// Downstream end customers owned by linked indirect resellers, summed across every ACTIVE
    /// channel partner link (<c>accounts.channelPartnerLinks.customers.list</c>). This is a separate
    /// set from the direct <see cref="CustomerCount"/> (a distributor's <c>accounts.customers.list</c>
    /// returns only its own direct customers), so the total estate is the two added together. Computed
    /// by the (unbudgeted) background refresher because it costs one customer-list call per reseller.
    /// </summary>
    public int IndirectCustomerCount { get; init; }

    /// <summary>
    /// Indirect resellers ranked by how many downstream customers they own (most first), for the
    /// "top resellers" chart. Built from the same per-reseller customer-list fan-out as
    /// <see cref="IndirectCustomerCount"/> and labelled with each link's primary domain when available.
    /// </summary>
    public IReadOnlyList<DashboardResellerCustomers> TopIndirectResellers { get; init; } = [];

    /// <summary>Entitlements currently in the <c>ACTIVE</c> provisioning state.</summary>
    public int ActiveEntitlementCount { get; init; }

    /// <summary>Entitlements still in a trial.</summary>
    public int TrialEntitlementCount { get; init; }

    /// <summary>Entitlements currently <c>SUSPENDED</c>.</summary>
    public int SuspendedEntitlementCount { get; init; }

    /// <summary>Sum of seats (<c>num_units</c>) across active entitlements.</summary>
    public long ActiveSeats { get; init; }

    /// <summary>Customers whose entitlements could not be loaded (skipped during aggregation).</summary>
    public int SkippedCustomerCount { get; init; }

    /// <summary>
    /// Human-readable explanation of why some customers were skipped (e.g. the aggregation time
    /// budget was hit, or specific API errors), or <c>null</c> when nothing was skipped.
    /// </summary>
    public string? IncompleteReason { get; init; }

    /// <summary>Customers onboarded per month over the trailing 6 months (oldest first).</summary>
    public IReadOnlyList<DashboardMonthlyPoint> CustomersOnboarded { get; init; } = [];

    /// <summary>Active entitlements grouped by product (for the product-mix donut). Spans the whole estate (direct + indirect).</summary>
    public IReadOnlyList<DashboardProductSlice> ProductMix { get; init; } = [];

    /// <summary>Active entitlements grouped by product for <b>direct</b> customers only (account-owned).</summary>
    public IReadOnlyList<DashboardProductSlice> DirectProductMix { get; init; } = [];

    /// <summary>Active entitlements grouped by product for <b>indirect</b> customers only (owned by downstream resellers).</summary>
    public IReadOnlyList<DashboardProductSlice> IndirectProductMix { get; init; } = [];

    /// <summary>
    /// Estimated monthly estate value (wholesale cost, repriced revenue and margin) derived from
    /// offer list pricing (§11) and repricing configs (§6), or <c>null</c> when pricing wasn't
    /// resolved (e.g. the read-model overlay is off). These are non-invoiced estimates.
    /// </summary>
    public DashboardEstateValue? EstateValue { get; init; }
}

/// <summary>
/// Cheap first phase of the dashboard: the headline customer count and onboarded-over-time chart,
/// which need only the customer list (no per-customer entitlement calls). The UI renders these
/// immediately, then fills in the entitlement figures from <see cref="DashboardSummary"/>.
/// </summary>
public sealed record DashboardOverview
{
    /// <summary>
    /// Direct customers owned by this account (<c>accounts.customers</c>) — i.e. end customers we
    /// transact with directly rather than through a downstream indirect reseller. The reseller estate
    /// is counted separately in <see cref="DashboardSummary.IndirectCustomerCount"/>.
    /// </summary>
    public int CustomerCount { get; init; }

    /// <summary>Channel partner links (§5) on the reseller account, across all states.</summary>
    public int ChannelLinkCount { get; init; }

    /// <summary>Channel partner links broken down by link state (ACTIVE, INVITED, SUSPENDED, …).</summary>
    public IReadOnlyList<DashboardChannelLinkState> ChannelLinkStates { get; init; } = [];

    /// <summary>Customers onboarded per month over the trailing 6 months (oldest first).</summary>
    public IReadOnlyList<DashboardMonthlyPoint> CustomersOnboarded { get; init; } = [];
}

/// <summary>An indirect reseller and its downstream estate (customers + seats), for the top-resellers chart.</summary>
public sealed record DashboardResellerCustomers
{
    /// <summary>Friendly reseller label — the channel partner link's primary domain, else its id.</summary>
    public required string Reseller { get; init; }

    /// <summary>Number of downstream end customers owned by this reseller.</summary>
    public int CustomerCount { get; init; }

    /// <summary>
    /// Total active seats (<c>num_units</c>) across all of this reseller's downstream customers'
    /// entitlements. The top-resellers chart ranks by this so a reseller with many small customers
    /// doesn't outrank one with fewer but much larger customers.
    /// </summary>
    public long SeatCount { get; init; }

    /// <summary>Estimated monthly wholesale cost (the reseller's Google cost) across this reseller's active entitlements.</summary>
    public decimal WholesaleMonthly { get; init; }

    /// <summary>Estimated monthly margin (repriced revenue minus wholesale cost) for this reseller.</summary>
    public decimal MarginMonthly { get; init; }
}

/// <summary>
/// Estimated monthly estate value rollup (§11). All figures are non-invoiced estimates derived from
/// offer <em>list</em> pricing and configured repricing mark-ups, summed over active entitlements.
/// </summary>
public sealed record DashboardEstateValue
{
    /// <summary>ISO currency code the figures are reported in (the estate's dominant currency).</summary>
    public required string Currency { get; init; }

    /// <summary>Estimated monthly wholesale cost (sum of offer effective price × seats) — what the reseller pays Google.</summary>
    public decimal WholesaleMonthly { get; init; }

    /// <summary>Estimated monthly revenue after applying repricing mark-ups (§6) — what end customers are billed.</summary>
    public decimal RevenueMonthly { get; init; }

    /// <summary>Estimated monthly margin (<see cref="RevenueMonthly"/> − <see cref="WholesaleMonthly"/>).</summary>
    public decimal MarginMonthly { get; init; }

    /// <summary>True when active entitlements span more than one currency (see <see cref="Currencies"/>).</summary>
    public bool MixedCurrencies { get; init; }

    /// <summary>Total active entitlements with a resolved price that are included in the totals (across all currencies).</summary>
    public int PricedEntitlementCount { get; init; }

    /// <summary>Active entitlements whose offer price couldn't be resolved (excluded from the totals).</summary>
    public int UnpricedEntitlementCount { get; init; }

    /// <summary>
    /// Direct slice (your own customers, no owning channel link) of the headline/dominant currency —
    /// so the estate value can show what comes from direct business vs downstream resellers.
    /// </summary>
    public DashboardEstateValueScope Direct { get; init; } = new();

    /// <summary>Indirect (reseller-owned) slice of the headline/dominant currency.</summary>
    public DashboardEstateValueScope Indirect { get; init; } = new();

    /// <summary>
    /// Per-currency breakdown (dominant currency first). Every priced currency is reported on its own
    /// line so non-dominant currencies aren't dropped; the headline fields above mirror the first entry.
    /// </summary>
    public IReadOnlyList<DashboardEstateValueCurrency> Currencies { get; init; } = [];
}

/// <summary>
/// One source slice (direct or reseller-owned) of the estimated monthly estate value, in a single
/// currency. Lets the dashboard split the estate value into direct vs indirect (reseller) business.
/// </summary>
public sealed record DashboardEstateValueScope
{
    /// <summary>Estimated monthly wholesale cost for this source (offer effective price × seats).</summary>
    public decimal WholesaleMonthly { get; init; }

    /// <summary>Estimated monthly repriced revenue for this source.</summary>
    public decimal RevenueMonthly { get; init; }

    /// <summary>Estimated monthly margin for this source (<see cref="RevenueMonthly"/> − <see cref="WholesaleMonthly"/>).</summary>
    public decimal MarginMonthly { get; init; }

    /// <summary>Active priced entitlements counted in this source slice.</summary>
    public int PricedEntitlementCount { get; init; }
}

/// <summary>One currency's slice of the estimated monthly estate value rollup (§11).</summary>
public sealed record DashboardEstateValueCurrency
{
    /// <summary>ISO currency code these figures are reported in.</summary>
    public required string Currency { get; init; }

    /// <summary>Estimated monthly wholesale cost in this currency (offer effective price × seats).</summary>
    public decimal WholesaleMonthly { get; init; }

    /// <summary>Estimated monthly repriced revenue in this currency.</summary>
    public decimal RevenueMonthly { get; init; }

    /// <summary>Estimated monthly margin in this currency (<see cref="RevenueMonthly"/> − <see cref="WholesaleMonthly"/>).</summary>
    public decimal MarginMonthly { get; init; }

    /// <summary>Active priced entitlements counted in this currency.</summary>
    public int PricedEntitlementCount { get; init; }

    /// <summary>Direct (your own customers) slice of this currency's total.</summary>
    public DashboardEstateValueScope Direct { get; init; } = new();

    /// <summary>Indirect (reseller-owned) slice of this currency's total.</summary>
    public DashboardEstateValueScope Indirect { get; init; } = new();
}

/// <summary>A count of channel partner links in a given link state, for the dashboard breakdown.</summary>
public sealed record DashboardChannelLinkState
{
    /// <summary>Link state, e.g. <c>ACTIVE</c>, <c>INVITED</c>, <c>SUSPENDED</c>, <c>REVOKED</c>.</summary>
    public required string State { get; init; }

    public int Count { get; init; }
}

/// <summary>A single month bucket of onboarded customers.</summary>
public sealed record DashboardMonthlyPoint
{
    /// <summary>Abbreviated month label, e.g. "Jan".</summary>
    public required string Month { get; init; }

    public int Customers { get; init; }
}

/// <summary>A product slice of the active-entitlement product mix.</summary>
public sealed record DashboardProductSlice
{
    /// <summary>Friendly product name (falls back to the product id).</summary>
    public required string Product { get; init; }

    public int Count { get; init; }
}

/// <summary>
/// Health/freshness of the background dashboard refresher, surfaced on the home page so users can see
/// when the figures were last recomputed and whether a refresh is in progress.
/// </summary>
public sealed record DashboardRefreshStatus
{
    /// <summary>Whether the background refresher is configured and running (vs. on-demand only).</summary>
    public bool Enabled { get; init; }

    /// <summary>True while a background recompute is currently in progress.</summary>
    public bool IsRunning { get; init; }

    /// <summary>When the most recent background recompute started, or <c>null</c> if it never has.</summary>
    public DateTimeOffset? LastStartedUtc { get; init; }

    /// <summary>When the most recent background recompute completed, or <c>null</c> if none has finished.</summary>
    public DateTimeOffset? LastCompletedUtc { get; init; }

    /// <summary>Wall-clock duration of the last completed recompute, in seconds.</summary>
    public int? LastDurationSeconds { get; init; }

    /// <summary>How many customers the last completed recompute skipped (genuine per-customer errors).</summary>
    public int? LastSkippedCount { get; init; }

    /// <summary>
    /// Estimated time the next background recompute will begin, or <c>null</c> when the refresher is
    /// disabled / hasn't run yet. An estimate based on the configured interval and the last run.
    /// </summary>
    public DateTimeOffset? NextRefreshUtc { get; init; }
}
