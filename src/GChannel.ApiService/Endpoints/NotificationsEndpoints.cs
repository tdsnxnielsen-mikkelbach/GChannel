using System.Text.Json;
using GChannel.ApiService.Configuration;
using GChannel.ApiService.Services;
using GChannel.Shared.Contracts;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GChannel.ApiService.Endpoints;

/// <summary>
/// Maps the eventing endpoints (§7): the live feed of Channel change notifications received from
/// Pub/Sub (read from the capped Redis list written by the worker's <c>ChannelNotificationsService</c>),
/// plus management of the Pub/Sub subscriber registration
/// (<c>accounts.register</c> / <c>unregister</c> / <c>listSubscribers</c>).
/// </summary>
public static class NotificationsEndpoints
{
    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notifications").WithTags("Notifications");

        group.MapGet("/", async (
                IConnectionMultiplexer redis,
                IOptions<GoogleChannelOptions> options,
                CancellationToken cancellationToken) =>
            {
                var db = redis.GetDatabase();
                var entries = await db.ListRangeAsync(ChannelNotificationFeed.RedisKey, 0, -1);
                var notifications = entries
                    .Where(entry => entry.HasValue)
                    .Select(entry => JsonSerializer.Deserialize<ChannelNotification>((string)entry!))
                    .Where(notification => notification is not null)
                    .Select(notification => notification!)
                    .ToList();

                return Results.Ok(new ChannelNotificationsResult
                {
                    Notifications = notifications,
                    SubscriberEnabled = options.Value.PubSubEnabled
                });
            })
            .WithName("ListNotifications")
            .WithSummary("Lists recent Channel change notifications received from Pub/Sub.");

        group.MapGet("/subscribers", (
                IGoogleChannelClient channel,
                CancellationToken cancellationToken) =>
                channel.ListSubscribersAsync(cancellationToken))
            .WithName("ListSubscribers")
            .WithSummary("Lists the account's Pub/Sub subscriber registration (topic + service accounts).");

        group.MapPost("/subscribers", async (
                RegisterSubscriberRequest request,
                IGoogleChannelClient channel,
                CancellationToken cancellationToken) =>
            {
                var registration = await channel.RegisterSubscriberAsync(request.ServiceAccount, cancellationToken);
                return Results.Ok(registration);
            })
            .WithName("RegisterSubscriber")
            .WithSummary("Registers a service account as a Pub/Sub subscriber.");

        group.MapDelete("/subscribers/{serviceAccount}", async (
                string serviceAccount,
                IGoogleChannelClient channel,
                CancellationToken cancellationToken) =>
            {
                var registration = await channel.UnregisterSubscriberAsync(serviceAccount, cancellationToken);
                return Results.Ok(registration);
            })
            .WithName("UnregisterSubscriber")
            .WithSummary("Unregisters a Pub/Sub subscriber service account.");

        return app;
    }
}
