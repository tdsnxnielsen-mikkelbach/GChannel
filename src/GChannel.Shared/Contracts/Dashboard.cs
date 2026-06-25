namespace GChannel.Shared.Contracts;

// Dashboard contracts shared between the Web client and the API service. The home-page figures have
// no single Channel API reporting endpoint (the legacy accounts.reports.* API is deprecated in v1),
// so the API derives them by aggregating the customer (§2) and entitlement (§3) read paths.

/// <summary>Aggregated reseller overview figures shown on the home dashboard.</summary>
public sealed record DashboardSummary
{
    /// <summary>Total customers linked to the reseller account.</summary>
    public int CustomerCount { get; init; }

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

    /// <summary>Active entitlements grouped by product (for the product-mix donut).</summary>
    public IReadOnlyList<DashboardProductSlice> ProductMix { get; init; } = [];
}

/// <summary>
/// Cheap first phase of the dashboard: the headline customer count and onboarded-over-time chart,
/// which need only the customer list (no per-customer entitlement calls). The UI renders these
/// immediately, then fills in the entitlement figures from <see cref="DashboardSummary"/>.
/// </summary>
public sealed record DashboardOverview
{
    /// <summary>Total customers linked to the reseller account.</summary>
    public int CustomerCount { get; init; }

    /// <summary>Channel partner links (§5) on the reseller account, across all states.</summary>
    public int ChannelLinkCount { get; init; }

    /// <summary>Customers onboarded per month over the trailing 6 months (oldest first).</summary>
    public IReadOnlyList<DashboardMonthlyPoint> CustomersOnboarded { get; init; } = [];
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
}
