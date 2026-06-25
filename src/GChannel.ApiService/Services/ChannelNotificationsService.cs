using System.Text.Json;
using GChannel.ApiService.Configuration;
using GChannel.Shared.Contracts;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GChannel.ApiService.Services;

/// <summary>
/// Background subscriber for Google Cloud Channel change notifications (§7). The Channel API publishes
/// entitlement/customer change events to a Google-owned Pub/Sub topic (managed via
/// <c>accounts.register</c>); the reseller creates a subscription to that topic in their own Google
/// Cloud project. This service streams-pulls that subscription and records each event into a capped
/// Redis list the UI reads as a live feed — so the app reacts to changes instead of polling.
///
/// <para>Hosting: this runs inside the existing (internal) API container app, like the dashboard
/// refresher — no separate worker container is needed. Pub/Sub load-balances messages across all
/// subscribers, so when the API scales to multiple replicas they share the subscription automatically
/// and <em>no</em> distributed lock is required (unlike the dashboard compute, which must be
/// single-flight). Authentication uses the same Google service-account key as the background refresh
/// (Pub/Sub uses the key directly; no domain-wide delegation). On Azure that key is read from Key
/// Vault via the app's managed identity; locally it comes from user-secrets. Disabled (no-op) unless
/// a Pub/Sub project + subscription and a service-account credential are configured.</para>
/// </summary>
public sealed class ChannelNotificationsService(
    IOptions<GoogleChannelOptions> options,
    IConnectionMultiplexer redis,
    ILogger<ChannelNotificationsService> logger) : BackgroundService
{
    /// <summary>Redis key of the capped list holding the most recent notifications (newest first).</summary>
    public const string FeedKey = "channel:notifications";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.PubSubEnabled)
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(opts.PubSubProjectId)) missing.Add("GoogleChannel:PubSubProjectId");
            if (string.IsNullOrWhiteSpace(opts.PubSubSubscriptionId)) missing.Add("GoogleChannel:PubSubSubscriptionId");
            if (!opts.HasServiceAccountCredential) missing.Add("GoogleChannel:ServiceAccountKeyJson (or ServiceAccountKeyPath)");

            logger.LogInformation(
                "Channel Pub/Sub subscriber is disabled; missing configuration: {Missing}.",
                string.Join(", ", missing));
            return;
        }

        var subscriptionName = SubscriptionName.FromProjectSubscription(opts.PubSubProjectId, opts.PubSubSubscriptionId);
        var builder = new SubscriberClientBuilder { SubscriptionName = subscriptionName };
        if (!string.IsNullOrWhiteSpace(opts.ServiceAccountKeyJson))
        {
            builder.GoogleCredential = CredentialFactory.FromJson(opts.ServiceAccountKeyJson, "service_account");
        }
        else if (!string.IsNullOrWhiteSpace(opts.ServiceAccountKeyPath))
        {
            builder.GoogleCredential = CredentialFactory.FromFile(opts.ServiceAccountKeyPath, "service_account");
        }

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

        var db = redis.GetDatabase();
        var maxItems = Math.Max(1, opts.PubSubMaxNotifications);

        logger.LogInformation("Channel Pub/Sub subscriber listening on {Subscription}.", subscriptionName);

        // StartAsync completes when StopAsync is called (shutdown) or an unrecoverable fault occurs.
        var startTask = subscriber.StartAsync(async (message, _) =>
        {
            try
            {
                var notification = ParseNotification(message);
                await db.ListLeftPushAsync(FeedKey, JsonSerializer.Serialize(notification));
                await db.ListTrimAsync(FeedKey, 0, maxItems - 1);
                logger.LogInformation(
                    "Channel notification received: {Kind} {EventType} {Resource}",
                    notification.Kind, notification.EventType, notification.ResourceName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to record a Channel notification (message {MessageId}).", message.MessageId);
            }

            return SubscriberClient.Reply.Ack;
        });

        // Stop the subscriber when the host begins shutting down, then await a clean drain. The
        // CancellationToken overload is marked obsolete, but it is the documented stop signal and the
        // ShutdownOptions overload is not needed here (default WaitForProcessing behaviour is fine).
#pragma warning disable CS0618 // Type or member is obsolete
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
    /// Parses a Cloud Channel Pub/Sub message into a correlated notification. The message data is a
    /// JSON payload with a <c>customerEvent</c> or <c>entitlementEvent</c> object carrying the affected
    /// resource name and an event type; some deployments also echo ids in the message attributes.
    /// </summary>
    private static ChannelNotification ParseNotification(PubsubMessage message)
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
                if (root.TryGetProperty("entitlementEvent", out var entitlementEvent))
                {
                    kind = "Entitlement";
                    resourceName = GetString(entitlementEvent, "entitlement");
                    eventType = GetString(entitlementEvent, "eventType");
                }
                else if (root.TryGetProperty("customerEvent", out var customerEvent))
                {
                    kind = "Customer";
                    resourceName = GetString(customerEvent, "customer");
                    eventType = GetString(customerEvent, "eventType");
                }
            }
            catch (JsonException)
            {
                // Leave as Unknown; the attributes below may still carry the resource.
            }
        }

        if (resourceName is null && message.Attributes is { } attributes)
        {
            if (attributes.TryGetValue("entitlement", out var entitlement))
            {
                kind = kind == "Unknown" ? "Entitlement" : kind;
                resourceName = entitlement;
            }
            else if (attributes.TryGetValue("customer", out var customer))
            {
                kind = kind == "Unknown" ? "Customer" : kind;
                resourceName = customer;
            }

            eventType ??= attributes.TryGetValue("eventType", out var attrEventType) ? attrEventType : null;
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

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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
