using GChannel.Core.Configuration;
using GChannel.Core.Estate;
using GChannel.Core.Estate.Events;
using GChannel.Core.Estate.ReadModels;
using GChannel.Core.Messaging;
using GChannel.Core.Persistence.Documents;
using Marten;
using Microsoft.Extensions.Options;
using Wolverine;

namespace GChannel.Worker.Handlers;

/// <summary>
/// Persists a Channel change notification bridged from Google Pub/Sub: stores it in the feed, appends a
/// <see cref="ChannelNotificationReceived"/> event to the affected customer / entitlement streams (so each
/// aggregate carries the externally-observed change history) and, when the read-model sync is configured,
/// cascades a targeted re-projection of the customer. The Service Bus topic de-duplicates on the Pub/Sub
/// message id, and the feed document id is that same id, so redeliveries are idempotent.
/// </summary>
public sealed class ChannelNotificationArrivedHandler(IOptions<GoogleChannelOptions> options, ILogger<ChannelNotificationArrivedHandler> logger)
{
    public async Task<OutgoingMessages> Handle(ChannelNotificationArrived message, IDocumentSession session, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        var n = message.Notification;
        var id = string.IsNullOrWhiteSpace(n.MessageId) ? $"local-{Guid.CreateVersion7():N}" : n.MessageId!;

        session.Store(new ChannelNotificationRecord
        {
            Id = id,
            Notification = n with { MessageId = id },
            ReceivedAt = n.ReceivedAt,
            CustomerId = n.CustomerId,
            EntitlementId = n.EntitlementId,
        });

        var evt = new ChannelNotificationReceived(id, n.Kind, n.EventType, n.ResourceName, n.CustomerId, n.EntitlementId, n.ReceivedAt);
        if (!string.IsNullOrEmpty(n.CustomerId)
            && await session.LoadAsync<CustomerRecord>(EstateStreams.Customer(n.CustomerId), ct) is { } customer)
        {
            session.Events.Append(customer.Id, evt);
        }
        if (!string.IsNullOrEmpty(n.EntitlementId)
            && await session.LoadAsync<EntitlementRecord>(EstateStreams.Entitlement(n.EntitlementId), ct) is { } entitlement)
        {
            session.Events.Append(entitlement.Id, evt);
        }

        // Retention: keep the feed bounded well above what the UI shows.
        var keep = Math.Max(1, options.Value.PubSubMaxNotifications) * 10;
        var count = await session.Query<ChannelNotificationRecord>().CountAsync(ct);
        if (count > keep + 100)
        {
            var cutoff = await session.Query<ChannelNotificationRecord>()
                .OrderByDescending(r => r.ReceivedAt)
                .Skip(keep)
                .Select(r => r.ReceivedAt)
                .FirstOrDefaultAsync(ct);
            session.DeleteWhere<ChannelNotificationRecord>(r => r.ReceivedAt < cutoff);
        }

        if (!string.IsNullOrEmpty(n.CustomerId) && options.Value.ReadModelSyncEnabled)
        {
            outgoing.Add(new ProjectCustomer(n.CustomerId, $"notification {n.Kind} {n.EventType}"));
        }

        logger.LogInformation("Channel notification recorded: {Kind} {EventType} {Resource}", n.Kind, n.EventType, n.ResourceName);
        return outgoing;
    }
}
