using System.Text.Json;
using GChannel.ApiService.Configuration;
using GChannel.ApiService.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace GChannel.ApiService.Endpoints;

/// <summary>Maps the read-only Catalog endpoints (products, SKUs, offers, SKU groups).</summary>
public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/catalog").WithTags("Catalog");

        group.MapGet("/products", (
                IGoogleChannelClient channel,
                IDistributedCache cache,
                IOptions<GoogleChannelOptions> options,
                CancellationToken cancellationToken) =>
                CachedAsync(cache, "catalog:products", options.Value.CacheSeconds,
                    () => channel.ListProductsAsync(cancellationToken), cancellationToken))
            .WithName("ListProducts")
            .WithSummary("Lists the products the reseller is authorized to sell.");

        group.MapGet("/products/{productId}/skus", (
                string productId,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                IOptions<GoogleChannelOptions> options,
                CancellationToken cancellationToken) =>
                CachedAsync(cache, $"catalog:skus:{productId}", options.Value.CacheSeconds,
                    () => channel.ListSkusAsync(productId, cancellationToken), cancellationToken))
            .WithName("ListSkus")
            .WithSummary("Lists the SKUs for a product.");

        group.MapGet("/offers", (
                IGoogleChannelClient channel,
                IDistributedCache cache,
                IOptions<GoogleChannelOptions> options,
                CancellationToken cancellationToken) =>
                CachedAsync(cache, "catalog:offers", options.Value.CacheSeconds,
                    () => channel.ListOffersAsync(cancellationToken), cancellationToken))
            .WithName("ListOffers")
            .WithSummary("Lists the offers the reseller can sell.");

        group.MapGet("/sku-groups", (
                IGoogleChannelClient channel,
                IDistributedCache cache,
                IOptions<GoogleChannelOptions> options,
                CancellationToken cancellationToken) =>
                CachedAsync(cache, "catalog:skugroups", options.Value.CacheSeconds,
                    () => channel.ListSkuGroupsAsync(cancellationToken), cancellationToken))
            .WithName("ListSkuGroups")
            .WithSummary("Lists the rebilling-supported SKU groups.");

        group.MapGet("/sku-groups/{skuGroupId}/billable-skus", (
                string skuGroupId,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                IOptions<GoogleChannelOptions> options,
                CancellationToken cancellationToken) =>
                CachedAsync(cache, $"catalog:billableskus:{skuGroupId}", options.Value.CacheSeconds,
                    () => channel.ListBillableSkusAsync(skuGroupId, cancellationToken), cancellationToken))
            .WithName("ListBillableSkus")
            .WithSummary("Lists the billable SKUs in a SKU group.");

        return app;
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
