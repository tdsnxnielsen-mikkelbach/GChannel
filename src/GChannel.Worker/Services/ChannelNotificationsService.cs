using GChannel.Core.Configuration;
using GChannel.Core.Messaging;
using GChannel.Shared.Contracts;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Wolverine;
using Wolverine.Runtime;

namespace GChannel.Worker.Services;

/// <summary>
/// Bridge from Google Cloud Pub/Sub to the app's own messaging. The Channel API publishes entitlement /
/// customer change events to a Google-owned Pub/Sub topic (managed via <c>accounts.register</c>); the
/// reseller creates a subscription to that topic in their own Google Cloud project. This service streams-pulls
/// that subscription, parses each message into a correlated <see cref="ChannelNotification"/> and publishes it
/// as a <see cref="ChannelNotificationArrived"/> message on Azure Service Bus (durably, through Wolverine) —
/// the handler then records it in the feed, on the event streams, and triggers the targeted re-projection.
/// The message is acknowledged only after the publish succeeds (at-least-once); the Service Bus topic
/// de-duplicates on the Pub/Sub message id.
///
/// <para>Authentication prefers Workload Identity Federation (key-less) and falls back to a service-account
/// key. Disabled (no-op) unless a Pub/Sub project + subscription and a credential are configured.</para>
/// </summary>
public sealed class ChannelNotificationsService(
    IOptions<GoogleChannelOptions> options,
    IWolverineRuntime runtime,
    ILogger<ChannelNotificationsService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.PubSubEnabled)
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(opts.PubSubProjectId)) missing.Add("GoogleChannel:PubSubProjectId");
            if (string.IsNullOrWhiteSpace(opts.PubSubSubscriptionId)) missing.Add("GoogleChannel:PubSubSubscriptionId");
            if (!opts.HasWorkloadIdentityCredential && !opts.HasServiceAccountCredential)
                missing.Add("GoogleChannel:WorkloadIdentityCredentialJson (or ServiceAccountKeyJson/ServiceAccountKeyPath)");

            logger.LogInformation("Channel Pub/Sub subscriber is disabled; missing configuration: {Missing}.", string.Join(", ", missing));
            return;
        }

        var subscriptionName = SubscriptionName.FromProjectSubscription(opts.PubSubProjectId, opts.PubSubSubscriptionId);
        var builder = new SubscriberClientBuilder { SubscriptionName = subscriptionName, GoogleCredential = ResolveCredential(opts) };

        SubscriberClient subscriber;
        try
        {
            subscriber = await builder.BuildAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start the Channel Pub/Sub subscriber for {Subscription}.", subscriptionName);
            return;
        }

        var bus = new MessageBus(runtime);
        logger.LogInformation("Channel Pub/Sub subscriber listening on {Subscription}; bridging to Service Bus.", subscriptionName);

        var startTask = subscriber.StartAsync(async (message, cancellation) =>
        {
            try
            {
                var notification = ParseNotification(message);
                await bus.PublishAsync(new ChannelNotificationArrived(notification));
                logger.LogInformation("Channel notification bridged: {Kind} {EventType} {Resource}", notification.Kind, notification.EventType, notification.ResourceName);
                return SubscriberClient.Reply.Ack;
            }
            catch (Exception ex)
            {
                // Nack so Pub/Sub redelivers instead of dropping the message (at-least-once to the feed).
                logger.LogWarning(ex, "Failed to bridge a Channel notification (message {MessageId}); requeuing.", message.MessageId);
                return SubscriberClient.Reply.Nack;
            }
        });

#pragma warning disable CS0618 // The CancellationToken overload is the documented stop signal.
        using (stoppingToken.Register(() => _ = subscriber.StopAsync(CancellationToken.None)))
