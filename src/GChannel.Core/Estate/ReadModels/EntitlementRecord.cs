using GChannel.Core.Estate.Events;
using JasperFx.Events;

namespace GChannel.Core.Estate.ReadModels;

/// <summary>
/// Read-model document for one entitlement, projected <b>inline</b> from its event stream. Carries the
/// denormalised catalog names, per-month wholesale price, repricing mark-up and Ion cost so lists,
/// customer/reseller value panels and the dashboard roll up with no live Channel API calls.
/// </summary>
public sealed class EntitlementRecord
{
    /// <summary>Marten identity = the stream key (<see cref="EstateStreams.Entitlement"/>).</summary>
    public string Id { get; set; } = string.Empty;
    public int Version { get; set; }

    /// <summary>Short entitlement id (last segment of the entitlement resource name).</summary>
    public string EntitlementId { get; set; } = string.Empty;
    public string CustomerId { get; set; } = string.Empty;
    /// <summary>Owning channel partner link id, or null for the account's direct customers (denormalised from the customer).</summary>
    public string? OwningLinkId { get; set; }
    /// <summary>Cloud Billing (sub-)account backing this entitlement — the join key to the BigQuery cost export.</summary>
    public string? BillingAccount { get; set; }
    /// <summary>Provisioned-service id; for GCP entitlements the Cloud Billing sub-account.</summary>
    public string? ProvisioningId { get; set; }
    public string? ProductId { get; set; }
    public string? ProductName { get; set; }
    public string? SkuId { get; set; }
    public string? SkuName { get; set; }
    public string? OfferId { get; set; }
    public string? OfferName { get; set; }
    public string State { get; set; } = string.Empty;
    /// <summary>Seat footprint (num_units, falling back to a flexible plan's max_units cap) — for seat counts, NOT pricing.</summary>
    public long Seats { get; set; }
    /// <summary>Committed/billable seats (num_units only) — what money rollups multiply by.</summary>
    public long BillableSeats { get; set; }
    public bool IsTrial { get; set; }
    public DateTimeOffset? CreateTime { get; set; }
    public DateTimeOffset? CommitmentEndTime { get; set; }
    public bool? RenewalEnabled { get; set; }
    public string? PlanDescription { get; set; }
    /// <summary>Wholesale effective per-seat price normalised to a PER-MONTH figure. 0 if unknown.</summary>
    public decimal UnitPrice { get; set; }
    public string? Currency { get; set; }
    /// <summary>Repricing mark-up percent: per-entitlement config, else owning-link channel-partner config, else 0.</summary>
    public decimal RepricingPercent { get; set; }
    public decimal? IonCostPerSeat { get; set; }
    public decimal? IonMarginPercent { get; set; }
    public string? IonCurrency { get; set; }
    /// <summary>Denormalised from the owning customer: reseller buying for its own use (direct) vs an end customer (indirect).</summary>
    public bool IsResellerSelf { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastChangedUtc { get; set; }
    /// <summary>Operation name of the most recent app-requested change still in flight (null when none).</summary>
    public string? PendingOperationName { get; set; }
    public string? PendingAction { get; set; }

    public bool IsActive => string.Equals(State, "ACTIVE", StringComparison.OrdinalIgnoreCase);

    public static EntitlementRecord Create(IEvent<EntitlementDiscovered> e) => new()
    {
        EntitlementId = e.Data.EntitlementId,
        CustomerId = e.Data.CustomerId,
        OwningLinkId = e.Data.OwningLinkId,
        ProductId = e.Data.ProductId,
        SkuId = e.Data.SkuId,
        OfferId = e.Data.OfferId,
        State = e.Data.State,
        Seats = e.Data.Seats,
        BillableSeats = e.Data.BillableSeats,
        IsTrial = e.Data.IsTrial,
        CreateTime = e.Data.CreateTime,
        CommitmentEndTime = e.Data.CommitmentEndTime,
        RenewalEnabled = e.Data.RenewalEnabled,
        BillingAccount = e.Data.BillingAccount,
        ProvisioningId = e.Data.ProvisioningId,
        FirstSeenUtc = e.Timestamp,
        LastChangedUtc = e.Timestamp,
    };

    public void Apply(IEvent<EntitlementSeatsChanged> e)
    {
        Seats = e.Data.Seats;
        BillableSeats = e.Data.BillableSeats;
        LastChangedUtc = e.Timestamp;
    }

    public void Apply(IEvent<EntitlementStateChanged> e)
    {
        State = e.Data.State;
        IsTrial = e.Data.IsTrial;
        LastChangedUtc = e.Timestamp;
    }

    public void Apply(IEvent<EntitlementOfferChanged> e)
    {
        ProductId = e.Data.ProductId;
        SkuId = e.Data.SkuId;
        OfferId = e.Data.OfferId;
        LastChangedUtc = e.Timestamp;
    }

    public void Apply(IEvent<EntitlementCommitmentChanged> e)
    {
        CommitmentEndTime = e.Data.CommitmentEndTime;
        RenewalEnabled = e.Data.RenewalEnabled;
        LastChangedUtc = e.Timestamp;
    }

    public void Apply(EntitlementOwnershipChanged e)
    {
        OwningLinkId = e.OwningLinkId;
        IsResellerSelf = e.IsResellerSelf;
    }

    public void Apply(EntitlementBillingChanged e)
    {
        BillingAccount = e.BillingAccount;
        ProvisioningId = e.ProvisioningId;
    }

    public void Apply(EntitlementCatalogResolved e)
    {
        ProductName = e.ProductName;
        SkuName = e.SkuName;
        OfferName = e.OfferName;
        PlanDescription = e.PlanDescription;
    }

    public void Apply(EntitlementPriced e)
    {
        UnitPrice = e.UnitPrice;
        Currency = e.Currency;
        RepricingPercent = e.RepricingPercent;
    }

    public void Apply(EntitlementIonCostResolved e)
    {
        IonCostPerSeat = e.IonCostPerSeat;
        IonMarginPercent = e.IonMarginPercent;
        IonCurrency = e.IonCurrency;
    }

    public void Apply(IEvent<EntitlementRemovedObserved> e)
    {
        IsDeleted = true;
        LastChangedUtc = e.Timestamp;
    }

    public void Apply(IEvent<EntitlementRestoredObserved> e)
    {
        IsDeleted = false;
        LastChangedUtc = e.Timestamp;
    }

    public void Apply(EntitlementChangeRequested e)
    {
        PendingOperationName = e.OperationName;
        PendingAction = e.Action;
    }

    public void Apply(EntitlementChangeCompleted e)
    {
        if (PendingOperationName == e.OperationName) { PendingOperationName = null; PendingAction = null; }
    }

    public void Apply(EntitlementChangeFailed e)
    {
        if (PendingOperationName == e.OperationName) { PendingOperationName = null; PendingAction = null; }
    }

    // No Apply(ChannelNotificationReceived): that event is also appended to customer streams, and an inline
    // snapshot runs on every stream containing an event type it handles — even a no-op Apply would create a
    // stray EntitlementRecord on each customer stream. Unhandled events stay in the stream as history.
}
