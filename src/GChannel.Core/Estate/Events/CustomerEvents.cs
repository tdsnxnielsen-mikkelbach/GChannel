namespace GChannel.Core.Estate.Events;

// Customer stream (key: EstateStreams.Customer(customerId)). Google remains the system of record; these
// events record what the app OBSERVED about a customer (diffs between the projected state and freshly
// fetched Channel API state), how it was classified, and the mutations the app itself requested.

/// <summary>Marker for every event in a customer stream.</summary>
public interface ICustomerEvent
{
    string CustomerId { get; }
}

/// <summary>First observation of a customer (stream start).</summary>
public sealed record CustomerDiscovered(
    string CustomerId,
    string? OrgName,
    string? Domain,
    string? CloudIdentityId,
    string? CloudIdentityDomain,
    string? OwningLinkId,
    DateTimeOffset? CreateTime,
    ObservationSource Source) : ICustomerEvent;

/// <summary>The customer's metadata differs from the last projected state.</summary>
public sealed record CustomerDetailsChanged(
    string CustomerId,
    string? OrgName,
    string? Domain,
    string? CloudIdentityId,
    string? CloudIdentityDomain,
    DateTimeOffset? CreateTime,
    ObservationSource Source) : ICustomerEvent;

/// <summary>The customer now shows up under a different owner (direct ↔ a channel partner link).</summary>
public sealed record CustomerOwnershipChanged(
    string CustomerId,
    string? PreviousOwningLinkId,
    string? OwningLinkId,
    ObservationSource Source) : ICustomerEvent;

/// <summary>
/// Classification of the customer as a reseller buying for its own use (DIRECT sale from us) vs a
/// reseller's end customer (indirect), derived by matching its Cloud Identity / domain against the
/// channel partner link roster.
/// </summary>
public sealed record CustomerClassified(string CustomerId, bool IsResellerSelf) : ICustomerEvent;

/// <summary>The customer vanished from a fresh list pass (or was deleted through the app).</summary>
public sealed record CustomerRemovedObserved(string CustomerId, string Reason, ObservationSource Source) : ICustomerEvent;

/// <summary>A previously removed customer re-appeared.</summary>
public sealed record CustomerRestoredObserved(string CustomerId, ObservationSource Source) : ICustomerEvent;

/// <summary>A mutation the app issued to the Channel API for this customer (audit trail of who did what).</summary>
public sealed record CustomerMutationRequested(
    string CustomerId,
    string Action,
    string? RequestedBy,
    string? OperationName) : ICustomerEvent;