#pragma warning restore CS0618
        {
            try
            {
                await startTask;
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The Channel Pub/Sub subscriber stopped unexpectedly.");
            }
        }
    }

    /// <summary>
    /// Prefers a Workload Identity Federation configuration (the <c>external_account</c> JSON, loaded with auto
    /// type-detection so the host's own identity mints short-lived federated tokens), else a service-account key.
    /// </summary>
    private static GoogleCredential? ResolveCredential(GoogleChannelOptions opts)
    {
        if (!string.IsNullOrWhiteSpace(opts.WorkloadIdentityCredentialJson))
            return CredentialFactory.FromJson<GoogleCredential>(opts.WorkloadIdentityCredentialJson);
        if (!string.IsNullOrWhiteSpace(opts.WorkloadIdentityCredentialPath))
            return CredentialFactory.FromFile<GoogleCredential>(opts.WorkloadIdentityCredentialPath);
        if (!string.IsNullOrWhiteSpace(opts.ServiceAccountKeyJson))
            return CredentialFactory.FromJson(opts.ServiceAccountKeyJson, "service_account");
        if (!string.IsNullOrWhiteSpace(opts.ServiceAccountKeyPath))
            return CredentialFactory.FromFile(opts.ServiceAccountKeyPath, "service_account");
        return null;
    }

    /// <summary>
    /// Parses a Cloud Channel Pub/Sub message: a JSON payload with a <c>customer_event</c> or <c>entitlement_event</c>
    /// object carrying the affected resource name and an <c>event_type</c>; the attributes echo
    /// <c>subscriber_event_type</c> and <c>event_type</c>.
    /// </summary>
    internal static ChannelNotification ParseNotification(PubsubMessage message)
    {
        var json = message.Data?.ToStringUtf8();
        var kind = "Unknown";
        string? eventType = null;
        string? resourceName = null;

        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (TryGetProperty(root, out var entitlementEvent, "entitlement_event", "entitlementEvent"))
                {
                    kind = "Entitlement";
                    resourceName = GetString(entitlementEvent, "entitlement");
                    eventType = GetString(entitlementEvent, "event_type", "eventType");
                }
                else if (TryGetProperty(root, out var customerEvent, "customer_event", "customerEvent"))
                {
                    kind = "Customer";
                    resourceName = GetString(customerEvent, "customer");
                    eventType = GetString(customerEvent, "event_type", "eventType");
                }
            }
            catch (JsonException)
            {
                // Leave as Unknown; the attributes below may still carry the resource.
            }
        }

        if (message.Attributes is { } attributes)
        {
            if (kind == "Unknown" && attributes.TryGetValue("subscriber_event_type", out var subscriberEventType))
            {
                kind = subscriberEventType switch
                {
                    "ENTITLEMENT_EVENT" => "Entitlement",
                    "CUSTOMER_EVENT" => "Customer",
                    _ => kind
                };
            }

            resourceName ??= attributes.TryGetValue("entitlement", out var entitlement) ? entitlement
                : attributes.TryGetValue("customer", out var customer) ? customer
                : null;

            eventType ??= attributes.TryGetValue("event_type", out var attrEventType) ? attrEventType
                : attributes.TryGetValue("eventType", out var attrEventTypeCamel) ? attrEventTypeCamel
                : null;
        }

        var (customerId, entitlementId) = SplitResource(resourceName);

        return new ChannelNotification
        {
            Kind = kind,
            EventType = eventType,
            ResourceName = resourceName,
            CustomerId = customerId,
            EntitlementId = entitlementId,
            MessageId = message.MessageId,
            ReceivedAt = DateTimeOffset.UtcNow
        };
    }

    private static bool TryGetProperty(JsonElement element, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out value))
            {
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, params string[] names) =>
        TryGetProperty(element, out var value, names) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static (string? CustomerId, string? EntitlementId) SplitResource(string? resourceName)
    {
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return (null, null);
        }

        var segments = resourceName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? customerId = null;
        string? entitlementId = null;
        for (var i = 0; i + 1 < segments.Length; i++)
        {
            if (segments[i] == "customers")
            {
                customerId = segments[i + 1];
            }
            else if (segments[i] == "entitlements")
            {
                entitlementId = segments[i + 1];
            }
        }
        return (customerId, entitlementId);
    }
}
