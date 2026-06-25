using System.Text.Json;
using GChannel.ApiService.Configuration;
using GChannel.ApiService.Services;
using GChannel.Shared.Contracts;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace GChannel.ApiService.Endpoints;

/// <summary>
/// Maps the transfer endpoints (§4): the read-only transferability inspection
/// (<c>listTransferableSkus</c> / <c>listTransferableOffers</c>) plus the mutating transfer calls
/// (<c>transferEntitlements</c> / <c>transferEntitlementsToGoogle</c>). Transfers are nested under a
/// customer, mirroring the Cloud Channel resource hierarchy and the entitlement-lifecycle routes.
/// </summary>
public static class TransfersEndpoints
{
    public static IEndpointRouteBuilder MapTransfersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/customers/{customerId}").WithTags("Transfers");

        // Transferability reads are idempotent and safe to cache briefly.
        group.MapGet("/transferable-skus", (
                string customerId,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                IOptions<GoogleChannelOptions> options,
                CancellationToken cancellationToken) =>
                CachedAsync(cache, $"customer:{customerId}:transferable-skus", options.Value.CacheSeconds,
                    () => channel.ListTransferableSkusAsync(customerId, cancellationToken), cancellationToken))
            .WithName("ListTransferableSkus")
            .WithSummary("Lists the SKUs a customer currently holds that could be transferred in.");

        group.MapGet("/transferable-offers", (
                string customerId,
                string productId,
                string skuId,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                IOptions<GoogleChannelOptions> options,
                CancellationToken cancellationToken) =>
                CachedAsync(cache, $"customer:{customerId}:transferable-offers:{productId}:{skuId}", options.Value.CacheSeconds,
                    () => channel.ListTransferableOffersAsync(customerId, productId, skuId, cancellationToken), cancellationToken))
            .WithName("ListTransferableOffers")
            .WithSummary("Lists the offers a customer is eligible to transfer in for a SKU.");

        // Transfer-in creates entitlements on this customer, so drop the customer's entitlement list
        // cache and the transferability caches so the UI reflects the new state once Google finishes.
        group.MapPost("/transfer-entitlements", async (
                string customerId,
                TransferEntitlementsRequest request,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                CancellationToken cancellationToken) =>
            {
                var operation = await channel.TransferEntitlementsAsync(customerId, request, cancellationToken);
                await InvalidateAsync(cache, customerId, cancellationToken);
                return Results.Accepted(value: operation);
            })
            .WithName("TransferEntitlements")
            .WithSummary("Transfers entitlements to this reseller.");

        group.MapPost("/transfer-entitlements-to-google", async (
                string customerId,
                TransferEntitlementsToGoogleRequest request,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                CancellationToken cancellationToken) =>
            {
                var operation = await channel.TransferEntitlementsToGoogleAsync(customerId, request, cancellationToken);
                await InvalidateAsync(cache, customerId, cancellationToken);
                return Results.Accepted(value: operation);
            })
            .WithName("TransferEntitlementsToGoogle")
            .WithSummary("Transfers entitlements back to Google (direct) billing.");

        return app;
    }

    /// <summary>Drops the affected entitlement-list and transferability caches after a transfer.</summary>
    private static async Task InvalidateAsync(IDistributedCache cache, string customerId, CancellationToken cancellationToken)
    {
        await cache.RemoveAsync($"entitlements:{customerId}:list", cancellationToken);
        await cache.RemoveAsync($"customer:{customerId}:transferable-skus", cancellationToken);
    }

    /// <summary>Returns a cached JSON payload when present, otherwise invokes <paramref name="factory"/> and caches it.</summary>
    private static async Task<IResult> CachedAsync<T>(
        IDistributedCache cache,
        string cacheKey,
        int cacheSeconds,
        Func<Task<T>> factory,
        CancellationToken cancellationToken)
    {
        var cached = await cache.GetStringAsync(cacheKey, cancellationToken);
        if (cached is not null)
        {
            return Results.Ok(JsonSerializer.Deserialize<T>(cached));
        }

        var result = await factory();

        await cache.SetStringAsync(
            cacheKey,
            JsonSerializer.Serialize(result),
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(cacheSeconds)
            },
            cancellationToken);

        return Results.Ok(result);
    }
}
