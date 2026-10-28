using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;

namespace GChannel.ApiService.Caching;

/// <summary>
/// Redis-backed read-through cache for idempotent Google Channel API reads. Redis is shared by every API
/// replica, so a warm lookup never hits Google — reducing latency and shrinking the 429 surface. Mutating
/// endpoints invalidate the affected keys; the worker's <c>CustomerReadModelUpdated</c> events evict a
/// customer's keys when an external change is observed.
/// </summary>
public static class GoogleReadCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Returns a cached JSON payload when present, otherwise invokes <paramref name="factory"/> and caches it.</summary>
    public static async Task<IResult> CachedAsync<T>(
        IDistributedCache cache,
        string cacheKey,
        int cacheSeconds,
        Func<Task<T>> factory,
        CancellationToken cancellationToken)
    {
        var result = await GetOrSetAsync(cache, cacheKey, cacheSeconds, factory, cancellationToken);
        return Results.Ok(result);
    }

    /// <summary>Value-returning variant of <see cref="CachedAsync{T}"/>.</summary>
    public static async Task<T> GetOrSetAsync<T>(
        IDistributedCache cache,
        string cacheKey,
        int cacheSeconds,
        Func<Task<T>> factory,
        CancellationToken cancellationToken)
    {
        var cached = await cache.GetStringAsync(cacheKey, cancellationToken);
        if (cached is not null)
        {
            var hit = JsonSerializer.Deserialize<T>(cached, Json);
            if (hit is not null)
            {
                return hit;
            }
        }

        var result = await factory();
        await SetAsync(cache, cacheKey, result, cacheSeconds, cancellationToken);
        return result;
    }

    public static Task SetAsync<T>(IDistributedCache cache, string cacheKey, T value, int cacheSeconds, CancellationToken cancellationToken)
        => cache.SetStringAsync(
            cacheKey,
            JsonSerializer.Serialize(value, Json),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(Math.Max(1, cacheSeconds)) },
            cancellationToken);

    public static async Task<T?> GetAsync<T>(IDistributedCache cache, string cacheKey, CancellationToken cancellationToken) where T : class
    {
        var cached = await cache.GetStringAsync(cacheKey, cancellationToken);
        return cached is null ? null : JsonSerializer.Deserialize<T>(cached, Json);
    }

    // ---- Well-known keys -------------------------------------------------------------------------

    public const string CustomersListKey = "customers:list";
    public static string CustomerKey(string customerId) => $"customers:get:{customerId}";
    public static string EntitlementsListKey(string customerId) => $"entitlements:{customerId}:list";
    public static string EntitlementKey(string customerId, string entitlementId) => $"entitlements:{customerId}:get:{entitlementId}";
    public static string EntitlementChangesKey(string customerId, string entitlementId) => $"entitlements:{customerId}:{entitlementId}:changes";
    public static string EntitlementOfferKey(string customerId, string entitlementId) => $"entitlements:{customerId}:{entitlementId}:offer";
    public static string TransferableSkusKey(string customerId) => $"customer:{customerId}:transferable-skus";
    public static string CustomerRepricingKey(string customerId) => $"customer:{customerId}:repricing-configs";
    public const string ChannelPartnerLinksListKey = "channel-partner-links:list";
    public static string ChannelPartnerLinkKey(string linkId) => $"channel-partner-links:get:{linkId}";
    public static string ChannelPartnerCustomersKey(string linkId) => $"channel-partner-links:{linkId}:customers";
    public static string ChannelPartnerCustomerKey(string linkId, string customerId) => $"channel-partner-links:{linkId}:customers:{customerId}";
    public static string ChannelPartnerRepricingKey(string linkId) => $"channel-partner-links:{linkId}:repricing-configs";
    /// <summary>The domain we recorded when inviting a reseller, keyed by their Cloud Identity id (read by the Cloud Identity check).</summary>
    public static string InvitedDomainKey(string resellerCloudIdentityId) => $"cpl-invited-domain:{resellerCloudIdentityId.ToLowerInvariant()}";

    /// <summary>Evicts every cached read that concerns one customer (list + get + entitlements + transfers + partner-link customers).</summary>
    public static async Task EvictCustomerAsync(IDistributedCache cache, string customerId, string? owningLinkId, CancellationToken cancellationToken)
    {
        await cache.RemoveAsync(CustomersListKey, cancellationToken);
        await cache.RemoveAsync(CustomerKey(customerId), cancellationToken);
        await cache.RemoveAsync(EntitlementsListKey(customerId), cancellationToken);
        await cache.RemoveAsync(TransferableSkusKey(customerId), cancellationToken);
        if (!string.IsNullOrEmpty(owningLinkId))
        {
            await cache.RemoveAsync(ChannelPartnerCustomersKey(owningLinkId), cancellationToken);
            await cache.RemoveAsync(ChannelPartnerCustomerKey(owningLinkId, customerId), cancellationToken);
        }
    }
}
