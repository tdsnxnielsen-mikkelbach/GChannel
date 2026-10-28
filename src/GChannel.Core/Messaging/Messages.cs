using GChannel.Shared.Contracts;

namespace GChannel.Core.Messaging;

// ---------------------------------------------------------------------------------------------------
// Integration events — published to the Service Bus topic "estate-events" (subscriptions: worker-estate,
// api-cache). Published through Wolverine's Marten outbox, so they commit atomically with the events /
// documents that caused them.
// ---------------------------------------------------------------------------------------------------

/// <summary>A customer was created / imported / updated / deleted through the app (or observed changed); the worker re-projects it.</summary>
public sealed record CustomerChanged(string CustomerId, string? OwningLinkId, string Reason, string? RequestedBy);

/// <summary>An entitlement lifecycle command was issued through the app for this customer/entitlement.</summary>
public sealed record EntitlementChanged(string CustomerId, string EntitlementId, string Action, string? RequestedBy);

/// <summary>A Google Pub/Sub change notification arrived (bridged by the worker's subscriber).</summary>
public sealed record ChannelNotificationArrived(ChannelNotification Notification);

/// <summary>A mutating Channel API call returned a long-running operation to track.</summary>
public sealed record OperationSubmitted(
    string OperationName,
    string CustomerId,
    string? EntitlementId,
    string Action,
    string? RequestedBy,
    DateTimeOffset SubmittedAt);

/// <summary>The worker finished (re-)projecting a customer; the API evicts its Redis read cache for that customer/link.</summary>
public sealed record CustomerReadModelUpdated(string CustomerId, string? OwningLinkId, string Reason);

// ---------------------------------------------------------------------------------------------------
// Commands — Service Bus queue "estate-sync" (sequential consumer so Channel API quotas can be paced).
// ---------------------------------------------------------------------------------------------------

/// <summary>Targeted refresh of one customer (metadata + all entitlements) from live Channel API state.</summary>
public sealed record ProjectCustomer(string CustomerId, string Reason);

// ---------------------------------------------------------------------------------------------------
// Recurring job ticks — Service Bus queue "jobs". Each tick carries the RunId recorded in the job's
// RecurringJobState; a tick whose RunId no longer matches is a stale/duplicate chain and is ignored.
// ---------------------------------------------------------------------------------------------------

public interface IRecurringJobTick
{
    Guid RunId { get; }
}

/// <summary>One rolling estate read-model sync cycle (direct customers, link roster, link fan-out, entitlement pass).</summary>
public sealed record RunEstateSyncCycle(Guid RunId) : IRecurringJobTick;

/// <summary>Recompute the dashboard snapshot (from the read model, or the live fan-out when the read model is off).</summary>
public sealed record RefreshDashboardSnapshot(Guid RunId) : IRecurringJobTick;

/// <summary>One BigQuery billing-export sync cycle.</summary>
public sealed record RunBillingSyncCycle(Guid RunId) : IRecurringJobTick;

/// <summary>One nightly billing IAM security-posture scan.</summary>
public sealed record RunBillingPostureScan(Guid RunId) : IRecurringJobTick;

// ---------------------------------------------------------------------------------------------------
// Operation tracking saga — Service Bus queue "operations".
// ---------------------------------------------------------------------------------------------------

/// <summary>Timeout message: poll the Google long-running operation again.</summary>
public sealed record CheckChannelOperation(string OperationName);
