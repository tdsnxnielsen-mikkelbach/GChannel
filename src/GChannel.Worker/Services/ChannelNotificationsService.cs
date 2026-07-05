using System.Text.Json;
using GChannel.ApiService.Configuration;
using GChannel.ApiService.Data;
using GChannel.ApiService.Services;
using GChannel.Shared.Contracts;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GChannel.Worker.Services;

/// <summary>
/// Background subscriber for Google Cloud Channel change notifications (§7). The Channel API publishes
/// entitlement/customer change events to a Google-owned Pub/Sub topic (managed via
/// <c>accounts.register</c>); the reseller creates a subscription to that topic in their own Google
/// Cloud project. This service streams-pulls that subscription and records each event into a capped
/// Redis list the UI reads as a live feed — so the app reacts to changes instead of polling.
///
/// <para>Hosting: this runs in the dedicated worker container app (alongside the dashboard refresher
/// and read-model sync). Pub/Sub load-balances messages across all
/// subscribers, so when the worker scales to multiple replicas they share the subscription automatically
/// and <em>no</em> distributed lock is required (unlike the dashboard compute, which must be
/// single-flight). Authentication prefers Workload Identity Federation (the Azure managed identity
/// mints short-lived federated Google tokens, no downloaded key) and falls back to a Google
/// service-account key when WIF is not configured. Pub/Sub needs no domain-wide delegation, so unlike
/// the background dashboard refresh this path can run entirely key-less. Disabled (no-op) unless a
/// Pub/Sub project + subscription and a credential (WIF config or service-account key) are configured.</para>
/// </summary>
public sealed class ChannelNotificationsService(
    IOptions<GoogleChannelOptions> options,
    IConnectionMultiplexer redis,
    IServiceScopeFactory scopeFactory,
    ReadModelProjector projector,
    ILoggerFactory loggerFactory,
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

            logger.LogInformation(
                "Channel Pub/Sub subscriber is disabled; missing configuration: {Missing}.",
                string.Join(", ", missing));
            return;
        }

        var subscriptionName = SubscriptionName.FromProjectSubscription(opts.PubSubProjectId, opts.PubSubSubscriptionId);
        var builder = new SubscriberClientBuilder { SubscriptionName = subscriptionName };
        builder.GoogleCredential = ResolveCredential(opts);

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

        // §10 event-driven projection: when the read-model is enabled, a change event triggers a targeted
        // refresh of just the affected customer (its metadata + entitlements) so the read-model and the UI
        // it backs update within seconds of a change — the background poll is left as a reconciliation
        // backstop. Projection needs a service-account credential with domain-wide delegation to read the
        // Channel API (the Pub/Sub credential above may be key-less WIF without DWD), so it is built
        // separately and only when the read-model sync is configured.
        GoogleChannelClient? projectionClient = null;
        if (opts.ReadModelSyncEnabled)
        {
            projectionClient = new GoogleChannelClient(
                new ServiceAccountCredentialSource(opts), options, loggerFactory.CreateLogger<GoogleChannelClient>());
            logger.LogInformation("Event-driven read-model projection enabled; change events will refresh the affected customer.");
        }

        logger.LogInformation("Channel Pub/Sub subscriber listening on {Subscription}.", subscriptionName);

        // StartAsync completes when StopAsync is called (shutdown) or an unrecoverable fault occurs.
        var startTask = subscriber.StartAsync(async (message, cancellation) =>
        {
            ChannelNotification notification;
            try
            {
                notification = ParseNotification(message);
                await db.ListLeftPushAsync(ChannelNotificationFeed.RedisKey, JsonSerializer.Serialize(notification));
                await db.ListTrimAsync(ChannelNotificationFeed.RedisKey, 0, maxItems - 1);
                logger.LogInformation(
                    "Channel notification received: {Kind} {EventType} {Resource}",
                    notification.Kind, notification.EventType, notification.ResourceName);
            }
            catch (Exception ex)
            {
                // Recording failed (e.g. a transient Redis fault) — nack so Pub/Sub redelivers the
                // message instead of dropping it, preserving at-least-once delivery to the feed.
                logger.LogWarning(ex, "Failed to record a Channel notification (message {MessageId}); requeuing.", message.MessageId);
                return SubscriberClient.Reply.Nack;
            }

            // Targeted read-model projection for the affected customer. Best-effort: a failure here must
            // NOT nack (that would redeliver and duplicate the feed entry) — the projection is idempotent
            // and re-reads live state, so duplicate/out-of-order events converge, and the poll reconciles
            // anything missed. Runs on its own DbContext scope (the subscriber may process concurrently).
            if (projectionClient is not null && !string.IsNullOrEmpty(notification.CustomerId))
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var projectDb = scope.ServiceProvider.GetRequiredService<GChannelDbContext>();
                    await projector.ProjectCustomerAsync(
                        projectDb, projectionClient, notification.CustomerId, DateTimeOffset.UtcNow, cancellation);
                    logger.LogInformation(
                        "Projected read-model refresh for customer {Customer} from a {Kind} event.",
                        notification.CustomerId, notification.Kind);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Shutting down — leave the refresh for the poll backstop.
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Event-driven projection failed for customer {Customer}; the poll will reconcile.", notification.CustomerId);
                }
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
    /// Resolves the Google credential the subscriber authenticates with. A Workload Identity Federation
    /// configuration (Google's recommended key-less approach) is preferred when present — the
    /// <c>external_account</c> JSON is loaded with auto type-detection so the host's own identity (e.g.
    /// the Azure managed identity) mints short-lived federated tokens. Otherwise a downloaded
    /// service-account key is used. Returns <c>null</c> only when nothing is configured, which the
    /// <see cref="GoogleChannelOptions.PubSubEnabled"/> guard already prevents.
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
    /// Parses a Cloud Channel Pub/Sub message into a correlated notification. The message data is a
    /// JSON payload with a <c>customer_event</c> or <c>entitlement_event</c> object (snake_case, as Google
    /// emits) carrying the affected resource name and an <c>event_type</c>; the message attributes also
    /// echo <c>subscriber_event_type</c> (ENTITLEMENT_EVENT/CUSTOMER_EVENT) and <c>event_type</c>.
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
            if (kind == "Unknown"
                && attributes.TryGetValue("subscriber_event_type", out var subscriberEventType))
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

    /// <summary>Returns the first matching property (supports both snake_case and camelCase keys).</summary>
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
        TryGetProperty(element, out var value, names) && value.ValueKind == JsonValueKind.String
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
