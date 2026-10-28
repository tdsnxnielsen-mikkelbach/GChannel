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
using Wolverine;
using Wolverine.Http;

namespace GChannel.ApiService.Endpoints;

/// <summary>
/// Channel partner link endpoints (distributor / n-tier): managing the links (list / get / create / patch)
/// plus the customers owned by a partner. Links live at the account level, so they are rooted off
/// <c>/api/channel-partner-links</c> rather than under a customer.
/// </summary>
public static class ChannelPartnerLinksEndpoints
{
    [WolverineGet("/api/channel-partner-links")]
    public static Task<IResult> ListChannelPartnerLinks(
        IGoogleChannelClient channel, IDistributedCache cache, IOptions<GoogleChannelOptions> options, CancellationToken cancellationToken)
        => GoogleReadCache.CachedAsync(cache, GoogleReadCache.ChannelPartnerLinksListKey, options.Value.CacheSeconds,
            () => channel.ListChannelPartnerLinksAsync(cancellationToken), cancellationToken);

    [WolverineGet("/api/channel-partner-links/{linkId}")]
    public static Task<IResult> GetChannelPartnerLink(
        string linkId, IGoogleChannelClient channel, IDistributedCache cache, IOptions<GoogleChannelOptions> options, CancellationToken cancellationToken)
        => GoogleReadCache.CachedAsync(cache, GoogleReadCache.ChannelPartnerLinkKey(linkId), options.Value.CacheSeconds,
            () => channel.GetChannelPartnerLinkAsync(linkId, cancellationToken), cancellationToken);

    [WolverineGet("/api/channel-partner-links/{linkId}/customers")]
    public static async Task<IResult> ListChannelPartnerCustomers(
        string linkId, IGoogleChannelClient channel, IDistributedCache cache, IQuerySession session,
        IOptions<GoogleChannelOptions> options, CancellationToken cancellationToken)
    {
        // Read model: when enabled and this link has observed customers, serve them from the projected
        // documents so the contended ListCustomers quota stays reserved for the background sync. Falls back
        // to a live (cached) call when the link has no customers yet (cold start, or genuinely zero).
        if (options.Value.UseReadModel)
        {
            var rows = await session.Query<CustomerRecord>()
                .Where(c => c.OwningLinkId == linkId && !c.IsDeleted)
                .OrderBy(c => c.OrgName)
                .ToListAsync(cancellationToken);
            if (rows.Count > 0)
            {
                return Results.Ok(new CustomersResult { Customers = rows.Select(c => MapCustomer(c, linkId)).ToList() });
            }
        }

        return await GoogleReadCache.CachedAsync(cache, GoogleReadCache.ChannelPartnerCustomersKey(linkId), options.Value.CacheSeconds,
            () => channel.ListChannelPartnerCustomersAsync(linkId, cancellationToken), cancellationToken);
    }

    [WolverineGet("/api/channel-partner-links/{linkId}/customers/{customerId}")]
    public static Task<IResult> GetChannelPartnerCustomer(
        string linkId, string customerId, IGoogleChannelClient channel, IDistributedCache cache,
        IOptions<GoogleChannelOptions> options, CancellationToken cancellationToken)
        => GoogleReadCache.CachedAsync(cache, GoogleReadCache.ChannelPartnerCustomerKey(linkId, customerId), options.Value.CacheSeconds,
            () => channel.GetChannelPartnerCustomerAsync(linkId, customerId, cancellationToken), cancellationToken);

