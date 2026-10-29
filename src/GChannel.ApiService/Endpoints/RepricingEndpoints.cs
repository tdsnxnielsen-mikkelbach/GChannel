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
/// Repricing / rebilling-margin endpoints. Customer repricing configs hang off a customer, channel
/// partner repricing configs off a channel partner link. Both return the config resource directly (not
/// long-running operations). Lists are cached briefly and invalidated on every mutation; a mutation also
/// triggers a re-projection so the denormalised repricing percent on the read model follows.
/// </summary>
public static class RepricingEndpoints
{
    // ---- Customer repricing ------------------------------------------------------------------------

    [WolverineGet("/api/customers/{customerId}/repricing-configs")]
    public static Task<IResult> ListCustomerRepricingConfigs(
        string customerId, IGoogleChannelClient channel, IDistributedCache cache, IOptions<GoogleChannelOptions> options, CancellationToken cancellationToken)
        => GoogleReadCache.CachedAsync(cache, GoogleReadCache.CustomerRepricingKey(customerId), options.Value.CacheSeconds,
            () => channel.ListCustomerRepricingConfigsAsync(customerId, cancellationToken), cancellationToken);

    [WolverinePost("/api/customers/{customerId}/repricing-configs")]
    public static async Task<IResult> CreateCustomerRepricingConfig(
        string customerId, SaveRepricingConfigRequest request, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, IMessageBus bus, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
    {
        var created = await channel.CreateCustomerRepricingConfigAsync(customerId, request, cancellationToken);
        await cache.RemoveAsync(GoogleReadCache.CustomerRepricingKey(customerId), cancellationToken);
        await RecordCustomerAsync(session, bus, observer, customerId, "createRepricingConfig", RequestUser.NameOf(http), cancellationToken);
        return Results.Created(ApiRoutes.CustomerRepricingConfig(customerId, created.Id), created);
    }

    [WolverinePut("/api/customers/{customerId}/repricing-configs/{configId}")]
    public static async Task<IResult> UpdateCustomerRepricingConfig(
        string customerId, string configId, SaveRepricingConfigRequest request, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, IMessageBus bus, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
    {
        var updated = await channel.UpdateCustomerRepricingConfigAsync(customerId, configId, request, cancellationToken);
        await cache.RemoveAsync(GoogleReadCache.CustomerRepricingKey(customerId), cancellationToken);
        await RecordCustomerAsync(session, bus, observer, customerId, "updateRepricingConfig", RequestUser.NameOf(http), cancellationToken);
        return Results.Ok(updated);
    }

    [WolverineDelete("/api/customers/{customerId}/repricing-configs/{configId}")]
    public static async Task<IResult> DeleteCustomerRepricingConfig(
        string customerId, string configId, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, IMessageBus bus, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
    {
        await channel.DeleteCustomerRepricingConfigAsync(customerId, configId, cancellationToken);
        await cache.RemoveAsync(GoogleReadCache.CustomerRepricingKey(customerId), cancellationToken);
        await RecordCustomerAsync(session, bus, observer, customerId, "deleteRepricingConfig", RequestUser.NameOf(http), cancellationToken);
        return Results.NoContent();
    }

    // ---- Channel partner repricing -----------------------------------------------------------------

    [WolverineGet("/api/channel-partner-links/{linkId}/repricing-configs")]
    public static Task<IResult> ListChannelPartnerRepricingConfigs(
        string linkId, IGoogleChannelClient channel, IDistributedCache cache, IOptions<GoogleChannelOptions> options, CancellationToken cancellationToken)
        => GoogleReadCache.CachedAsync(cache, GoogleReadCache.ChannelPartnerRepricingKey(linkId), options.Value.CacheSeconds,
            () => channel.ListChannelPartnerRepricingConfigsAsync(linkId, cancellationToken), cancellationToken);

    [WolverinePost("/api/channel-partner-links/{linkId}/repricing-configs")]
    public static async Task<IResult> CreateChannelPartnerRepricingConfig(
        string linkId, SaveRepricingConfigRequest request, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
    {
        var created = await channel.CreateChannelPartnerRepricingConfigAsync(linkId, request, cancellationToken);
        await cache.RemoveAsync(GoogleReadCache.ChannelPartnerRepricingKey(linkId), cancellationToken);
        await RecordLinkAsync(session, observer, linkId, "createRepricingConfig", RequestUser.NameOf(http), cancellationToken);
        return Results.Created(ApiRoutes.ChannelPartnerRepricingConfig(linkId, created.Id), created);
    }

    [WolverinePut("/api/channel-partner-links/{linkId}/repricing-configs/{configId}")]
    public static async Task<IResult> UpdateChannelPartnerRepricingConfig(
        string linkId, string configId, SaveRepricingConfigRequest request, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
    {
        var updated = await channel.UpdateChannelPartnerRepricingConfigAsync(linkId, configId, request, cancellationToken);
        await cache.RemoveAsync(GoogleReadCache.ChannelPartnerRepricingKey(linkId), cancellationToken);
        await RecordLinkAsync(session, observer, linkId, "updateRepricingConfig", RequestUser.NameOf(http), cancellationToken);
        return Results.Ok(updated);
    }

    [WolverineDelete("/api/channel-partner-links/{linkId}/repricing-configs/{configId}")]
    public static async Task<IResult> DeleteChannelPartnerRepricingConfig(
        string linkId, string configId, IGoogleChannelClient channel, IDistributedCache cache,
        IDocumentSession session, [FromServices] EstateObserver observer, HttpContext http, CancellationToken cancellationToken)
    {
        await channel.DeleteChannelPartnerRepricingConfigAsync(linkId, configId, cancellationToken);
        await cache.RemoveAsync(GoogleReadCache.ChannelPartnerRepricingKey(linkId), cancellationToken);
        await RecordLinkAsync(session, observer, linkId, "deleteRepricingConfig", RequestUser.NameOf(http), cancellationToken);
        return Results.NoContent();
    }

    private static async Task RecordCustomerAsync(
        IDocumentSession session, IMessageBus bus, EstateObserver observer, string customerId, string action, string? user, CancellationToken cancellationToken)
    {
        await observer.RecordCustomerMutationAsync(session, customerId, action, user, null, cancellationToken);
        // Re-project so the denormalised RepricingPercent on the customer's entitlements follows the new config.
        await bus.PublishAsync(new CustomerChanged(customerId, null, action, user));
        await session.SaveChangesAsync(cancellationToken);
    }

    private static async Task RecordLinkAsync(
        IDocumentSession session, EstateObserver observer, string linkId, string action, string? user, CancellationToken cancellationToken)
    {
        await observer.RecordLinkMutationAsync(session, linkId, action, user, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }
}
