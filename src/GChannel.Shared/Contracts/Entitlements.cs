namespace GChannel.Shared.Contracts;

// Entitlement lifecycle contracts shared between the Web client and the API service. These mirror a
// trimmed, UI-friendly subset of the Cloud Channel entitlement resources so UI code never references
// Google REST shapes directly. Entitlements are the "subscriptions" a customer holds and are the
// core selling artefact: they tie a customer to a SKU/Offer from the Catalog (§1) and hang off a
// Customer (§2).

/// <summary>A customer subscription. Maps to <c>accounts.customers.entitlements</c>.</summary>
public sealed record Entitlement
{
    /// <summary>Resource name, e.g. "accounts/{account}/customers/{customer}/entitlements/{id}".</summary>
    public required string Name { get; init; }

    /// <summary>Short id (the last path segment of <see cref="Name"/>).</summary>
    public required string Id { get; init; }

    /// <summary>Offer resource name backing this entitlement.</summary>
    public string? OfferName { get; init; }

    /// <summary>Offer short id (for navigation to the Catalog offer).</summary>
    public string? OfferId { get; init; }

    /// <summary>Human-friendly offer name resolved from the Catalog (falls back to <see cref="OfferId"/> in the UI).</summary>
    public string? OfferDisplayName { get; init; }

    /// <summary>Product id of the provisioned service (for Catalog correlation).</summary>
    public string? ProductId { get; init; }

    /// <summary>Human-friendly product name resolved from the Catalog (falls back to <see cref="ProductId"/> in the UI).</summary>
    public string? ProductDisplayName { get; init; }

    /// <summary>SKU id of the provisioned service (for Catalog correlation).</summary>
    public string? SkuId { get; init; }

    /// <summary>Human-friendly SKU name resolved from the Catalog (falls back to <see cref="SkuId"/> in the UI).</summary>
    public string? SkuDisplayName { get; init; }

    /// <summary>Provisioning state, e.g. ACTIVE / SUSPENDED / PENDING / CANCELLED.</summary>
    public string? ProvisioningState { get; init; }

    public string? PurchaseOrderId { get; init; }
    public string? BillingAccount { get; init; }
    public DateTimeOffset? CreateTime { get; init; }
    public DateTimeOffset? UpdateTime { get; init; }

    /// <summary>Reasons the entitlement is suspended (when state is SUSPENDED).</summary>
    public IReadOnlyList<string> SuspensionReasons { get; init; } = [];

    public bool IsTrial { get; init; }
    public DateTimeOffset? TrialEndTime { get; init; }

    public EntitlementCommitment? Commitment { get; init; }
    public IReadOnlyList<EntitlementParameter> Parameters { get; init; } = [];

    /// <summary>
    /// Estimated wholesale price per seat (per month) from the offer's list pricing. Null when the
    /// offer could not be priced. Populated from the §10 read-model; not an invoiced figure.
    /// </summary>
    public decimal? UnitPrice { get; init; }

    /// <summary>Currency of <see cref="UnitPrice"/>.</summary>
    public string? PriceCurrency { get; init; }

    /// <summary>Repricing (rebilling) mark-up percentage applied to this entitlement, when known.</summary>
    public decimal? RepricingPercent { get; init; }
}

/// <summary>Commitment / renewal summary for an entitlement. Maps to <c>CommitmentSettings</c>.</summary>
public sealed record EntitlementCommitment
{
    public DateTimeOffset? StartTime { get; init; }
    public DateTimeOffset? EndTime { get; init; }
    public bool? RenewalEnabled { get; init; }
    public string? PaymentPlan { get; init; }
}

/// <summary>A typed entitlement parameter (e.g. <c>num_units</c> = seat count). Maps to <c>Parameter</c>.</summary>
public sealed record EntitlementParameter
{
    public required string Name { get; init; }
    public string? Value { get; init; }
    public bool Editable { get; init; }
}

/// <summary>Result of listing a customer's entitlements.</summary>
public sealed record EntitlementsResult
{
    public IReadOnlyList<Entitlement> Entitlements { get; init; } = [];
}

/// <summary>A single change in an entitlement's history. Maps to <c>EntitlementChange</c>.</summary>
public sealed record EntitlementChange
{
    /// <summary>Type of change, e.g. CREATED / OFFER_CHANGED / SUSPENDED / ACTIVATED / CANCELLED.</summary>
    public string? ChangeType { get; init; }

    /// <summary>Offer short id active after this change (for Catalog correlation).</summary>
    public string? OfferId { get; init; }

    /// <summary>Human-friendly offer name resolved from the Catalog (falls back to <see cref="OfferId"/> in the UI).</summary>
    public string? OfferDisplayName { get; init; }

    /// <summary>Who initiated the change, e.g. CUSTOMER_SERVICE_REPRESENTATIVE / SYSTEM / RESELLER.</summary>
    public string? OperatorType { get; init; }

    public DateTimeOffset? CreateTime { get; init; }

    /// <summary>The single populated reason for the change (activation/cancellation/suspension/other).</summary>
    public string? Reason { get; init; }
}

/// <summary>Result of listing an entitlement's change history.</summary>
public sealed record EntitlementChangesResult
{
    public IReadOnlyList<EntitlementChange> Changes { get; init; } = [];
}

/// <summary>
/// Outcome of a mutating entitlement call. Most mutations are long-running operations (LROs); the
/// full operation polling lives in the roadmap's §7, so for now the UI just reflects that the
/// operation was accepted and whether Google completed it inline.
/// </summary>
public sealed record EntitlementOperation
{
    /// <summary>Resource name of the long-running operation, e.g. "operations/...".</summary>
    public string? OperationName { get; init; }

    /// <summary>True when Google completed the operation before returning.</summary>
    public bool Done { get; init; }

    /// <summary>Error message when the operation failed inline.</summary>
    public string? Error { get; init; }
}

/// <summary>A single value for an entitlement parameter (seats etc.). Numeric values use <see cref="IntValue"/>.</summary>
public sealed record EntitlementParameterInput
{
    public required string Name { get; init; }
    public long? IntValue { get; init; }
    public string? StringValue { get; init; }
}

/// <summary>Purchase payload (<c>entitlements.create</c>).</summary>
public sealed record PurchaseEntitlementRequest
{
    /// <summary>Offer id or full offer resource name to purchase.</summary>
    public required string OfferId { get; init; }

    public string? PurchaseOrderId { get; init; }
    public IReadOnlyList<EntitlementParameterInput> Parameters { get; init; } = [];
}

/// <summary>Change-offer payload (<c>entitlements.changeOffer</c>).</summary>
public sealed record ChangeOfferRequest
{
    /// <summary>Offer id or full offer resource name to switch to.</summary>
    public required string OfferId { get; init; }

    public string? PurchaseOrderId { get; init; }
    public IReadOnlyList<EntitlementParameterInput> Parameters { get; init; } = [];
}

/// <summary>Change-parameters payload, e.g. seat count (<c>entitlements.changeParameters</c>).</summary>
public sealed record ChangeParametersRequest
{
    public required IReadOnlyList<EntitlementParameterInput> Parameters { get; init; }
    public string? PurchaseOrderId { get; init; }
}

/// <summary>Change-renewal-settings payload (<c>entitlements.changeRenewalSettings</c>).</summary>
public sealed record ChangeRenewalSettingsRequest
{
    public required bool EnableRenewal { get; init; }
}
