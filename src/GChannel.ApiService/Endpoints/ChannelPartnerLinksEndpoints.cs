using System.Text.Json;
using GChannel.ApiService.Configuration;
using GChannel.ApiService.Services;
using GChannel.Shared.Contracts;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace GChannel.ApiService.Endpoints;

/// <summary>
/// Maps the channel partner link endpoints (§5 — distributor / n-tier): managing the links
/// (<c>list</c> / <c>get</c> / <c>create</c> / <c>patch</c>) plus listing the customers owned by a
/// partner (<c>channelPartnerLinks.customers.list</c>). Links live at the account level, so they are
/// rooted off <c>/api/channel-partner-links</c> rather than under a customer.
/// </summary>
public static class ChannelPartnerLinksEndpoints
{
    public static IEndpointRouteBuilder MapChannelPartnerLinksEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/channel-partner-links").WithTags("ChannelPartnerLinks");

        // Links change rarely, so list/get are cached briefly and invalidated on create/patch.
        group.MapGet("/", (
                IGoogleChannelClient channel,
                IDistributedCache cache,
                IOptions<GoogleChannelOptions> options,
                CancellationToken cancellationToken) =>
                CachedAsync(cache, ListCacheKey, options.Value.CacheSeconds,
                    () => channel.ListChannelPartnerLinksAsync(cancellationToken), cancellationToken))
            .WithName("ListChannelPartnerLinks")
            .WithSummary("Lists the reseller account's channel partner links.");

        group.MapGet("/{linkId}", (
                string linkId,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                IOptions<GoogleChannelOptions> options,
                CancellationToken cancellationToken) =>
                CachedAsync(cache, GetCacheKey(linkId), options.Value.CacheSeconds,
                    () => channel.GetChannelPartnerLinkAsync(linkId, cancellationToken), cancellationToken))
            .WithName("GetChannelPartnerLink")
            .WithSummary("Gets a single channel partner link.");

        group.MapGet("/{linkId}/customers", (
                string linkId,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                IOptions<GoogleChannelOptions> options,
                CancellationToken cancellationToken) =>
                CachedAsync(cache, CustomersCacheKey(linkId), options.Value.CacheSeconds,
                    () => channel.ListChannelPartnerCustomersAsync(linkId, cancellationToken), cancellationToken))
            .WithName("ListChannelPartnerCustomers")
            .WithSummary("Lists the customers owned by a channel partner link.");

        group.MapPost("/", async (
                CreateChannelPartnerLinkRequest request,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                CancellationToken cancellationToken) =>
            {
                var created = await channel.CreateChannelPartnerLinkAsync(request, cancellationToken);

                // Remember the domain we invited (keyed by reseller Cloud Identity ID) so a later
                // Cloud Identity check on the same domain can correlate this link even before Google
                // populates the partner's primary domain on a freshly INVITED link.
                if (!string.IsNullOrWhiteSpace(request.Domain))
                {
                    await cache.SetStringAsync(
                        InvitedDomainCacheKey(request.ResellerCloudIdentityId),
                        request.Domain.Trim(),
                        new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(90) },
                        cancellationToken);
                }

                await InvalidateAsync(cache, created.Id, cancellationToken);
                return Results.Created($"/api/channel-partner-links/{created.Id}", created);
            })
            .WithName("CreateChannelPartnerLink")
            .WithSummary("Invites a downstream reseller by creating a channel partner link.");

        group.MapPut("/{linkId}/state", async (
                string linkId,
                UpdateChannelPartnerLinkRequest request,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                CancellationToken cancellationToken) =>
            {
                var updated = await channel.UpdateChannelPartnerLinkStateAsync(linkId, request, cancellationToken);
                await InvalidateAsync(cache, linkId, cancellationToken);
                return Results.Ok(updated);
            })
            .WithName("UpdateChannelPartnerLinkState")
            .WithSummary("Updates a channel partner link's state.");

        return app;
    }

    private const string ListCacheKey = "channel-partner-links:list";

    private static string GetCacheKey(string linkId) => $"channel-partner-links:get:{linkId}";

    private static string CustomersCacheKey(string linkId) => $"channel-partner-links:{linkId}:customers";

    /// <summary>
    /// Cache key for the domain we recorded when inviting a reseller, keyed by their Cloud Identity
    /// ID. Read by the Cloud Identity check to cross-correlate a domain to a (possibly still pending)
    /// partner link invitation.
    /// </summary>
    internal static string InvitedDomainCacheKey(string resellerCloudIdentityId) =>
        $"cpl-invited-domain:{resellerCloudIdentityId.ToLowerInvariant()}";

    /// <summary>Drops the cached link list and a specific link after a mutation.</summary>
    private static async Task InvalidateAsync(IDistributedCache cache, string linkId, CancellationToken cancellationToken)
    {
        await cache.RemoveAsync(ListCacheKey, cancellationToken);
        if (!string.IsNullOrEmpty(linkId))
        {
            await cache.RemoveAsync(GetCacheKey(linkId), cancellationToken);
        }
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
