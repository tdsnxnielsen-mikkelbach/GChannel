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

    /// <summary>
    /// How long the "last known good" fallback copy is kept. Far longer than the live TTL so a
    /// sustained quota outage still has a result to serve.
    /// </summary>
    public static readonly TimeSpan StaleTtl = TimeSpan.FromHours(24);

    /// <summary>Derives the "last known good" key for a given live cache key.</summary>
    public static string StaleKey(string cacheKey) => cacheKey + ":last";

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

    /// <summary>
    /// Returns a cached JSON payload when present, otherwise invokes <paramref name="factory"/> and
    /// caches it. A separate long-lived "last known good" copy is also kept: if a live recompute fails
    /// (e.g. the Channel API per-minute quota is exhausted), the most recent successful result is served
    /// instead of failing the whole dashboard.
    /// </summary>
    private static async Task<IResult> CachedAsync<T>(
        IDistributedCache cache,
        string cacheKey,
        int cacheSeconds,
        Func<Task<T>> factory,
        CancellationToken cancellationToken)
    {
        var staleKey = StaleKey(cacheKey);

        var cached = await cache.GetStringAsync(cacheKey, cancellationToken);
        if (cached is not null)
        {
            return Results.Ok(JsonSerializer.Deserialize<T>(cached));
        }

        T result;
        try
        {
            result = await factory();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Live recompute failed (commonly a 429 quota error). Serve the last known good result if
            // we have one so a transient quota blip doesn't break the page; otherwise surface the error.
            var stale = await cache.GetStringAsync(staleKey, cancellationToken);
            if (stale is not null)
            {
                return Results.Ok(JsonSerializer.Deserialize<T>(stale));
            }

            throw;
        }

        var json = JsonSerializer.Serialize(result);
        await cache.SetStringAsync(
            cacheKey,
            json,
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(cacheSeconds)
            },
            cancellationToken);

        // Refresh the long-lived fallback copy (survives well past the live TTL).
        await cache.SetStringAsync(
            staleKey,
            json,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = StaleTtl },
            cancellationToken);

        return Results.Ok(result);
    }
}
