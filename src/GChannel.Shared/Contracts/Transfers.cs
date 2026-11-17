namespace GChannel.Shared.Contracts;

// Transfer contracts shared between the Web client and the API service. These mirror a trimmed,
// UI-friendly subset of the Cloud Channel transfer resources so UI code never references Google REST
// shapes directly. Transfers bring a customer's existing entitlements (held directly with Google or
// with another reseller) into this reseller's account (§4), or hand them back to Google. They tie a
// Customer (§2) to a SKU/Offer from the Catalog (§1), mirroring the entitlement lifecycle (§3).

/// <summary>A SKU a customer currently holds that could be transferred in. Maps to <c>accounts.listTransferableSkus</c>.</summary>
public sealed record TransferableSku
{
    /// <summary>SKU resource name, e.g. "products/{product}/skus/{sku}".</summary>
    public required string SkuName { get; init; }

    /// <summary>Short SKU id (for navigation to the Catalog and to load transferable offers).</summary>
    public string? SkuId { get; init; }

    /// <summary>Id of the product the SKU belongs to (for Catalog correlation).</summary>
    public string? ProductId { get; init; }

    /// <summary>Human-friendly SKU name (falls back to <see cref="SkuId"/> in the UI).</summary>
    public string? SkuDisplayName { get; init; }

    /// <summary>Human-friendly product name (falls back to <see cref="ProductId"/> in the UI).</summary>
    public string? ProductDisplayName { get; init; }

    /// <summary>True when the SKU is eligible to be transferred to this reseller.</summary>
    public bool IsEligible { get; init; }

    /// <summary>Reason the SKU is not eligible to transfer (when <see cref="IsEligible"/> is false).</summary>
    public string? IneligibilityReason { get; init; }

    /// <summary>Localized human-readable description of the eligibility result.</summary>
    public string? EligibilityDescription { get; init; }

    /// <summary>Legacy SKU id, when the transferable SKU maps to a legacy SKU.</summary>
    public string? LegacySku { get; init; }
}

/// <summary>Result of listing a customer's transferable SKUs.</summary>
public sealed record TransferableSkusResult
{
    public IReadOnlyList<TransferableSku> Skus { get; init; } = [];
}

/// <summary>An offer a customer is eligible to transfer in for a SKU. Maps to <c>accounts.listTransferableOffers</c>.</summary>
public sealed record TransferableOffer
{
    /// <summary>Offer resource name, e.g. "accounts/{account}/offers/{offer}".</summary>
    public required string OfferName { get; init; }

    /// <summary>Short offer id (the last path segment of <see cref="OfferName"/>).</summary>
    public string? OfferId { get; init; }

    public string? OfferDisplayName { get; init; }

    /// <summary>Id of the SKU this offer relates to (for Catalog correlation).</summary>
    public string? SkuId { get; init; }

    public string? SkuDisplayName { get; init; }

    /// <summary>Id of the product the related SKU belongs to.</summary>
    public string? ProductId { get; init; }

    /// <summary>Wholesale list pricing per priced resource (seats, GB, etc.). Empty if not exposed.</summary>
    public IReadOnlyList<OfferPrice> Pricing { get; init; } = [];

    /// <summary>Human-friendly payment cycle (e.g. "Monthly", "Annual"). Null when unknown.</summary>
    public string? PaymentCycle { get; init; }
}

/// <summary>Result of listing a customer's transferable offers for a SKU.</summary>
public sealed record TransferableOffersResult
{
    public IReadOnlyList<TransferableOffer> Offers { get; init; } = [];
}

/// <summary>One entitlement to transfer (an Offer plus optional seats / purchase-order id).</summary>
public sealed record TransferEntitlementLine
{
    /// <summary>Offer id or full offer resource name to transfer.</summary>
    public required string OfferId { get; init; }

    public string? PurchaseOrderId { get; init; }
    public IReadOnlyList<EntitlementParameterInput> Parameters { get; init; } = [];

    /// <summary>
    /// Billing account resource name ("accounts/{account}/billingAccounts/{id}") to bill the transferred
    /// entitlement against. Required by Google for multi-currency reseller accounts; null otherwise.
    /// </summary>
    public string? BillingAccount { get; init; }

    /// <summary>Optional friendly offer name from the UI; only recorded in the operation log, never sent to Google.</summary>
    public string? OfferDisplayName { get; init; }
}

/// <summary>Transfer-in payload (<c>accounts.customers.transferEntitlements</c>).</summary>
public sealed record TransferEntitlementsRequest
{
    /// <summary>The entitlements (offers) to transfer to this reseller.</summary>
    public required IReadOnlyList<TransferEntitlementLine> Entitlements { get; init; }

    /// <summary>
    /// Optional transfer auth token the customer provides when transferring from another reseller.
    /// Not required when the customer is transferring from direct Google billing.
    /// </summary>
    public string? AuthToken { get; init; }
}

/// <summary>Transfer-to-Google payload (<c>accounts.customers.transferEntitlementsToGoogle</c>).</summary>
public sealed record TransferEntitlementsToGoogleRequest
{
    /// <summary>The entitlements (offers) to hand back to Google (direct) billing.</summary>
    public required IReadOnlyList<TransferEntitlementLine> Entitlements { get; init; }
}

/// <summary>One transferable SKU of one customer, from the estate-wide incoming-transfer scan.</summary>
public sealed record IncomingTransfer
{
    public required string CustomerId { get; init; }
    public string? CustomerName { get; init; }
    public string? Domain { get; init; }
    /// <summary>Owning channel partner link id (null for direct customers).</summary>
    public string? OwningLinkId { get; init; }
    /// <summary>Primary domain of the owning reseller, when known.</summary>
    public string? ResellerName { get; init; }
    public required TransferableSku Sku { get; init; }
    /// <summary>When the customer's transferability was last refreshed successfully.</summary>
    public DateTimeOffset? CheckedUtc { get; init; }
}

/// <summary>A customer whose last transferability check failed.</summary>
public sealed record IncomingTransferError
{
    public required string CustomerId { get; init; }
    public string? CustomerName { get; init; }
    public required string Error { get; init; }
    public DateTimeOffset CheckedUtc { get; init; }
}

/// <summary>Estate-wide incoming transfers (<c>GET /api/transfers/incoming</c>).</summary>
public sealed record IncomingTransfersResult
{
    public IReadOnlyList<IncomingTransfer> Items { get; init; } = [];
    public IReadOnlyList<IncomingTransferError> Errors { get; init; } = [];
    /// <summary>Customers checked at least once.</summary>
    public int CustomersScanned { get; init; }
    /// <summary>Customers with at least one eligible SKU.</summary>
    public int CustomersWithEligible { get; init; }
    /// <summary>When the last full scan finished.</summary>
    public DateTimeOffset? AsOf { get; init; }
    /// <summary>When the next nightly scan is scheduled.</summary>
    public DateTimeOffset? NextScanUtc { get; init; }
    /// <summary>True while a scan is running.</summary>
    public bool Scanning { get; init; }
    /// <summary>True when the scan cannot run (no service account / disabled).</summary>
    public bool Unconfigured { get; init; }
}
