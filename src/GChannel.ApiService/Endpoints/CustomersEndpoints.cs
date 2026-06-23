using System.Text.Json;
using GChannel.ApiService.Configuration;
using GChannel.ApiService.Services;
using GChannel.Shared.Contracts;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace GChannel.ApiService.Endpoints;

/// <summary>
/// Maps the customer-management endpoints (CRUD) plus the read-only purchasable-catalog
/// endpoints that correlate a customer back to the Catalog.
/// </summary>
public static class CustomersEndpoints
{
    public static IEndpointRouteBuilder MapCustomersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/customers").WithTags("Customers");

        // Customer data is mutable, so list/get are served live (never cached) to avoid staleness.
        group.MapGet("/", async (
                IGoogleChannelClient channel,
                CancellationToken cancellationToken) =>
                Results.Ok(await channel.ListCustomersAsync(cancellationToken)))
            .WithName("ListCustomers")
            .WithSummary("Lists the reseller's customers.");

        group.MapGet("/{customerId}", async (
                string customerId,
                IGoogleChannelClient channel,
                CancellationToken cancellationToken) =>
                Results.Ok(await channel.GetCustomerAsync(customerId, cancellationToken)))
            .WithName("GetCustomer")
            .WithSummary("Gets a single customer.");

        group.MapPost("/", async (
                SaveCustomerRequest request,
                IGoogleChannelClient channel,
                CancellationToken cancellationToken) =>
            {
                var created = await channel.CreateCustomerAsync(request, cancellationToken);
                return Results.Created($"/api/customers/{created.Id}", created);
            })
            .WithName("CreateCustomer")
            .WithSummary("Creates a customer.");

        group.MapPut("/{customerId}", async (
                string customerId,
                SaveCustomerRequest request,
                IGoogleChannelClient channel,
                CancellationToken cancellationToken) =>
            {
                var updated = await channel.UpdateCustomerAsync(request with { Id = customerId }, cancellationToken);
                return Results.Ok(updated);
            })
            .WithName("UpdateCustomer")
            .WithSummary("Updates a customer.");

        group.MapDelete("/{customerId}", async (
                string customerId,
                IGoogleChannelClient channel,
                CancellationToken cancellationToken) =>
            {
                await channel.DeleteCustomerAsync(customerId, cancellationToken);
                return Results.NoContent();
            })
            .WithName("DeleteCustomer")
            .WithSummary("Deletes a customer.");

        // Purchasable catalog reads are idempotent and safe to cache briefly.
        group.MapGet("/{customerId}/purchasable-skus", (
                string customerId,
                string productId,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                IOptions<GoogleChannelOptions> options,
                CancellationToken cancellationToken) =>
                CachedAsync(cache, $"customer:{customerId}:purchasable-skus:{productId}", options.Value.CacheSeconds,
                    () => channel.ListPurchasableSkusAsync(customerId, productId, cancellationToken), cancellationToken))
            .WithName("ListPurchasableSkus")
            .WithSummary("Lists the SKUs a customer is eligible to purchase within a product.");

        group.MapGet("/{customerId}/purchasable-offers", (
                string customerId,
                string productId,
                string skuId,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                IOptions<GoogleChannelOptions> options,
                CancellationToken cancellationToken) =>
                CachedAsync(cache, $"customer:{customerId}:purchasable-offers:{productId}:{skuId}", options.Value.CacheSeconds,
                    () => channel.ListPurchasableOffersAsync(customerId, productId, skuId, cancellationToken), cancellationToken))
            .WithName("ListPurchasableOffers")
            .WithSummary("Lists the offers a customer is eligible to purchase for a SKU.");

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
