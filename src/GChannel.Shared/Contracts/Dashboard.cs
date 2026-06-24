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