    [WolverinePost("/api/channel-partner-links/{linkId}/customers")]
    public static async Task<IResult> CreateChannelPartnerCustomer(
        string linkId, SaveCustomerRequest request, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, IMessageBus bus, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
    {
        var created = await channel.CreateChannelPartnerCustomerAsync(linkId, request, cancellationToken);
        await InvalidateCustomersAsync(cache, linkId, created.Id, cancellationToken);
        await RecordMutationAsync(session, bus, observer, created, linkId, "create", RequestUser.NameOf(http), cancellationToken);
        return Results.Created($"/api/channel-partner-links/{linkId}/customers/{created.Id}", created);
    }

    [WolverinePut("/api/channel-partner-links/{linkId}/customers/{customerId}")]
    public static async Task<IResult> UpdateChannelPartnerCustomer(
        string linkId, string customerId, SaveCustomerRequest request, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, IMessageBus bus, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
    {
        var updated = await channel.UpdateChannelPartnerCustomerAsync(linkId, request with { Id = customerId }, cancellationToken);
        await InvalidateCustomersAsync(cache, linkId, customerId, cancellationToken);
        await RecordMutationAsync(session, bus, observer, updated, linkId, "update", RequestUser.NameOf(http), cancellationToken);
        return Results.Ok(updated);
    }

    [WolverineDelete("/api/channel-partner-links/{linkId}/customers/{customerId}")]
    public static async Task<IResult> DeleteChannelPartnerCustomer(
        string linkId, string customerId, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, IMessageBus bus, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
    {
        await channel.DeleteChannelPartnerCustomerAsync(linkId, customerId, cancellationToken);
        await InvalidateCustomersAsync(cache, linkId, customerId, cancellationToken);
        var user = RequestUser.NameOf(http);
        await observer.RecordCustomerMutationAsync(session, customerId, "delete", user, null, cancellationToken);
        await observer.ObserveCustomerRemovedAsync(session, customerId, "Deleted through the app", ObservationSource.Mutation, cancellationToken);
        await bus.PublishAsync(new CustomerReadModelUpdated(customerId, linkId, "delete"));
        await session.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    [WolverinePost("/api/channel-partner-links/{linkId}/customers/import")]
    public static async Task<IResult> ImportChannelPartnerCustomer(
        string linkId, ImportCustomerRequest request, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, IMessageBus bus, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
    {
        var imported = await channel.ImportChannelPartnerCustomerAsync(linkId, request, cancellationToken);
        await InvalidateCustomersAsync(cache, linkId, imported.Id, cancellationToken);
        await RecordMutationAsync(session, bus, observer, imported, linkId, "import", RequestUser.NameOf(http), cancellationToken);
        return Results.Created($"/api/channel-partner-links/{linkId}/customers/{imported.Id}", imported);
    }

    [WolverinePost("/api/channel-partner-links")]
    public static async Task<IResult> CreateChannelPartnerLink(
        CreateChannelPartnerLinkRequest request, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
    {
        var created = await channel.CreateChannelPartnerLinkAsync(request, cancellationToken);

        // Remember the domain we invited (keyed by reseller Cloud Identity ID) so a later Cloud Identity check
        // on the same domain can correlate this link even before Google populates the partner's primary domain.
        if (!string.IsNullOrWhiteSpace(request.Domain))
        {
            await cache.SetStringAsync(
                GoogleReadCache.InvitedDomainKey(request.ResellerCloudIdentityId),
                request.Domain.Trim(),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(90) },
                cancellationToken);
        }

        await InvalidateAsync(cache, created.Id, cancellationToken);
        await observer.ObserveLinkAsync(session, created, ObservationSource.Mutation, cancellationToken);
        await observer.RecordLinkMutationAsync(session, created.Id, "invite", RequestUser.NameOf(http), cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/channel-partner-links/{created.Id}", created);
    }

    [WolverinePut("/api/channel-partner-links/{linkId}/state")]
    public static async Task<IResult> UpdateChannelPartnerLinkState(
        string linkId, UpdateChannelPartnerLinkRequest request, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
    {
        var updated = await channel.UpdateChannelPartnerLinkStateAsync(linkId, request, cancellationToken);
        await InvalidateAsync(cache, linkId, cancellationToken);
        await observer.ObserveLinkAsync(session, updated, ObservationSource.Mutation, cancellationToken);
        await observer.RecordLinkMutationAsync(session, linkId, $"state {request.LinkState}", RequestUser.NameOf(http), cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return Results.Ok(updated);
    }

    /// <summary>
    /// Projects a read-model customer owned by a partner link onto the shared <see cref="Customer"/> contract.
    /// Full-fidelity fields (contact, address, language) come from the live customer-detail call.
    /// </summary>
    private static Customer MapCustomer(CustomerRecord r, string linkId) => new()
    {
        Name = $"customers/{r.CustomerId}",
        Id = r.CustomerId,
        OrgDisplayName = r.OrgName,
        Domain = r.Domain,
        CloudIdentityId = r.CloudIdentityId,
        ChannelPartnerId = linkId,
        CreateTime = r.CreateTime,
    };

    private static async Task RecordMutationAsync(
        IDocumentSession session, IMessageBus bus, EstateObserver observer, Customer customer, string linkId, string action, string? user,
        CancellationToken cancellationToken)
    {
        await observer.ObserveCustomerAsync(session, customer, linkId, ObservationSource.Mutation, DateTimeOffset.UtcNow, cancellationToken);
        await observer.RecordCustomerMutationAsync(session, customer.Id, action, user, null, cancellationToken);
        await bus.PublishAsync(new CustomerChanged(customer.Id, linkId, action, user));
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Drops the cached partner-customer list and a specific customer after a mutation.</summary>
    private static async Task InvalidateCustomersAsync(IDistributedCache cache, string linkId, string customerId, CancellationToken cancellationToken)
    {
        await cache.RemoveAsync(GoogleReadCache.ChannelPartnerCustomersKey(linkId), cancellationToken);
        if (!string.IsNullOrEmpty(customerId))
        {
            await cache.RemoveAsync(GoogleReadCache.ChannelPartnerCustomerKey(linkId, customerId), cancellationToken);
        }
    }

    /// <summary>Drops the cached link list and a specific link after a mutation.</summary>
    private static async Task InvalidateAsync(IDistributedCache cache, string linkId, CancellationToken cancellationToken)
    {
        await cache.RemoveAsync(GoogleReadCache.ChannelPartnerLinksListKey, cancellationToken);
        if (!string.IsNullOrEmpty(linkId))
        {
            await cache.RemoveAsync(GoogleReadCache.ChannelPartnerLinkKey(linkId), cancellationToken);
        }
    }
}
