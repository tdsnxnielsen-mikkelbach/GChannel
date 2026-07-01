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

        // Customer data is mutable, so list/get are cached only briefly and the cache is
        // invalidated on every create/update/delete to keep the UI consistent.
        group.MapGet("/", (
                IGoogleChannelClient channel,
                IDistributedCache cache,
                IOptions<GoogleChannelOptions> options,
                CancellationToken cancellationToken) =>
                CachedAsync(cache, ListCacheKey, options.Value.CacheSeconds,
                    () => channel.ListCustomersAsync(cancellationToken), cancellationToken))
            .WithName("ListCustomers")
            .WithSummary("Lists the reseller's customers.");

        group.MapGet("/{customerId}", (
                string customerId,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                IOptions<GoogleChannelOptions> options,
                CancellationToken cancellationToken) =>
                CachedAsync(cache, GetCacheKey(customerId), options.Value.CacheSeconds,
                    () => channel.GetCustomerAsync(customerId, cancellationToken), cancellationToken))
            .WithName("GetCustomer")
            .WithSummary("Gets a single customer.");

        group.MapPost("/", async (
                SaveCustomerRequest request,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                CancellationToken cancellationToken) =>
            {
                var created = await channel.CreateCustomerAsync(request, cancellationToken);
                await InvalidateAsync(cache, created.Id, cancellationToken);
                return Results.Created($"/api/customers/{created.Id}", created);
            })
            .WithName("CreateCustomer")
            .WithSummary("Creates a customer.");

        group.MapPut("/{customerId}", async (
                string customerId,
                SaveCustomerRequest request,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                CancellationToken cancellationToken) =>
            {
                var updated = await channel.UpdateCustomerAsync(request with { Id = customerId }, cancellationToken);
                await InvalidateAsync(cache, customerId, cancellationToken);
                return Results.Ok(updated);
            })
            .WithName("UpdateCustomer")
            .WithSummary("Updates a customer.");

        group.MapDelete("/{customerId}", async (
                string customerId,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                CancellationToken cancellationToken) =>
            {
                await channel.DeleteCustomerAsync(customerId, cancellationToken);
                await InvalidateAsync(cache, customerId, cancellationToken);
                return Results.NoContent();
            })
            .WithName("DeleteCustomer")
            .WithSummary("Deletes a customer.");

        // Import a pre-existing Cloud Identity customer (pre-transfer). Synchronous — returns the
        // Customer directly — so it's a plain Created, not routed through the Operations (LRO) UX.
        group.MapPost("/import", async (
                ImportCustomerRequest request,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                CancellationToken cancellationToken) =>
            {
                var imported = await channel.ImportCustomerAsync(request, cancellationToken);
                await InvalidateAsync(cache, imported.Id, cancellationToken);
                return Results.Created($"/api/customers/{imported.Id}", imported);
            })
            .WithName("ImportCustomer")
            .WithSummary("Imports a pre-existing Cloud Identity customer before transfer.");

        // Provision a new Cloud Identity — a long-running operation. Return 202 Accepted with the
        // operation so the UI can track it on the Operations page (§7), mirroring the entitlement LROs.
        group.MapPost("/{customerId}/provision-cloud-identity", async (
                string customerId,
                ProvisionCloudIdentityRequest request,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                CancellationToken cancellationToken) =>
            {
                var operation = await channel.ProvisionCloudIdentityAsync(customerId, request, cancellationToken);
                await InvalidateAsync(cache, customerId, cancellationToken);
                return Results.Accepted($"/api/operations/{operation.Id}", operation);
            })
            .WithName("ProvisionCloudIdentity")
            .WithSummary("Provisions a new Cloud Identity for a customer (long-running operation).");

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

    private const string ListCacheKey = "customers:list";

    private static string GetCacheKey(string customerId) => $"customers:get:{customerId}";

    /// <summary>Drops the cached customer list and a specific customer after a mutation.</summary>
    private static async Task InvalidateAsync(IDistributedCache cache, string customerId, CancellationToken cancellationToken)
    {
        await cache.RemoveAsync(ListCacheKey, cancellationToken);
        if (!string.IsNullOrEmpty(customerId))
        {
            await cache.RemoveAsync(GetCacheKey(customerId), cancellationToken);
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
