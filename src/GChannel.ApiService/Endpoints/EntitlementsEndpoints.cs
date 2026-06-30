using System.Globalization;
using System.Text.Json;
using GChannel.ApiService.Configuration;
using GChannel.ApiService.Data;
using GChannel.ApiService.Services;
using GChannel.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace GChannel.ApiService.Endpoints;

/// <summary>
/// Maps the entitlement-lifecycle endpoints (the core selling flow): read paths (list / get /
/// change history / offer lookup) plus the mutating purchase, modify and state-change calls.
/// Entitlements are nested under a customer, mirroring the Cloud Channel resource hierarchy.
/// </summary>
public static class EntitlementsEndpoints
{
    public static IEndpointRouteBuilder MapEntitlementsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/customers/{customerId}/entitlements").WithTags("Entitlements");

        // Entitlements are mutable (state changes), so list/get are cached only briefly and the
        // cache is invalidated on every mutation for the affected customer.
        group.MapGet("/", async (
                string customerId,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                GChannelDbContext db,
                IOptions<GoogleChannelOptions> options,
                CancellationToken cancellationToken) =>
            {
                // §10 read-model: when enabled and this customer has been synced, serve the list from
                // SQL so the contended ListEntitlements quota stays reserved for the background sync.
                // Falls back to a live (cached) call for customers not yet in the read-model (cold start).
                if (options.Value.UseReadModel
                    && await db.CustomerRecords.AsNoTracking()
                        .AnyAsync(c => c.CustomerId == customerId && !c.IsDeleted, cancellationToken))
                {
                    var rows = await db.EntitlementRecords.AsNoTracking()
                        .Where(r => r.CustomerId == customerId && !r.IsDeleted)
                        .OrderByDescending(r => r.CreateTime)
                        .ToListAsync(cancellationToken);
                    return Results.Ok(new EntitlementsResult { Entitlements = rows.Select(MapEntitlement).ToList() });
                }

                return await CachedAsync(cache, ListCacheKey(customerId), options.Value.CacheSeconds,
                    () => channel.ListEntitlementsAsync(customerId, cancellationToken), cancellationToken);
            })
            .WithName("ListEntitlements")
            .WithSummary("Lists a customer's entitlements.");

        group.MapGet("/{entitlementId}", (
                string customerId,
                string entitlementId,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                IOptions<GoogleChannelOptions> options,
                CancellationToken cancellationToken) =>
                CachedAsync(cache, GetCacheKey(customerId, entitlementId), options.Value.CacheSeconds,
                    () => channel.GetEntitlementAsync(customerId, entitlementId, cancellationToken), cancellationToken))
            .WithName("GetEntitlement")
            .WithSummary("Gets a single entitlement.");

        group.MapGet("/{entitlementId}/changes", (
                string customerId,
                string entitlementId,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                IOptions<GoogleChannelOptions> options,
                CancellationToken cancellationToken) =>
                CachedAsync(cache, $"entitlements:{customerId}:{entitlementId}:changes", options.Value.CacheSeconds,
                    () => channel.ListEntitlementChangesAsync(customerId, entitlementId, cancellationToken), cancellationToken))
            .WithName("ListEntitlementChanges")
            .WithSummary("Lists an entitlement's change history.");

        group.MapGet("/{entitlementId}/offer", (
                string customerId,
                string entitlementId,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                IOptions<GoogleChannelOptions> options,
                CancellationToken cancellationToken) =>
                CachedAsync(cache, $"entitlements:{customerId}:{entitlementId}:offer", options.Value.CacheSeconds,
                    () => channel.LookupEntitlementOfferAsync(customerId, entitlementId, cancellationToken), cancellationToken))
            .WithName("LookupEntitlementOffer")
            .WithSummary("Looks up the Offer backing an entitlement.");

        // Purchase (create). Returns a long-running operation; the cache for this customer is dropped
        // so the new entitlement shows up once Google finishes provisioning.
        group.MapPost("/", async (
                string customerId,
                PurchaseEntitlementRequest request,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                CancellationToken cancellationToken) =>
            {
                var operation = await channel.CreateEntitlementAsync(customerId, request, cancellationToken);
                await InvalidateAsync(cache, customerId, null, cancellationToken);
                return Results.Accepted(value: operation);
            })
            .WithName("CreateEntitlement")
            .WithSummary("Purchases (creates) an entitlement.");

        group.MapPost("/{entitlementId}/change-offer", (
                string customerId,
                string entitlementId,
                ChangeOfferRequest request,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                CancellationToken cancellationToken) =>
                MutateAsync(cache, customerId, entitlementId,
                    () => channel.ChangeEntitlementOfferAsync(customerId, entitlementId, request, cancellationToken), cancellationToken))
            .WithName("ChangeEntitlementOffer")
            .WithSummary("Changes the Offer of an entitlement.");

