using System.Text.Json;
using GChannel.ApiService.Configuration;
using GChannel.ApiService.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace GChannel.ApiService.Endpoints;

/// <summary>
/// Maps the derived home-dashboard endpoint. There is no single Channel API reporting endpoint
/// (the legacy <c>accounts.reports.*</c> API is deprecated in v1), so the figures are aggregated
/// from the customer and entitlement read paths and cached in Redis like the other reads.
/// </summary>
public static class DashboardEndpoints
{
    /// <summary>Redis key the dashboard summary is cached under (shared with the background refresher).</summary>
    public const string CacheKey = "dashboard:summary";

    /// <summary>Redis key the cheap dashboard overview (count + onboarding) is cached under.</summary>
    public const string OverviewCacheKey = "dashboard:overview";

    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/dashboard").WithTags("Dashboard");

        group.MapGet("/summary", async (
                IGoogleChannelClient channel,
                IDistributedCache cache,
                IOptions<GoogleChannelOptions> options,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    return await CachedAsync(cache, CacheKey, options.Value.CacheSeconds,
                        () => channel.GetDashboardSummaryAsync(cancellationToken), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Benign: the caller (Blazor circuit) went away mid-aggregation. Caught here in
                    // user code so the debugger doesn't flag it as user-unhandled; nothing to return.
                    return Results.StatusCode(499);
                }
            })
            .WithName("GetDashboardSummary")
            .WithSummary("Aggregated reseller figures derived from customers and entitlements.");

        group.MapGet("/overview", async (
                IGoogleChannelClient channel,
                IDistributedCache cache,
                IOptions<GoogleChannelOptions> options,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    return await CachedAsync(cache, OverviewCacheKey, options.Value.CacheSeconds,
                        () => channel.GetDashboardOverviewAsync(cancellationToken), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return Results.StatusCode(499);
                }
            })
            .WithName("GetDashboardOverview")
            .WithSummary("Cheap first-phase dashboard figures (customer count + onboarded-over-time).");

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
