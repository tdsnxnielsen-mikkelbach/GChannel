namespace GChannel.Shared.Contracts;

/// <summary>
/// Well-known Redis keys for the Channel notification feed, shared between the worker (the background
/// <c>ChannelNotificationsService</c> that produces events) and the API (which reads the feed and, in
/// development, seeds it). Lives in the shared contracts so neither side depends on the other.
/// </summary>
public static class ChannelNotificationFeed
{
    /// <summary>Redis key of the capped list holding the most recent notifications (newest first).</summary>
    public const string RedisKey = "channel:notifications";
}

/// <summary>
/// The reseller account's current Pub/Sub subscriber registration: the Google-owned topic that
/// Channel change notifications are published to, plus the service-account emails granted subscriber
/// access (via <c>accounts.register</c> / <c>accounts.listSubscribers</c>).
/// </summary>
public sealed record SubscriberRegistration
{
    /// <summary>The Pub/Sub topic notifications are published to (null when nothing is registered).</summary>
    public string? Topic { get; init; }

    /// <summary>Service-account emails currently registered as subscribers on the topic.</summary>
    public IReadOnlyList<string> ServiceAccounts { get; init; } = [];
}

/// <summary>Registers (or, via DELETE, unregisters) a service account as a Pub/Sub subscriber.</summary>
public sealed record RegisterSubscriberRequest
{
    /// <summary>The service-account email to grant (or revoke) subscriber access for.</summary>
    public required string ServiceAccount { get; init; }
}

/// <summary>
/// A Channel change event received from Google Cloud Pub/Sub. Each notification is correlated to the
/// customer/entitlement it concerns so the UI can deep-link back to §2 (customers) and §3
/// (entitlements) instead of polling.
/// </summary>
public sealed record ChannelNotification
{
    /// <summary>"Entitlement", "Customer" or "Unknown".</summary>
    public required string Kind { get; init; }

    /// <summary>The event type carried in the message, e.g. an entitlement/customer event type.</summary>
    public string? EventType { get; init; }

    /// <summary>The full Google resource name the event concerns.</summary>
    public string? ResourceName { get; init; }

    /// <summary>Customer id parsed from the resource name (deep-link to §2).</summary>
    public string? CustomerId { get; init; }

    /// <summary>Entitlement id parsed from the resource name (deep-link to §3).</summary>
    public string? EntitlementId { get; init; }

    /// <summary>Pub/Sub message id (useful for de-duplication / support).</summary>
    public string? MessageId { get; init; }

    /// <summary>When the subscriber received the message (UTC).</summary>
    public DateTimeOffset ReceivedAt { get; init; }
}

/// <summary>Recent Channel change notifications, newest first.</summary>
public sealed record ChannelNotificationsResult
{
    public IReadOnlyList<ChannelNotification> Notifications { get; init; } = [];

    /// <summary>True when the background Pub/Sub subscriber is configured and running.</summary>
    public bool SubscriberEnabled { get; init; }
}
