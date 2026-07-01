namespace GChannel.Shared.Contracts;

// §10 read-model contracts: server-side paged/sorted/filtered estate views backed by SQL, so the
// customer and channel-partner-link list pages stay fast at distributor scale instead of loading the
// whole estate in memory. Every result carries an "as-of" timestamp (the oldest LastSyncedUtc in the
// page) so the UI can honestly show how fresh the synced data is.

/// <summary>A single page of read-model rows plus the total row count and freshness timestamp.</summary>
public sealed record PagedEstateResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = [];
    public int Total { get; init; }

    /// <summary>Oldest <c>LastSyncedUtc</c> across the returned rows (the page's freshness), or null when empty.</summary>
    public DateTimeOffset? AsOf { get; init; }
}

/// <summary>A customer row from the read-model (direct or indirect).</summary>
public sealed record EstateCustomer
{
    public required string CustomerId { get; init; }
    public string? OrgName { get; init; }
    public string? Domain { get; init; }
    public string? CloudIdentityId { get; init; }
    public string? OwningLinkId { get; init; }
    public long SeatCount { get; init; }
    public DateTimeOffset? CreateTime { get; init; }
    public DateTimeOffset LastSyncedUtc { get; init; }

    /// <summary>
    /// Estimated monthly value of the customer's active entitlements, repricing mark-up applied
    /// (Σ unit price × seats × (1 + markup%)). Null when the customer has no priced active
    /// entitlements synced yet. Estimated from offer list pricing — not an invoiced figure.
    /// </summary>
    public decimal? EstimatedMonthlyTotal { get; init; }

    /// <summary>Currency of <see cref="EstimatedMonthlyTotal"/> (the customer's dominant currency).</summary>
    public string? Currency { get; init; }

    /// <summary>Number of the customer's entitlements in the ACTIVE state.</summary>
    public int ActiveSubscriptions { get; init; }

    /// <summary>Number of the customer's entitlements in the SUSPENDED state.</summary>
    public int SuspendedSubscriptions { get; init; }

    /// <summary>Earliest upcoming commitment end (renewal) date across the customer's active entitlements, or null when none commit.</summary>
    public DateTimeOffset? NextRenewalUtc { get; init; }

    /// <summary>Friendly offer name of the entitlement renewing at <see cref="NextRenewalUtc"/>.</summary>
    public string? NextRenewalOfferName { get; init; }
}

/// <summary>A reseller (channel partner link) row from the read-model.</summary>
public sealed record EstateReseller
{
    public required string LinkId { get; init; }
    public string? PrimaryDomain { get; init; }
    public string? ResellerCloudId { get; init; }
    public string LinkState { get; init; } = string.Empty;
    public int CustomerCount { get; init; }
    public DateTimeOffset? CreateTime { get; init; }
    public DateTimeOffset LastSyncedUtc { get; init; }
    public string? SyncError { get; init; }
}

/// <summary>
/// A single entitlement (subscription) row from the read-model, joined to its customer's org name.
/// Powers the estate-wide entitlements list the dashboard lifecycle KPIs link into.
/// </summary>
public sealed record EstateEntitlement
{
    public required string EntitlementId { get; init; }
    public required string CustomerId { get; init; }
    /// <summary>Owning customer's organisation display name (joined from the customer read-model).</summary>
    public string? CustomerName { get; init; }
    /// <summary>Owning channel partner link id, or null for the account's direct customers.</summary>
    public string? OwningLinkId { get; init; }
    public string? ProductName { get; init; }
    public string? SkuName { get; init; }
    public string? OfferName { get; init; }
    public string State { get; init; } = string.Empty;
    public bool IsTrial { get; init; }
    public long Seats { get; init; }
    /// <summary>Wholesale effective per-seat price (the reseller's cost from Google). 0 if unknown.</summary>
    public decimal UnitPrice { get; init; }
    /// <summary>ISO currency code for <see cref="UnitPrice"/>, or null when no price was resolved.</summary>
    public string? Currency { get; init; }
    /// <summary>Repricing mark-up percent applied to this entitlement (§6).</summary>
    public decimal RepricingPercent { get; init; }
    /// <summary>Commitment/renewal end time, or null when the plan doesn't commit.</summary>
    public DateTimeOffset? CommitmentEndTime { get; init; }
    public DateTimeOffset? CreateTime { get; init; }
    public DateTimeOffset LastSyncedUtc { get; init; }
}
