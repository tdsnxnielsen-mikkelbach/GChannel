using GChannel.ApiService.Caching;
using GChannel.ApiService.Http;
using GChannel.Core.Configuration;
using GChannel.Core.Estate;
using GChannel.Core.Estate.ReadModels;
using GChannel.Core.GoogleApis;
using GChannel.Core.Messaging;
using GChannel.Shared.Contracts;
using Marten;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using System.Globalization;
using Wolverine;
using Wolverine.Http;

namespace GChannel.ApiService.Endpoints;

/// <summary>
/// Entitlement-lifecycle endpoints (the core selling flow): read paths (list / get / change history /
/// offer lookup) plus the mutating purchase, modify and state-change calls. Entitlements are nested under
/// a customer, mirroring the Cloud Channel resource hierarchy. Each mutation is recorded on the
/// entitlement's event stream and its long-running operation handed to the worker's saga.
/// </summary>
public static class EntitlementsEndpoints
{
    [WolverineGet("/api/customers/{customerId}/entitlements")]
    public static async Task<IResult> ListEntitlements(
        string customerId, IGoogleChannelClient channel, IDistributedCache cache, IQuerySession session,
        IOptions<GoogleChannelOptions> options, CancellationToken cancellationToken)
    {
        // Read model: when enabled and this customer has been observed, serve the list from the projected
        // documents so the contended ListEntitlements quota stays reserved for the background sync. Falls
        // back to a live (cached) call for customers not yet in the read model (cold start).
        if (options.Value.UseReadModel)
        {
            var customer = await session.LoadAsync<CustomerRecord>(EstateStreams.Customer(customerId), cancellationToken);
            if (customer is { IsDeleted: false })
            {
                var rows = await session.Query<EntitlementRecord>()
                    .Where(r => r.CustomerId == customerId && !r.IsDeleted)
                    .OrderByDescending(r => r.CreateTime)
                    .ToListAsync(cancellationToken);
                return Results.Ok(new EntitlementsResult { Entitlements = rows.Select(MapEntitlement).ToList() });
            }
        }

        return await GoogleReadCache.CachedAsync(cache, GoogleReadCache.EntitlementsListKey(customerId), options.Value.CacheSeconds,
            () => channel.ListEntitlementsAsync(customerId, cancellationToken), cancellationToken);
    }

    [WolverineGet("/api/customers/{customerId}/entitlements/{entitlementId}")]
    public static async Task<IResult> GetEntitlement(
        string customerId, string entitlementId, IGoogleChannelClient channel, IDistributedCache cache, IQuerySession session,
        IOptions<GoogleChannelOptions> options, CancellationToken cancellationToken)
    {
        // The live Channel API get is cached; Ion cost/margin (which it doesn't carry) is merged fresh from
        // the read model each call so it stays current even when the live get is cached.
        var entitlement = await GoogleReadCache.GetOrSetAsync(cache, GoogleReadCache.EntitlementKey(customerId, entitlementId),
            options.Value.CacheSeconds, () => channel.GetEntitlementAsync(customerId, entitlementId, cancellationToken), cancellationToken);
        if (entitlement is null)
        {
            return Results.NotFound();
        }

        var rec = await session.LoadAsync<EntitlementRecord>(EstateStreams.Entitlement(entitlementId), cancellationToken);
        if (rec is not null && rec.CustomerId == customerId)
        {
            entitlement = entitlement with
            {
                IonCostPerSeat = rec.IonCostPerSeat,
                IonMarginPercent = rec.IonMarginPercent,
                IonCurrency = rec.IonCurrency,
            };
        }
        return Results.Ok(entitlement);
    }

    [WolverineGet("/api/customers/{customerId}/entitlements/{entitlementId}/changes")]
    public static Task<IResult> ListEntitlementChanges(
        string customerId, string entitlementId, IGoogleChannelClient channel, IDistributedCache cache,
        IOptions<GoogleChannelOptions> options, CancellationToken cancellationToken)
        => GoogleReadCache.CachedAsync(cache, GoogleReadCache.EntitlementChangesKey(customerId, entitlementId), options.Value.CacheSeconds,
            () => channel.ListEntitlementChangesAsync(customerId, entitlementId, cancellationToken), cancellationToken);

