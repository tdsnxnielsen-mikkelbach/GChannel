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
    /// Friendly name of the indirect reseller (channel partner link) that owns this customer — the
    /// link's primary domain, else its reseller cloud id, else the link id. Null for direct customers
    /// (<see cref="OwningLinkId"/> null).
    /// </summary>
    public string? ResellerName { get; init; }

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

    /// <summary>
    /// Whether auto-renewal is enabled for the entitlement renewing at <see cref="NextRenewalUtc"/>
    /// (matches the Renewal column). Null when no upcoming renewal or the entitlement has no renewal setting.
    /// </summary>
    public bool? NextRenewalAutoRenew { get; init; }
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
    /// <summary>Committed/billable seats (num_units only) — the monthly estimate uses this so a flexible plan's max_units cap doesn't inflate it.</summary>
    public long BillableSeats { get; init; }
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

/// <summary>
/// Estimated estate value for a single reseller (channel partner link): the wholesale cost, repriced
/// revenue and margin across all of that reseller's customers' active priced entitlements, from the
/// read-model. Headline figures are in the reseller's dominant currency; <see cref="Currencies"/> carries
/// the per-currency breakdown for multi-currency resellers.
/// </summary>
public sealed record ResellerEstateValue
{
    /// <summary>Dominant currency (largest wholesale), or null when nothing is priced yet.</summary>
    public string? Currency { get; init; }
    /// <summary>Wholesale cost (the distributor's cost from Google) in the dominant currency.</summary>
    public decimal WholesaleMonthly { get; init; }
    /// <summary>Repriced revenue (what the reseller is billed) in the dominant currency.</summary>
    public decimal RevenueMonthly { get; init; }
    /// <summary>Margin (revenue − wholesale) in the dominant currency — your rebilling mark-up on this reseller.</summary>
    public decimal MarginMonthly { get; init; }
    public bool MixedCurrencies { get; init; }
    public int PricedEntitlementCount { get; init; }
    public int UnpricedEntitlementCount { get; init; }
    public long ActiveSeats { get; init; }
    public int CustomerCount { get; init; }
    public IReadOnlyList<ResellerEstateValueCurrency> Currencies { get; init; } = [];
}

/// <summary>One currency's slice of a reseller's estimated estate value.</summary>
public sealed record ResellerEstateValueCurrency
{
    public required string Currency { get; init; }
    public decimal WholesaleMonthly { get; init; }
    public decimal RevenueMonthly { get; init; }
    public decimal MarginMonthly { get; init; }
    public int PricedEntitlementCount { get; init; }
    public long ActiveSeats { get; init; }
}

/// <summary>
/// Estimated monthly value for a single customer: the wholesale cost, repriced revenue and margin across
/// that customer's active priced entitlements, from the read-model. Headline figures are in the customer's
/// dominant currency; <see cref="Currencies"/> carries the per-currency breakdown for multi-currency
/// customers. Mirrors <see cref="ResellerEstateValue"/> but scoped to one customer (no customer count).
/// </summary>
public sealed record CustomerEstateValue
{
    /// <summary>Dominant currency (largest wholesale), or null when nothing is priced yet.</summary>
    public string? Currency { get; init; }
    /// <summary>Wholesale cost (what you pay Google) in the dominant currency.</summary>
    public decimal WholesaleMonthly { get; init; }
    /// <summary>Repriced revenue (what this customer is billed) in the dominant currency.</summary>
    public decimal RevenueMonthly { get; init; }
    /// <summary>Margin (revenue − wholesale) in the dominant currency.</summary>
    public decimal MarginMonthly { get; init; }
    public bool MixedCurrencies { get; init; }
    public int PricedEntitlementCount { get; init; }
    public int UnpricedEntitlementCount { get; init; }
    public long ActiveSeats { get; init; }
    public IReadOnlyList<ResellerEstateValueCurrency> Currencies { get; init; } = [];
}
