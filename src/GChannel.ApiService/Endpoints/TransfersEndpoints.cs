using GChannel.ApiService.Caching;
using GChannel.ApiService.Http;
using GChannel.Core.Configuration;
using GChannel.Core.Estate;
using GChannel.Core.GoogleApis;
using GChannel.Core.Messaging;
using GChannel.Shared.Contracts;
using Marten;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.Http;

namespace GChannel.ApiService.Endpoints;

/// <summary>
/// Transfer endpoints: the read-only transferability inspection plus the mutating transfer calls, nested
/// under a customer. Transfers return long-running operations; the request is recorded on the customer's
/// event stream and the operation handed to the worker's saga for tracking.
/// </summary>
public static class TransfersEndpoints
{
    [WolverineGet("/api/customers/{customerId}/transferable-skus")]
    public static Task<IResult> ListTransferableSkus(
        string customerId, IGoogleChannelClient channel, IDistributedCache cache, IOptions<GoogleChannelOptions> options, CancellationToken cancellationToken)
        => GoogleReadCache.CachedAsync(cache, GoogleReadCache.TransferableSkusKey(customerId), options.Value.CacheSeconds,
            () => channel.ListTransferableSkusAsync(customerId, cancellationToken), cancellationToken);

    [WolverineGet("/api/customers/{customerId}/transferable-offers")]
    public static Task<IResult> ListTransferableOffers(
        string customerId, string productId, string skuId, IGoogleChannelClient channel, IDistributedCache cache,
        IOptions<GoogleChannelOptions> options, CancellationToken cancellationToken)
        => GoogleReadCache.CachedAsync(cache, $"customer:{customerId}:transferable-offers:{productId}:{skuId}", options.Value.CacheSeconds,
            () => channel.ListTransferableOffersAsync(customerId, productId, skuId, cancellationToken), cancellationToken);

    [WolverinePost("/api/customers/{customerId}/transfer-entitlements")]
    public static async Task<IResult> TransferEntitlements(
        string customerId, TransferEntitlementsRequest request, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, IMessageBus bus, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
    {
        var operation = await channel.TransferEntitlementsAsync(customerId, request, cancellationToken);
        await InvalidateAsync(cache, customerId, cancellationToken);
        await RecordAsync(session, bus, observer, customerId, "transferEntitlements", operation, RequestUser.NameOf(http), cancellationToken);
        return Results.Accepted(value: operation);
    }

    [WolverinePost("/api/customers/{customerId}/transfer-entitlements-to-google")]
    public static async Task<IResult> TransferEntitlementsToGoogle(
        string customerId, TransferEntitlementsToGoogleRequest request, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, IMessageBus bus, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
    {
        var operation = await channel.TransferEntitlementsToGoogleAsync(customerId, request, cancellationToken);
        await InvalidateAsync(cache, customerId, cancellationToken);
        await RecordAsync(session, bus, observer, customerId, "transferEntitlementsToGoogle", operation, RequestUser.NameOf(http), cancellationToken);
        return Results.Accepted(value: operation);
    }

    private static async Task RecordAsync(
        IDocumentSession session, IMessageBus bus, EstateObserver observer, string customerId, string action,
        EntitlementOperation operation, string? user, CancellationToken cancellationToken)
    {
        await observer.RecordCustomerMutationAsync(session, customerId, action, user, operation.OperationName, cancellationToken);
        await bus.PublishAsync(new CustomerChanged(customerId, null, action, user));
        if (!string.IsNullOrEmpty(operation.OperationName) && !operation.Done)
        {
            await bus.PublishAsync(new OperationSubmitted(operation.OperationName, customerId, null, action, user, DateTimeOffset.UtcNow));
        }
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Drops the affected entitlement-list and transferability caches after a transfer.</summary>
    private static async Task InvalidateAsync(IDistributedCache cache, string customerId, CancellationToken cancellationToken)
    {
        await cache.RemoveAsync(GoogleReadCache.EntitlementsListKey(customerId), cancellationToken);
        await cache.RemoveAsync(GoogleReadCache.TransferableSkusKey(customerId), cancellationToken);
    }
}