    [WolverineGet("/api/customers/{customerId}/entitlements/{entitlementId}/offer")]
    public static Task<IResult> LookupEntitlementOffer(
        string customerId, string entitlementId, IGoogleChannelClient channel, IDistributedCache cache,
        IOptions<GoogleChannelOptions> options, CancellationToken cancellationToken)
        => GoogleReadCache.CachedAsync(cache, GoogleReadCache.EntitlementOfferKey(customerId, entitlementId), options.Value.CacheSeconds,
            () => channel.LookupEntitlementOfferAsync(customerId, entitlementId, cancellationToken), cancellationToken);

    // Purchase (create). Returns a long-running operation; the new entitlement's id is only known once Google
    // finishes, so the request is recorded on the customer stream and the saga triggers a re-projection.
    [WolverinePost("/api/customers/{customerId}/entitlements")]
    public static async Task<IResult> CreateEntitlement(
        string customerId, PurchaseEntitlementRequest request, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, IMessageBus bus, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
    {
        var operation = await channel.CreateEntitlementAsync(customerId, request, cancellationToken);
        await InvalidateAsync(cache, customerId, null, cancellationToken);
        var user = RequestUser.NameOf(http);
        await observer.RecordCustomerMutationAsync(session, customerId, $"purchase offer {request.OfferId}", user, operation.OperationName, cancellationToken);
        await PublishAsync(bus, customerId, null, "create", operation, user);
        await session.SaveChangesAsync(cancellationToken);
        return Results.Accepted(value: operation);
    }

    [WolverinePost("/api/customers/{customerId}/entitlements/{entitlementId}/change-offer")]
    public static Task<IResult> ChangeEntitlementOffer(
        string customerId, string entitlementId, ChangeOfferRequest request, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, IMessageBus bus, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
        => MutateAsync(cache, session, bus, observer, http, customerId, entitlementId, "changeOffer", $"offer {request.OfferId}",
            () => channel.ChangeEntitlementOfferAsync(customerId, entitlementId, request, cancellationToken), cancellationToken);

    [WolverinePost("/api/customers/{customerId}/entitlements/{entitlementId}/change-parameters")]
    public static Task<IResult> ChangeEntitlementParameters(
        string customerId, string entitlementId, ChangeParametersRequest request, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, IMessageBus bus, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
        => MutateAsync(cache, session, bus, observer, http, customerId, entitlementId, "changeParameters",
            string.Join(", ", request.Parameters.Select(p => $"{p.Name}={p.IntValue?.ToString(CultureInfo.InvariantCulture) ?? p.StringValue}")),
            () => channel.ChangeEntitlementParametersAsync(customerId, entitlementId, request, cancellationToken), cancellationToken);

    [WolverinePost("/api/customers/{customerId}/entitlements/{entitlementId}/change-renewal")]
    public static Task<IResult> ChangeEntitlementRenewal(
        string customerId, string entitlementId, ChangeRenewalSettingsRequest request, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, IMessageBus bus, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
        => MutateAsync(cache, session, bus, observer, http, customerId, entitlementId, "changeRenewalSettings", $"enableRenewal={request.EnableRenewal}",
            () => channel.ChangeEntitlementRenewalAsync(customerId, entitlementId, request, cancellationToken), cancellationToken);

    [WolverinePost("/api/customers/{customerId}/entitlements/{entitlementId}/activate")]
    public static Task<IResult> ActivateEntitlement(
        string customerId, string entitlementId, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, IMessageBus bus, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
        => MutateAsync(cache, session, bus, observer, http, customerId, entitlementId, "activate", null,
            () => channel.ActivateEntitlementAsync(customerId, entitlementId, cancellationToken), cancellationToken);

    [WolverinePost("/api/customers/{customerId}/entitlements/{entitlementId}/suspend")]
    public static Task<IResult> SuspendEntitlement(
        string customerId, string entitlementId, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, IMessageBus bus, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
        => MutateAsync(cache, session, bus, observer, http, customerId, entitlementId, "suspend", null,
            () => channel.SuspendEntitlementAsync(customerId, entitlementId, cancellationToken), cancellationToken);

    [WolverinePost("/api/customers/{customerId}/entitlements/{entitlementId}/cancel")]
    public static Task<IResult> CancelEntitlement(
        string customerId, string entitlementId, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, IMessageBus bus, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
        => MutateAsync(cache, session, bus, observer, http, customerId, entitlementId, "cancel", null,
            () => channel.CancelEntitlementAsync(customerId, entitlementId, cancellationToken), cancellationToken);

    [WolverinePost("/api/customers/{customerId}/entitlements/{entitlementId}/start-paid-service")]
    public static Task<IResult> StartPaidService(
        string customerId, string entitlementId, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, IMessageBus bus, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
        => MutateAsync(cache, session, bus, observer, http, customerId, entitlementId, "startPaidService", null,
            () => channel.StartPaidServiceAsync(customerId, entitlementId, cancellationToken), cancellationToken);

    /// <summary>
    /// Projects a read-model entitlement onto the shared <see cref="Entitlement"/> contract. Display names,
    /// create time and seat count are denormalised at sync time, so the list renders identically to the live
    /// path; the seat total is re-exposed as a <c>num_units</c> parameter so the UI's seat logic is unchanged.
    /// </summary>
    internal static Entitlement MapEntitlement(EntitlementRecord r) => new()
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
        BillingAccount = r.BillingAccount,
        IsTrial = r.IsTrial,
        CreateTime = r.CreateTime,
        PlanDescription = r.PlanDescription,
        Commitment = r.CommitmentEndTime is not null
            ? new EntitlementCommitment { EndTime = r.CommitmentEndTime }
            : null,
        UnitPrice = r.UnitPrice > 0 ? r.UnitPrice : null,
        PriceCurrency = r.Currency,
        RepricingPercent = r.RepricingPercent,
        IonCostPerSeat = r.IonCostPerSeat,
        IonMarginPercent = r.IonMarginPercent,
        IonCurrency = r.IonCurrency,
        BillableSeats = r.BillableSeats,
        Parameters = r.Seats > 0
            ? [new EntitlementParameter { Name = "num_units", Value = r.Seats.ToString(CultureInfo.InvariantCulture), Editable = true }]
            : [],
    };

    /// <summary>Runs a mutating entitlement call, invalidates the affected caches, records the request and returns the operation.</summary>
    private static async Task<IResult> MutateAsync(
        IDistributedCache cache, IDocumentSession session, IMessageBus bus, EstateObserver observer, HttpContext http,
        string customerId, string entitlementId, string action, string? details,
        Func<Task<EntitlementOperation>> mutation, CancellationToken cancellationToken)
    {
        var operation = await mutation();
        await InvalidateAsync(cache, customerId, entitlementId, cancellationToken);
        var user = RequestUser.NameOf(http);
        await observer.RecordEntitlementChangeRequestedAsync(session, customerId, entitlementId, action, user, operation.OperationName, details, cancellationToken);
        await PublishAsync(bus, customerId, entitlementId, action, operation, user);
        await session.SaveChangesAsync(cancellationToken);
        return Results.Accepted(value: operation);
    }

    private static async Task PublishAsync(IMessageBus bus, string customerId, string? entitlementId, string action, EntitlementOperation operation, string? user)
    {
        if (entitlementId is not null)
        {
            await bus.PublishAsync(new EntitlementChanged(customerId, entitlementId, action, user));
        }
        if (!string.IsNullOrEmpty(operation.OperationName) && !operation.Done)
        {
            await bus.PublishAsync(new OperationSubmitted(operation.OperationName, customerId, entitlementId, action, user, DateTimeOffset.UtcNow));
        }
        else
        {
            // Completed inline (or no operation to track): re-project right away so the read model catches up.
            await bus.PublishAsync(new CustomerChanged(customerId, null, action, user));
        }
    }

    /// <summary>Drops the cached entitlement list (and the specific entitlement's get/changes/offer entries) after a mutation.</summary>
    private static async Task InvalidateAsync(IDistributedCache cache, string customerId, string? entitlementId, CancellationToken cancellationToken)
    {
        await cache.RemoveAsync(GoogleReadCache.EntitlementsListKey(customerId), cancellationToken);
        if (!string.IsNullOrEmpty(entitlementId))
        {
            await cache.RemoveAsync(GoogleReadCache.EntitlementKey(customerId, entitlementId), cancellationToken);
            await cache.RemoveAsync(GoogleReadCache.EntitlementChangesKey(customerId, entitlementId), cancellationToken);
            await cache.RemoveAsync(GoogleReadCache.EntitlementOfferKey(customerId, entitlementId), cancellationToken);
        }
    }
}
