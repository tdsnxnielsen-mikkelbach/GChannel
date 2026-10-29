using GChannel.ApiService.Caching;
using GChannel.Core.Configuration;
using GChannel.Core.GoogleApis;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Wolverine.Http;

namespace GChannel.ApiService.Endpoints;

/// <summary>Read-only Catalog endpoints (products, SKUs, offers, SKU groups). Idempotent reads cached in Redis.</summary>
public static class CatalogEndpoints
{
    [WolverineGet("/api/catalog/products")]
    public static Task<IResult> ListProducts(
        IGoogleChannelClient channel, IDistributedCache cache, IOptions<GoogleChannelOptions> options, CancellationToken cancellationToken)
        => GoogleReadCache.CachedAsync(cache, "catalog:products", options.Value.CacheSeconds,
            () => channel.ListProductsAsync(cancellationToken), cancellationToken);

    [WolverineGet("/api/catalog/products/{productId}/skus")]
    public static Task<IResult> ListSkus(
        string productId, IGoogleChannelClient channel, IDistributedCache cache, IOptions<GoogleChannelOptions> options, CancellationToken cancellationToken)
        => GoogleReadCache.CachedAsync(cache, $"catalog:skus:{productId}", options.Value.CacheSeconds,
            () => channel.ListSkusAsync(productId, cancellationToken), cancellationToken);

    [WolverineGet("/api/catalog/offers")]
    public static Task<IResult> ListOffers(
        IGoogleChannelClient channel, IDistributedCache cache, IOptions<GoogleChannelOptions> options, CancellationToken cancellationToken)
        => GoogleReadCache.CachedAsync(cache, "catalog:offers", options.Value.CacheSeconds,
            () => channel.ListOffersAsync(cancellationToken), cancellationToken);

    [WolverineGet("/api/catalog/sku-groups")]
    public static Task<IResult> ListSkuGroups(
        IGoogleChannelClient channel, IDistributedCache cache, IOptions<GoogleChannelOptions> options, CancellationToken cancellationToken)
        => GoogleReadCache.CachedAsync(cache, "catalog:skugroups", options.Value.CacheSeconds,
            () => channel.ListSkuGroupsAsync(cancellationToken), cancellationToken);

    [WolverineGet("/api/catalog/sku-groups/{skuGroupId}/billable-skus")]
    public static Task<IResult> ListBillableSkus(
        string skuGroupId, IGoogleChannelClient channel, IDistributedCache cache, IOptions<GoogleChannelOptions> options, CancellationToken cancellationToken)
        => GoogleReadCache.CachedAsync(cache, $"catalog:billableskus:{skuGroupId}", options.Value.CacheSeconds,
            () => channel.ListBillableSkusAsync(skuGroupId, cancellationToken), cancellationToken);
}
