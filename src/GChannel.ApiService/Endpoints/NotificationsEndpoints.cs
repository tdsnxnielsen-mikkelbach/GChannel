using GChannel.Core.Configuration;
using GChannel.Core.GoogleApis;
using GChannel.Core.Persistence.Documents;
using GChannel.Shared.Contracts;
using Marten;
using Microsoft.Extensions.Options;
using Wolverine.Http;

namespace GChannel.ApiService.Endpoints;

/// <summary>
/// Eventing endpoints: the feed of Channel change notifications received from Pub/Sub (persisted by the
/// worker as <see cref="ChannelNotificationRecord"/> documents), plus management of the Pub/Sub subscriber
/// registration (<c>accounts.register</c> / <c>unregister</c> / <c>listSubscribers</c>).
/// </summary>
public static class NotificationsEndpoints
{
    [WolverineGet("/api/notifications")]
    public static async Task<ChannelNotificationsResult> ListNotifications(
        IQuerySession session, IOptions<GoogleChannelOptions> options, CancellationToken cancellationToken)
    {
        var records = await session.Query<ChannelNotificationRecord>()
            .OrderByDescending(n => n.ReceivedAt)
            .Take(Math.Max(1, options.Value.PubSubMaxNotifications))
            .ToListAsync(cancellationToken);

        return new ChannelNotificationsResult
        {
            Notifications = records.Select(r => r.Notification).ToList(),
            SubscriberEnabled = options.Value.PubSubEnabled
        };
    }

    [WolverineGet("/api/notifications/subscribers")]
    public static Task<SubscriberRegistration> ListSubscribers(IGoogleChannelClient channel, CancellationToken cancellationToken)
        => channel.ListSubscribersAsync(cancellationToken);

    [WolverinePost("/api/notifications/subscribers")]
    public static Task<SubscriberRegistration> RegisterSubscriber(
        RegisterSubscriberRequest request, IGoogleChannelClient channel, CancellationToken cancellationToken)
        => channel.RegisterSubscriberAsync(request.ServiceAccount, cancellationToken);

    [WolverineDelete("/api/notifications/subscribers/{serviceAccount}")]
    public static Task<SubscriberRegistration> UnregisterSubscriber(
        string serviceAccount, IGoogleChannelClient channel, CancellationToken cancellationToken)
        => channel.UnregisterSubscriberAsync(serviceAccount, cancellationToken);
}
