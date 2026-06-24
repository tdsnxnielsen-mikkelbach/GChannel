using System.Text.Json;
using GChannel.ApiService.Configuration;
using GChannel.ApiService.Endpoints;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GChannel.ApiService.Services;

/// <summary>
/// Periodically recomputes the dashboard summary using a service-account credential and warms the
/// Redis cache, so the user-facing endpoint serves a ready-made result instead of running the slow
/// N+1 aggregation on the request path (where it can exceed the HTTP timeout). Disabled unless a
/// service account and impersonation user are configured (see <see cref="GoogleChannelOptions"/>).
/// </summary>
public sealed class DashboardRefreshService(
    IOptions<GoogleChannelOptions> options,
    IDistributedCache cache,
    IConnectionMultiplexer redis,
    ILoggerFactory loggerFactory,
    ILogger<DashboardRefreshService> logger) : BackgroundService
{
    // Cluster-wide single-flight guard: only the replica that sets this key recomputes for the
    // interval. The key is intentionally never released; its TTL equals one interval so it doubles
    // as a "already refreshed this interval" marker across all replicas.
    private const string RefreshLockKey = "dashboard:refresh:lock";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.BackgroundRefreshEnabled)
        {
            logger.LogInformation(
                "Dashboard background refresh is disabled; the dashboard is computed on demand from the signed-in user's token.");
            return;
        }

        // Build the service-account-backed client once and reuse it across ticks.
        var credentialSource = new ServiceAccountCredentialSource(opts);
        var client = new GoogleChannelClient(credentialSource, options, loggerFactory.CreateLogger<GoogleChannelClient>());

        // Keep the cached value alive comfortably past one interval so it never expires between runs.
        var interval = TimeSpan.FromSeconds(opts.BackgroundRefreshSeconds);
        var ttl = TimeSpan.FromSeconds(opts.BackgroundRefreshSeconds * 2);
        using var timer = new PeriodicTimer(interval);

        logger.LogInformation(
            "Dashboard background refresh enabled; recomputing every {Interval}s as {User}.",
            opts.BackgroundRefreshSeconds, opts.ImpersonateUser);

        do
        {
            try
            {
                // Take a best-effort distributed lock so that when the API scales to multiple
                // replicas only one of them runs the expensive aggregation per interval. The lock
                // is not released on purpose — letting it expire after one interval also stops a
                // second replica from immediately recomputing what was just cached.
                var acquired = await redis.GetDatabase().StringSetAsync(
                    RefreshLockKey, Environment.MachineName, interval, when: When.NotExists);
                if (!acquired)
                {
                    logger.LogDebug("Another replica already refreshed the dashboard this interval; skipping.");
                    continue;
                }

                // Run unbounded (no request-path time budget) so the cached result is complete even
                // for large estates; this path is off the HTTP request and has no attempt timeout.
                var summary = await client.GetDashboardSummaryAsync(stoppingToken, applyTimeBudget: false);
                await cache.SetStringAsync(
                    DashboardEndpoints.CacheKey,
                    JsonSerializer.Serialize(summary),
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
                    stoppingToken);

                logger.LogInformation("Dashboard summary refreshed in background ({Skipped} customer(s) skipped).",
                    summary.SkippedCustomerCount);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let a transient failure kill the worker; log and try again next tick.
                logger.LogWarning(ex, "Background dashboard refresh failed; will retry on the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
