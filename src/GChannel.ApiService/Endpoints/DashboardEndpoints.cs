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

    /// <summary>Redis key the background refresher's run status (last run + in-progress flag) is stored under.</summary>
    public const string StatusKey = "dashboard:refresh:status";

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
                GChannelDbContext db,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    return await CachedAsync(cache, CacheKey, options.Value.CacheSeconds,
                        () => channel.GetDashboardSummaryAsync(cancellationToken), cancellationToken,
                        options.Value.UseReadModel ? s => OverlayReadModelAsync(s, db, cancellationToken) : null);
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

        group.MapGet("/status", async (
                IDistributedCache cache,
                IOptions<GoogleChannelOptions> options,
                CancellationToken cancellationToken) =>
            {
                var stored = await cache.GetStringAsync(StatusKey, cancellationToken);
                var status = stored is not null
                    ? JsonSerializer.Deserialize<DashboardRefreshStatus>(stored)
                    : null;

                // No status yet (refresher hasn't ticked, or it's disabled): report configuration only.
                return Results.Ok(status ?? new DashboardRefreshStatus
                {
                    Enabled = options.Value.BackgroundRefreshEnabled
                });
            })
            .WithName("GetDashboardStatus")
            .WithSummary("Freshness/health of the background dashboard refresher (last run + in-progress flag).");

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
        CancellationToken cancellationToken,
        Func<T, Task<T>>? overlay = null)
    {
        var staleKey = StaleKey(cacheKey);

        var cached = await cache.GetStringAsync(cacheKey, cancellationToken);
        if (cached is not null)
        {
            var hit = JsonSerializer.Deserialize<T>(cached);
            if (hit is not null && overlay is not null) { hit = await overlay(hit); }
            return Results.Ok(hit);
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
                var staleHit = JsonSerializer.Deserialize<T>(stale);
                if (staleHit is not null && overlay is not null) { staleHit = await overlay(staleHit); }
                return Results.Ok(staleHit);
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

        if (overlay is not null) { result = await overlay(result); }
        return Results.Ok(result);
    }

    /// <summary>
    /// §10 read path: replaces the indirect estate (count + top resellers ranked by active seats) on a
    /// summary with values aggregated from the SQL read-model, so the dashboard reflects the durably
    /// synced estate instead of waiting for the live per-reseller fan-out. No-op if no rows synced yet.
    /// </summary>
    private static async Task<DashboardSummary> OverlayReadModelAsync(
        DashboardSummary summary, GChannelDbContext db, CancellationToken cancellationToken)
    {
        var indirectCount = await db.CustomerRecords
            .CountAsync(c => !c.IsDeleted && c.OwningLinkId != null, cancellationToken);
        if (indirectCount == 0)
        {
            return summary; // Nothing synced yet — keep the live values.
        }

        var resellers = await db.CustomerRecords
            .Where(c => !c.IsDeleted && c.OwningLinkId != null)
            .GroupBy(c => c.OwningLinkId!)
            .Select(g => new { LinkId = g.Key, Customers = g.Count(), Seats = g.Sum(c => c.SeatCount) })
            .Join(db.ResellerLinks, g => g.LinkId, l => l.LinkId,
                (g, l) => new { l.PrimaryDomain, l.ResellerCloudId, g.LinkId, g.Customers, g.Seats })
            .OrderByDescending(x => x.Seats)
            .ThenByDescending(x => x.Customers)
            .Take(15)
            .ToListAsync(cancellationToken);

        return summary with
        {
            IndirectCustomerCount = indirectCount,
            TopIndirectResellers = resellers
                .Select(r => new DashboardResellerCustomers
                {
                    Reseller = r.PrimaryDomain ?? r.ResellerCloudId ?? r.LinkId,
                    CustomerCount = r.Customers,
                    SeatCount = r.Seats
                })
                .ToList()
        };
    }
}
