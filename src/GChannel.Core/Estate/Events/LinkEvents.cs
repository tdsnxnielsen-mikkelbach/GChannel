namespace GChannel.Core.Estate.Events;

// Channel partner link stream (key: EstateStreams.Link(linkId)) — the distributor's downstream resellers.

/// <summary>Marker for every event in a channel partner link stream.</summary>
public interface ILinkEvent
{
    string LinkId { get; }
}

/// <summary>First observation of a channel partner link (stream start).</summary>
public sealed record LinkDiscovered(
    string LinkId,
    string? ResellerCloudId,
    string? PrimaryDomain,
    string LinkState,
    DateTimeOffset? CreateTime,
    ObservationSource Source) : ILinkEvent;

/// <summary>Partner Cloud Identity / primary domain / create time changed.</summary>
public sealed record LinkDetailsChanged(
    string LinkId,
    string? ResellerCloudId,
    string? PrimaryDomain,
    DateTimeOffset? CreateTime,
    ObservationSource Source) : ILinkEvent;

/// <summary>INVITED → ACTIVE → SUSPENDED … transitions.</summary>
public sealed record LinkStateChanged(string LinkId, string PreviousLinkState, string LinkState, ObservationSource Source) : ILinkEvent;

/// <summary>The link vanished from the account's roster.</summary>
public sealed record LinkRemovedObserved(string LinkId, ObservationSource Source) : ILinkEvent;

/// <summary>A previously removed link re-appeared.</summary>
public sealed record LinkRestoredObserved(string LinkId, ObservationSource Source) : ILinkEvent;

/// <summary>A mutation the app issued for this link (invite, activate, suspend).</summary>
public sealed record LinkMutationRequested(string LinkId, string Action, string? RequestedBy) : ILinkEvent;

/// <summary>
/// A Google Pub/Sub change notification correlated to the customer (and optionally entitlement) it
/// concerns. Appended to the customer stream — and to the entitlement stream when the event names one —
/// so each aggregate carries the externally-observed change history alongside the app's own actions.
/// </summary>
public sealed record ChannelNotificationReceived(
    string MessageId,
    string Kind,
    string? EventType,
    string? ResourceName,
    string? CustomerId,
    string? EntitlementId,
    DateTimeOffset ReceivedAt);
