using GChannel.Core.Configuration;
using GChannel.Core.Estate;
using GChannel.Core.Messaging;
using GChannel.Worker.Infrastructure;
using Marten;
using Microsoft.Extensions.Options;
using Wolverine;

namespace GChannel.Worker.Handlers;

/// <summary>
/// Targeted re-projection of one customer from live Channel API state — triggered by an app mutation
/// (<see cref="CustomerChanged"/> / <see cref="EntitlementChanged"/> published by the API through its outbox),
/// a Pub/Sub change notification, a manual "refresh now" or the operation saga. Re-reads live state rather
/// than applying a payload, so duplicate / out-of-order triggers converge on current truth. The periodic
/// estate sync remains the reconciliation backstop. Requires the service-account credential (domain-wide
/// delegation) to read the Channel API, so it is a no-op until the read-model sync is configured.
/// </summary>
public sealed class ProjectCustomerHandler(
    IOptions<GoogleChannelOptions> options,
    ServiceAccountChannelClientProvider clients,
    EstateObserver observer,
    ILogger<ProjectCustomerHandler> logger)
{
    public async Task<OutgoingMessages> Handle(ProjectCustomer command, IDocumentSession session, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        if (!options.Value.ReadModelSyncEnabled || clients.Client is not { } client)
        {
            logger.LogDebug("Skipping projection of {Customer} ({Reason}): the read-model sync is not configured.", command.CustomerId, command.Reason);
            return outgoing;
        }

        await observer.ProjectCustomerAsync(session, client, command.CustomerId, ObservationSource.Notification, DateTimeOffset.UtcNow, ct);
        var record = await session.LoadAsync<Core.Estate.ReadModels.CustomerRecord>(EstateStreams.Customer(command.CustomerId), ct);
        outgoing.Add(new CustomerReadModelUpdated(command.CustomerId, record?.OwningLinkId, command.Reason));
        logger.LogInformation("Projected customer {Customer} from live state ({Reason}).", command.CustomerId, command.Reason);
        return outgoing;
    }

    public static ProjectCustomer Handle(CustomerChanged message) => new(message.CustomerId, $"mutation:{message.Reason}");

    public static ProjectCustomer Handle(EntitlementChanged message) => new(message.CustomerId, $"entitlement {message.Action}");
}