        group.MapPost("/{entitlementId}/change-parameters", (
                string customerId,
                string entitlementId,
                ChangeParametersRequest request,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                CancellationToken cancellationToken) =>
                MutateAsync(cache, customerId, entitlementId,
                    () => channel.ChangeEntitlementParametersAsync(customerId, entitlementId, request, cancellationToken), cancellationToken))
            .WithName("ChangeEntitlementParameters")
            .WithSummary("Changes the parameters (e.g. seats) of an entitlement.");

        group.MapPost("/{entitlementId}/change-renewal", (
                string customerId,
                string entitlementId,
                ChangeRenewalSettingsRequest request,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                CancellationToken cancellationToken) =>
                MutateAsync(cache, customerId, entitlementId,
                    () => channel.ChangeEntitlementRenewalAsync(customerId, entitlementId, request, cancellationToken), cancellationToken))
            .WithName("ChangeEntitlementRenewal")
            .WithSummary("Changes the renewal settings of an entitlement.");

        group.MapPost("/{entitlementId}/activate", (
                string customerId,
                string entitlementId,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                CancellationToken cancellationToken) =>
                MutateAsync(cache, customerId, entitlementId,
                    () => channel.ActivateEntitlementAsync(customerId, entitlementId, cancellationToken), cancellationToken))
            .WithName("ActivateEntitlement")
            .WithSummary("Activates a suspended entitlement.");

        group.MapPost("/{entitlementId}/suspend", (
                string customerId,
                string entitlementId,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                CancellationToken cancellationToken) =>
                MutateAsync(cache, customerId, entitlementId,
                    () => channel.SuspendEntitlementAsync(customerId, entitlementId, cancellationToken), cancellationToken))
            .WithName("SuspendEntitlement")
            .WithSummary("Suspends an entitlement.");

        group.MapPost("/{entitlementId}/cancel", (
                string customerId,
                string entitlementId,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                CancellationToken cancellationToken) =>
                MutateAsync(cache, customerId, entitlementId,
                    () => channel.CancelEntitlementAsync(customerId, entitlementId, cancellationToken), cancellationToken))
            .WithName("CancelEntitlement")
            .WithSummary("Cancels an entitlement.");

        group.MapPost("/{entitlementId}/start-paid-service", (
                string customerId,
                string entitlementId,
                IGoogleChannelClient channel,
                IDistributedCache cache,
                CancellationToken cancellationToken) =>
                MutateAsync(cache, customerId, entitlementId,
                    () => channel.StartPaidServiceAsync(customerId, entitlementId, cancellationToken), cancellationToken))
            .WithName("StartPaidService")
            .WithSummary("Starts paid service for a trial entitlement.");

        return app;
    }

    private static string ListCacheKey(string customerId) => $"entitlements:{customerId}:list";

    /// <summary>
    /// Projects a §10 read-model entitlement row onto the shared <see cref="Entitlement"/> contract.
    /// Display names, create time and seat count are denormalised at sync time, so the list renders
    /// identically to the live path. The seat total is re-exposed as a <c>num_units</c> parameter so
    /// the UI's existing seat-resolution logic works unchanged.
    /// </summary>
    private static Entitlement MapEntitlement(EntitlementRecord r) => new()
    {
        Name = $"customers/{r.CustomerId}/entitlements/{r.EntitlementId}",
        Id = r.EntitlementId,
        OfferId = r.OfferId,
        OfferDisplayName = r.OfferName,
        ProductId = r.ProductId,
        ProductDisplayName = r.ProductName,
        SkuId = r.SkuId,
        SkuDisplayName = r.SkuName,
        ProvisioningState = r.State,
        IsTrial = r.IsTrial,
        CreateTime = r.CreateTime,
        Parameters = r.Seats > 0
            ? [new EntitlementParameter { Name = "num_units", Value = r.Seats.ToString(CultureInfo.InvariantCulture), Editable = true }]
            : [],
    };

    private static string GetCacheKey(string customerId, string entitlementId) =>
        $"entitlements:{customerId}:get:{entitlementId}";

    /// <summary>Runs a mutating entitlement call, invalidates the affected caches and returns the operation.</summary>
    private static async Task<IResult> MutateAsync(
        IDistributedCache cache,
        string customerId,
        string entitlementId,
        Func<Task<EntitlementOperation>> mutation,
        CancellationToken cancellationToken)
    {
        var operation = await mutation();
        await InvalidateAsync(cache, customerId, entitlementId, cancellationToken);
        return Results.Accepted(value: operation);
    }

    /// <summary>Drops the cached entitlement list and (optionally) a specific entitlement after a mutation.</summary>
    private static async Task InvalidateAsync(IDistributedCache cache, string customerId, string? entitlementId, CancellationToken cancellationToken)
    {
        await cache.RemoveAsync(ListCacheKey(customerId), cancellationToken);
        if (!string.IsNullOrEmpty(entitlementId))
        {
            await cache.RemoveAsync(GetCacheKey(customerId, entitlementId), cancellationToken);
            await cache.RemoveAsync($"entitlements:{customerId}:{entitlementId}:changes", cancellationToken);
            await cache.RemoveAsync($"entitlements:{customerId}:{entitlementId}:offer", cancellationToken);
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
