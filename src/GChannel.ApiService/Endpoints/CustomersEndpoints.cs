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
/// Customer-management endpoints (CRUD, import, Cloud Identity provisioning) plus the read-only
/// purchasable-catalog endpoints that correlate a customer back to the Catalog.
/// <para>
/// Mutations are recorded on the customer's event stream from the Channel API result (so the read model is
/// immediately consistent for the customer's own data) and publish a <see cref="CustomerChanged"/> message
/// through the Marten outbox so the worker re-projects the customer's entitlements within seconds.
/// </para>
/// </summary>
public static class CustomersEndpoints
{
    [WolverineGet("/api/customers")]
    public static Task<IResult> ListCustomers(
        IGoogleChannelClient channel, IDistributedCache cache, IOptions<GoogleChannelOptions> options, CancellationToken cancellationToken)
        => GoogleReadCache.CachedAsync(cache, GoogleReadCache.CustomersListKey, options.Value.CacheSeconds,
            () => channel.ListCustomersAsync(cancellationToken), cancellationToken);

    [WolverineGet("/api/customers/{customerId}")]
    public static Task<IResult> GetCustomer(
        string customerId, IGoogleChannelClient channel, IDistributedCache cache, IOptions<GoogleChannelOptions> options, CancellationToken cancellationToken)
        => GoogleReadCache.CachedAsync(cache, GoogleReadCache.CustomerKey(customerId), options.Value.CacheSeconds,
            () => channel.GetCustomerAsync(customerId, cancellationToken), cancellationToken);

