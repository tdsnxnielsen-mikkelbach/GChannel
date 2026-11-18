using GChannel.Core.Configuration;
using GChannel.Core.Messaging;
using GChannel.Core.Persistence.Documents;
using GChannel.Shared.Contracts;
using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Runtime;

namespace GChannel.Worker.Services;

/// <summary>
/// Bridge from the Cloud Customer Care support event subscription to the app's messaging (Google's "Synchronize
/// cases using Pub/Sub" connector). Customer Care publishes an attribute-only message (<c>resource_name</c>,
/// <c>notification_type</c>) to our own topic whenever a case, comment or attachment changes; this service
/// streams-pulls the subscription on that topic and sends each one as a <see cref="SupportEventArrived"/> to the
/// worker's <c>support</c> queue, whose handler fetches the case through the Cloud Support API. Acknowledged only
/// after the send succeeds (at-least-once); the feed document id is the Pub/Sub message id, so redeliveries are
/// idempotent. Disabled (no-op) unless a project, subscription and credential are configured.
/// </summary>
public sealed class SupportEventsService(
    IOptions<GoogleChannelOptions> channelOptions,
    IOptions<GoogleSupportOptions> supportOptions,
    IWolverineRuntime runtime,
    ILogger<SupportEventsService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = channelOptions.Value;
        var support = supportOptions.Value;
        if (!support.SubscriberEnabled(channel))
        {
            logger.LogInformation(
                "Support Pub/Sub subscriber is disabled; set GoogleSupport:PubSubSubscriptionId, a Pub/Sub project and a Google credential to enable it.");
            return;
        }

        var subscriptionName = SubscriptionName.FromProjectSubscription(support.EffectivePubSubProjectId(channel), support.PubSubSubscriptionId);
        SubscriberClient subscriber;
        try
        {
            subscriber = await new SubscriberClientBuilder
            {
                SubscriptionName = subscriptionName,
                GoogleCredential = ChannelNotificationsService.ResolveCredential(channel),
            }.BuildAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start the support Pub/Sub subscriber for {Subscription}.", subscriptionName);
            return;
        }

        var bus = new MessageBus(runtime);
        logger.LogInformation("Support Pub/Sub subscriber listening on {Subscription}; bridging to Service Bus.", subscriptionName);

        var startTask = subscriber.StartAsync(async (message, cancellation) =>
        {
            try
            {
                var supportEvent = ParseEvent(message);
                await bus.SendAsync(new SupportEventArrived(supportEvent));
                logger.LogInformation("Support event bridged: {Type} {Resource}", supportEvent.NotificationType, supportEvent.ResourceName);
                return SubscriberClient.Reply.Ack;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to bridge a support event (message {MessageId}); requeuing.", message.MessageId);
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
                logger.LogError(ex, "The support Pub/Sub subscriber stopped unexpectedly.");
            }
        }
    }

    /// <summary>
    /// Reads a support event: the message data is empty and the attributes carry <c>resource_name</c> (a case, or a
    /// comment / attachment under one) and <c>notification_type</c>. Unknown types are passed through as-is.
    /// </summary>
    internal static SupportEvent ParseEvent(PubsubMessage message)
    {
        string? resourceName = null;
        string? notificationType = null;
        if (message.Attributes is { } attributes)
        {
            attributes.TryGetValue("resource_name", out resourceName);
            attributes.TryGetValue("notification_type", out notificationType);
        }

        return new SupportEvent
        {
            MessageId = string.IsNullOrWhiteSpace(message.MessageId) ? $"local-{Guid.CreateVersion7():N}" : message.MessageId,
            NotificationType = string.IsNullOrWhiteSpace(notificationType) ? null : notificationType,
            ResourceName = string.IsNullOrWhiteSpace(resourceName) ? null : resourceName,
            CaseName = SupportCaseNames.CaseNameOf(resourceName),
            ReceivedAt = DateTimeOffset.UtcNow,
        };
    }
}
