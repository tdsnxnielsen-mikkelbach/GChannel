namespace GChannel.Core.Estate.Events;

// Entitlement stream (key: EstateStreams.Entitlement(entitlementId)). Observed facts about a customer's
// subscription, the enrichment resolved for it (catalog names, pricing, Ion cost) and the lifecycle
// commands the app requested plus their long-running-operation outcome.

/// <summary>Marker for every event in an entitlement stream.</summary>
public interface IEntitlementEvent
{
    string EntitlementId { get; }
    string CustomerId { get; }
}

/// <summary>First observation of an entitlement (stream start).</summary>
public sealed record EntitlementDiscovered(
    string EntitlementId,
    string CustomerId,
    string? OwningLinkId,
    string? ProductId,
    string? SkuId,
    string? OfferId,
    string State,
    long Seats,
    long BillableSeats,
    bool IsTrial,
    DateTimeOffset? CreateTime,
    DateTimeOffset? CommitmentEndTime,
    bool? RenewalEnabled,
    string? BillingAccount,
    string? ProvisioningId,
    ObservationSource Source) : IEntitlementEvent;

/// <summary>Seat footprint (num_units / max_units) or committed seats changed.</summary>
public sealed record EntitlementSeatsChanged(
    string EntitlementId,
    string CustomerId,
    long PreviousSeats,
    long Seats,
    long PreviousBillableSeats,
    long BillableSeats,
    ObservationSource Source) : IEntitlementEvent;

/// <summary>Provisioning state (ACTIVE / SUSPENDED / …) or trial flag changed.</summary>
public sealed record EntitlementStateChanged(
    string EntitlementId,
    string CustomerId,
    string PreviousState,
    string State,
    bool IsTrial,
    ObservationSource Source) : IEntitlementEvent;

/// <summary>The entitlement now points at a different product / SKU / offer.</summary>
public sealed record EntitlementOfferChanged(
    string EntitlementId,
    string CustomerId,
    string? ProductId,
    string? SkuId,
    string? PreviousOfferId,
    string? OfferId,
    ObservationSource Source) : IEntitlementEvent;

/// <summary>Commitment end time or auto-renew flag changed.</summary>
public sealed record EntitlementCommitmentChanged(
    string EntitlementId,
    string CustomerId,
    DateTimeOffset? CommitmentEndTime,
    bool? RenewalEnabled,
    ObservationSource Source) : IEntitlementEvent;

/// <summary>Owning link (denormalised from the customer) or reseller-self classification changed.</summary>
public sealed record EntitlementOwnershipChanged(
    string EntitlementId,
    string CustomerId,
    string? OwningLinkId,
    bool IsResellerSelf) : IEntitlementEvent;

/// <summary>Cloud Billing (sub-)account / provisioning id changed.</summary>
public sealed record EntitlementBillingChanged(
    string EntitlementId,
    string CustomerId,
    string? BillingAccount,
    string? ProvisioningId) : IEntitlementEvent;

/// <summary>Friendly display names + plan summary resolved from the catalog (or <c>lookupOffer</c>).</summary>
public sealed record EntitlementCatalogResolved(
    string EntitlementId,
    string CustomerId,
    string? ProductName,
    string? SkuName,
    string? OfferName,
    string? PlanDescription) : IEntitlementEvent;

/// <summary>Wholesale per-month unit price, currency and repricing mark-up resolved for the entitlement.</summary>
public sealed record EntitlementPriced(
    string EntitlementId,
    string CustomerId,
    decimal UnitPrice,
    string? Currency,
    decimal RepricingPercent) : IEntitlementEvent;

/// <summary>Real per-seat cost / margin from Ion (TD SYNNEX StreamOne) resolved for the entitlement.</summary>
public sealed record EntitlementIonCostResolved(
    string EntitlementId,
    string CustomerId,
    decimal? IonCostPerSeat,
    decimal? IonMarginPercent,
    string? IonCurrency) : IEntitlementEvent;

/// <summary>The entitlement vanished from the customer's list (or its customer was deleted).</summary>
public sealed record EntitlementRemovedObserved(string EntitlementId, string CustomerId, string Reason, ObservationSource Source) : IEntitlementEvent;

/// <summary>A previously removed entitlement re-appeared.</summary>
public sealed record EntitlementRestoredObserved(string EntitlementId, string CustomerId, ObservationSource Source) : IEntitlementEvent;

/// <summary>A lifecycle command the app issued (purchase, change seats/offer/renewal, activate, suspend, cancel, …).</summary>
public sealed record EntitlementChangeRequested(
    string EntitlementId,
    string CustomerId,
    string Action,
    string? RequestedBy,
    string? OperationName,
    string? Details) : IEntitlementEvent;

/// <summary>The Google long-running operation for a requested change finished successfully.</summary>
public sealed record EntitlementChangeCompleted(
    string EntitlementId,
    string CustomerId,
    string Action,
    string OperationName) : IEntitlementEvent;

/// <summary>The Google long-running operation for a requested change failed.</summary>
public sealed record EntitlementChangeFailed(
    string EntitlementId,
    string CustomerId,
    string Action,
    string OperationName,
    string? Error) : IEntitlementEvent;
