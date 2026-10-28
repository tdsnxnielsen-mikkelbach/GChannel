using GChannel.ApiService.Caching;
using GChannel.Core.Messaging;
using Microsoft.Extensions.Caching.Distributed;

namespace GChannel.ApiService.Handlers;

/// <summary>
/// Listens on the <c>api-cache</c> Service Bus subscription: when the worker has (re-)projected a customer
/// from live Channel API state — a Pub/Sub change notification, a targeted refresh or the periodic sync —
/// evict the Redis read cache for that customer so the next live read isn't served stale.
/// </summary>
public static class CustomerReadModelUpdatedHandler
{
    public static Task Handle(CustomerReadModelUpdated message, IDistributedCache cache, CancellationToken cancellationToken)
        => GoogleReadCache.EvictCustomerAsync(cache, message.CustomerId, message.OwningLinkId, cancellationToken);
}
