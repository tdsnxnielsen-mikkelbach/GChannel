using GChannel.ApiService.Caching;
using GChannel.Core.Configuration;
using GChannel.Core.Estate;
using GChannel.Core.GoogleApis;
using GChannel.Core.Persistence.Documents;
using GChannel.Shared.Contracts;
using Marten;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Wolverine.Http;

namespace GChannel.ApiService.Endpoints;

/// <summary>
/// The derived home-dashboard endpoints. With the read model on, the figures are aggregated from the
/// projected Marten documents (cheap, no Channel API fan-out) on a short Redis cache; otherwise the live
/// aggregation runs on demand (long Redis cache, warmed by the worker's background refresh) with a
/// last-known-good fallback so a quota outage never blanks the page.
/// </summary>
public static class DashboardEndpoints
{
    /// <summary>Redis key the live-path dashboard summary is cached under (shared with the worker's background refresh).</summary>
    public const string CacheKey = "dashboard:summary";
    public const string OverviewCacheKey = "dashboard:overview";

    /// <summary>Redis keys of the short-lived read-model aggregation (distinct from the live-path keys).</summary>
    public const string LiveCacheKey = "dashboard:summary:live";
    public const string LiveOverviewCacheKey = "dashboard:overview:live";

    /// <summary>How long the "last known good" fallback copy is kept — far longer than the live TTL.</summary>
    public static readonly TimeSpan StaleTtl = TimeSpan.FromHours(24);

    public static string StaleKey(string cacheKey) => cacheKey + ":last";

    [WolverineGet("/api/dashboard/summary")]
    public static async Task<IResult> GetSummary(
        IGoogleChannelClient channel, IDistributedCache cache, IOptions<GoogleChannelOptions> options, IQuerySession session,
        CancellationToken cancellationToken)
    {
        try
        {
            if (options.Value.UseReadModel)
            {
                return await CachedAsync(cache, LiveCacheKey, Math.Max(1, options.Value.ReadModelDashboardCacheSeconds),
                    () => DashboardReadModel.BuildSummaryAsync(session, cancellationToken), cancellationToken);
            }

            // Live fan-out path; when the live run skipped customers (429s) back-fill the direct KPIs from
            // whatever the read model holds so the headline figures don't under-report.
            return await CachedAsync(cache, CacheKey, options.Value.CacheSeconds,
                () => channel.GetDashboardSummaryAsync(cancellationToken), cancellationToken,
                summary => DashboardReadModel.BackfillFromReadModelAsync(summary, session, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            // Benign: the caller (Blazor circuit) went away mid-aggregation.
            return Results.StatusCode(499);
        }
    }

    [WolverineGet("/api/dashboard/overview")]
    public static async Task<IResult> GetOverview(
        IGoogleChannelClient channel, IDistributedCache cache, IOptions<GoogleChannelOptions> options, IQuerySession session,
        CancellationToken cancellationToken)
    {
        try
        {
            if (options.Value.UseReadModel)
            {
                return await CachedAsync(cache, LiveOverviewCacheKey, Math.Max(1, options.Value.ReadModelDashboardCacheSeconds),
                    () => DashboardReadModel.BuildOverviewAsync(session, cancellationToken), cancellationToken);
            }

            return await CachedAsync(cache, OverviewCacheKey, options.Value.CacheSeconds,
                () => channel.GetDashboardOverviewAsync(cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(499);
        }
    }

    [WolverineGet("/api/dashboard/status")]
    public static async Task<DashboardRefreshStatus> GetStatus(
        IQuerySession session, IOptions<GoogleChannelOptions> options, CancellationToken cancellationToken)
    {
        var state = await session.LoadAsync<DashboardRefreshState>(DashboardRefreshState.SingletonId, cancellationToken);
        // No status yet (refresher hasn't ticked, or it's disabled): report configuration only.
        return state?.Status ?? new DashboardRefreshStatus { Enabled = options.Value.BackgroundRefreshEnabled };
    }

    /// <summary>
    /// Read-through cache with a separate long-lived "last known good" copy: if a live recompute fails (e.g.
    /// the Channel API per-minute quota is exhausted) the most recent successful result is served instead.
    /// </summary>
    private static async Task<IResult> CachedAsync<T>(
        IDistributedCache cache, string cacheKey, int cacheSeconds, Func<Task<T>> factory, CancellationToken cancellationToken,
        Func<T, Task<T>>? overlay = null) where T : class
    {
        var staleKey = StaleKey(cacheKey);

        var hit = await GoogleReadCache.GetAsync<T>(cache, cacheKey, cancellationToken);
        if (hit is not null)
        {
            if (overlay is not null) { hit = await overlay(hit); }
            return Results.Ok(hit);
        }

        T result;
        try
        {
            result = await factory();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var stale = await GoogleReadCache.GetAsync<T>(cache, staleKey, cancellationToken);
            if (stale is not null)
            {
                if (overlay is not null) { stale = await overlay(stale); }
                return Results.Ok(stale);
            }
            throw;
        }

        await GoogleReadCache.SetAsync(cache, cacheKey, result, cacheSeconds, cancellationToken);
        await cache.SetStringAsync(staleKey, JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = StaleTtl }, cancellationToken);

        if (overlay is not null) { result = await overlay(result); }
        return Results.Ok(result);
    }
}