    [WolverinePost("/api/customers")]
    public static async Task<IResult> CreateCustomer(
        SaveCustomerRequest request, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, IMessageBus bus, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
    {
        var created = await channel.CreateCustomerAsync(request, cancellationToken);
        await InvalidateAsync(cache, created.Id, cancellationToken);
        await RecordMutationAsync(session, bus, observer, created, "create", RequestUser.NameOf(http), cancellationToken);
        return Results.Created($"/api/customers/{created.Id}", created);
    }

    [WolverinePut("/api/customers/{customerId}")]
    public static async Task<IResult> UpdateCustomer(
        string customerId, SaveCustomerRequest request, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, IMessageBus bus, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
    {
        var updated = await channel.UpdateCustomerAsync(request with { Id = customerId }, cancellationToken);
        await InvalidateAsync(cache, customerId, cancellationToken);
        await RecordMutationAsync(session, bus, observer, updated, "update", RequestUser.NameOf(http), cancellationToken);
        return Results.Ok(updated);
    }

    [WolverineDelete("/api/customers/{customerId}")]
    public static async Task<IResult> DeleteCustomer(
        string customerId, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, IMessageBus bus, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
    {
        await channel.DeleteCustomerAsync(customerId, cancellationToken);
        await InvalidateAsync(cache, customerId, cancellationToken);
        var user = RequestUser.NameOf(http);
        await observer.RecordCustomerMutationAsync(session, customerId, "delete", user, null, cancellationToken);
        await observer.ObserveCustomerRemovedAsync(session, customerId, "Deleted through the app", ObservationSource.Mutation, cancellationToken);
        await bus.PublishAsync(new CustomerReadModelUpdated(customerId, null, "delete"));
        await session.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    // Import a pre-existing Cloud Identity customer (pre-transfer). Synchronous — returns the Customer
    // directly — so it's a plain Created, not routed through the Operations (LRO) UX.
    [WolverinePost("/api/customers/import")]
    public static async Task<IResult> ImportCustomer(
        ImportCustomerRequest request, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, IMessageBus bus, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
    {
        var imported = await channel.ImportCustomerAsync(request, cancellationToken);
        await InvalidateAsync(cache, imported.Id, cancellationToken);
        await RecordMutationAsync(session, bus, observer, imported, "import", RequestUser.NameOf(http), cancellationToken);
        return Results.Created($"/api/customers/{imported.Id}", imported);
    }

    // Provision a new Cloud Identity — a long-running operation. 202 Accepted with the operation so the UI
    // can track it on the Operations page; the worker's saga records the outcome on the customer stream.
    [WolverinePost("/api/customers/{customerId}/provision-cloud-identity")]
    public static async Task<IResult> ProvisionCloudIdentity(
        string customerId, ProvisionCloudIdentityRequest request, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, IMessageBus bus, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
    {
        var operation = await channel.ProvisionCloudIdentityAsync(customerId, request, cancellationToken);
        await InvalidateAsync(cache, customerId, cancellationToken);
        var user = RequestUser.NameOf(http);
        await observer.RecordCustomerMutationAsync(session, customerId, "provisionCloudIdentity", user, operation.Name, cancellationToken);
        if (!operation.Done)
        {
            await bus.PublishAsync(new OperationSubmitted(operation.Name, customerId, null, "provisionCloudIdentity", user, DateTimeOffset.UtcNow));
        }
        await bus.PublishAsync(new CustomerChanged(customerId, null, "provisionCloudIdentity", user));
        await session.SaveChangesAsync(cancellationToken);
        return Results.Accepted($"/api/operations/{operation.Id}", operation);
    }

    // Purchasable catalog reads are idempotent and safe to cache briefly.
    [WolverineGet("/api/customers/{customerId}/purchasable-skus")]
    public static Task<IResult> ListPurchasableSkus(
        string customerId, string productId, IGoogleChannelClient channel, IDistributedCache cache,
        IOptions<GoogleChannelOptions> options, CancellationToken cancellationToken)
        => GoogleReadCache.CachedAsync(cache, $"customer:{customerId}:purchasable-skus:{productId}", options.Value.CacheSeconds,
            () => channel.ListPurchasableSkusAsync(customerId, productId, cancellationToken), cancellationToken);

    [WolverineGet("/api/customers/{customerId}/purchasable-offers")]
    public static Task<IResult> ListPurchasableOffers(
        string customerId, string productId, string skuId, IGoogleChannelClient channel, IDistributedCache cache,
        IOptions<GoogleChannelOptions> options, CancellationToken cancellationToken)
        => GoogleReadCache.CachedAsync(cache, $"customer:{customerId}:purchasable-offers:{productId}:{skuId}", options.Value.CacheSeconds,
            () => channel.ListPurchasableOffersAsync(customerId, productId, skuId, cancellationToken), cancellationToken);

    // Which billing accounts a customer may use for the given SKUs (GCP / n-tier billing). Cached briefly
    // keyed on the sorted SKU set.
    [WolverineGet("/api/customers/{customerId}/eligible-billing-accounts")]
    public static Task<IResult> QueryEligibleBillingAccounts(
        string customerId, string[] skus, IGoogleChannelClient channel, IDistributedCache cache,
        IOptions<GoogleChannelOptions> options, CancellationToken cancellationToken)
        => GoogleReadCache.CachedAsync(cache,
            $"customer:{customerId}:eligible-billing-accounts:{string.Join(',', skus.OrderBy(s => s, StringComparer.Ordinal))}",
            options.Value.CacheSeconds,
            () => channel.QueryEligibleBillingAccountsAsync(customerId, skus, cancellationToken), cancellationToken);

    /// <summary>
    /// Records a create/import/update: observes the customer from the mutation result (direct customer, so no
    /// owning link), appends the mutation to its audit trail and asks the worker to refresh its entitlements.
    /// </summary>
    private static async Task RecordMutationAsync(
        IDocumentSession session, IMessageBus bus, EstateObserver observer, Customer customer, string action, string? user,
        CancellationToken cancellationToken)
    {
        await observer.ObserveCustomerAsync(session, customer, owningLinkId: null, ObservationSource.Mutation, DateTimeOffset.UtcNow, cancellationToken);
        await observer.RecordCustomerMutationAsync(session, customer.Id, action, user, null, cancellationToken);
        await bus.PublishAsync(new CustomerChanged(customer.Id, null, action, user));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Drops the cached customer list and a specific customer after a mutation.</summary>
    private static async Task InvalidateAsync(IDistributedCache cache, string customerId, CancellationToken cancellationToken)
    {
        await cache.RemoveAsync(GoogleReadCache.CustomersListKey, cancellationToken);
        if (!string.IsNullOrEmpty(customerId))
        {
            await cache.RemoveAsync(GoogleReadCache.CustomerKey(customerId), cancellationToken);
        }
    }
}
