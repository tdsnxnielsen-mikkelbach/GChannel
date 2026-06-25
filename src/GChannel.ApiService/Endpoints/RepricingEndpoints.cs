using System.Text.Json;
using GChannel.ApiService.Configuration;
using GChannel.ApiService.Services;
using GChannel.Shared.Contracts;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace GChannel.ApiService.Endpoints;

/// <summary>
/// Maps the repricing / rebilling-margin endpoints (§6). Customer repricing configs hang off a
/// customer (the reseller's margin on that customer's bill) and channel partner repricing configs
/// hang off a channel partner link (a distributor's margin on a downstream reseller's bill). Both
/// return the config resource directly (not long-running operations), so the UI reflects changes
/// immediately. Lists are cached briefly and invalidated on every mutation.
/// </summary>
public static class RepricingEndpoints
{
    public static IEndpointRouteBuilder MapRepricingEndpoints(this IEndpointRouteBuilder app)
    {
        MapCustomerRepricing(app);
        MapChannelPartnerRepricing(app);
        return app;
    }

    private static void MapCustomerRepricing(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/customers/{customerId}/repricing-configs").WithTags("Repricing");

        group.MapGet("/", (
                string customerId,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                IOptions<GoogleChannelOptions> options,
                CancellationToken cancellationToken) =>
                CachedAsync(cache, CustomerCacheKey(customerId), options.Value.CacheSeconds,
                    () => channel.ListCustomerRepricingConfigsAsync(customerId, cancellationToken), cancellationToken))
            .WithName("ListCustomerRepricingConfigs")
            .WithSummary("Lists a customer's repricing (rebilling-margin) configs.");

        group.MapPost("/", async (
                string customerId,
                SaveRepricingConfigRequest request,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                CancellationToken cancellationToken) =>
            {
                var created = await channel.CreateCustomerRepricingConfigAsync(customerId, request, cancellationToken);
                await cache.RemoveAsync(CustomerCacheKey(customerId), cancellationToken);
                return Results.Created(ApiRoutes.CustomerRepricingConfig(customerId, created.Id), created);
            })
            .WithName("CreateCustomerRepricingConfig")
            .WithSummary("Creates a customer repricing config.");

        group.MapPut("/{configId}", async (
                string customerId,
                string configId,
                SaveRepricingConfigRequest request,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                CancellationToken cancellationToken) =>
            {
                var updated = await channel.UpdateCustomerRepricingConfigAsync(customerId, configId, request, cancellationToken);
                await cache.RemoveAsync(CustomerCacheKey(customerId), cancellationToken);
                return Results.Ok(updated);
            })
            .WithName("UpdateCustomerRepricingConfig")
            .WithSummary("Updates a customer repricing config.");

        group.MapDelete("/{configId}", async (
                string customerId,
                string configId,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                CancellationToken cancellationToken) =>
            {
                await channel.DeleteCustomerRepricingConfigAsync(customerId, configId, cancellationToken);
                await cache.RemoveAsync(CustomerCacheKey(customerId), cancellationToken);
                return Results.NoContent();
            })
            .WithName("DeleteCustomerRepricingConfig")
            .WithSummary("Deletes a customer repricing config.");
    }

    private static void MapChannelPartnerRepricing(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/channel-partner-links/{linkId}/repricing-configs").WithTags("Repricing");

        group.MapGet("/", (
                string linkId,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                IOptions<GoogleChannelOptions> options,
                CancellationToken cancellationToken) =>
                CachedAsync(cache, PartnerCacheKey(linkId), options.Value.CacheSeconds,
                    () => channel.ListChannelPartnerRepricingConfigsAsync(linkId, cancellationToken), cancellationToken))
            .WithName("ListChannelPartnerRepricingConfigs")
            .WithSummary("Lists a channel partner link's repricing (rebilling-margin) configs.");

        group.MapPost("/", async (
                string linkId,
                SaveRepricingConfigRequest request,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                CancellationToken cancellationToken) =>
            {
                var created = await channel.CreateChannelPartnerRepricingConfigAsync(linkId, request, cancellationToken);
                await cache.RemoveAsync(PartnerCacheKey(linkId), cancellationToken);
                return Results.Created(ApiRoutes.ChannelPartnerRepricingConfig(linkId, created.Id), created);
            })
            .WithName("CreateChannelPartnerRepricingConfig")
            .WithSummary("Creates a channel partner repricing config.");

        group.MapPut("/{configId}", async (
                string linkId,
                string configId,
                SaveRepricingConfigRequest request,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                CancellationToken cancellationToken) =>
            {
                var updated = await channel.UpdateChannelPartnerRepricingConfigAsync(linkId, configId, request, cancellationToken);
                await cache.RemoveAsync(PartnerCacheKey(linkId), cancellationToken);
                return Results.Ok(updated);
            })
            .WithName("UpdateChannelPartnerRepricingConfig")
            .WithSummary("Updates a channel partner repricing config.");

        group.MapDelete("/{configId}", async (
                string linkId,
                string configId,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                CancellationToken cancellationToken) =>
            {
                await channel.DeleteChannelPartnerRepricingConfigAsync(linkId, configId, cancellationToken);
                await cache.RemoveAsync(PartnerCacheKey(linkId), cancellationToken);
                return Results.NoContent();
            })
            .WithName("DeleteChannelPartnerRepricingConfig")
            .WithSummary("Deletes a channel partner repricing config.");
    }

    private static string CustomerCacheKey(string customerId) => $"customer:{customerId}:repricing-configs";

    private static string PartnerCacheKey(string linkId) => $"channel-partner-links:{linkId}:repricing-configs";

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
