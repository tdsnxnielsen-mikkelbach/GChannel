using System.Diagnostics;
using System.Text.Json;
using GChannel.ApiService.Configuration;
using GChannel.ApiService.Data;
using GChannel.ApiService.Endpoints;
using GChannel.ApiService.Services;
using GChannel.Shared.Contracts;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GChannel.Worker.Services;

/// <summary>
/// Periodically recomputes the dashboard summary using a service-account credential and warms the
/// Redis cache, so the user-facing endpoint serves a ready-made result instead of running the slow
/// N+1 aggregation on the request path (where it can exceed the HTTP timeout). Disabled unless a
/// service account and impersonation user are configured (see <see cref="GoogleChannelOptions"/>).
/// </summary>
public sealed class DashboardRefreshService(
    IOptions<GoogleChannelOptions> options,
    IServiceScopeFactory scopeFactory,
    IDistributedCache cache,
    IConnectionMultiplexer redis,
    ILoggerFactory loggerFactory,
    ILogger<DashboardRefreshService> logger) : BackgroundService
{
    // Cluster-wide single-flight guard: only the replica that sets this key recomputes for the
    // interval. The key is intentionally never released; its TTL equals one interval so it doubles
    // as a "already refreshed this interval" marker across all replicas.
    private const string RefreshLockKey = "dashboard:refresh:lock";

    // How many customers to aggregate between partial-snapshot publishes during a background run.
    private const int PartialPublishEvery = 10;

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
        var db = redis.GetDatabase();

        logger.LogInformation(
            "Dashboard background refresh enabled; recomputing every {Interval}s as {User}.",
            opts.BackgroundRefreshSeconds, opts.ImpersonateUser);

        // Carry the previous run's outcome forward so each status write keeps a meaningful
        // "last completed" even while the next run is in progress.
        DateTimeOffset? lastCompletedUtc = null;
        int? lastDurationSeconds = null;
        int? lastSkippedCount = null;
        DateTimeOffset? nextRefreshUtc = null;

        do
        {
            var acquired = false;
            var startedAt = Stopwatch.GetTimestamp();
            var startedUtc = DateTimeOffset.UtcNow;
            try
            {
                // Take a best-effort distributed lock so that when the API scales to multiple
                // replicas only one of them runs the expensive aggregation per interval. The lock
                // is not released on purpose — letting it expire after one interval also stops a
                // second replica from immediately recomputing what was just cached.
                acquired = await db.StringSetAsync(
                    RefreshLockKey, Environment.MachineName, interval, when: When.NotExists);
                if (!acquired)
                {
                    logger.LogDebug("Another replica already refreshed the dashboard this interval; skipping.");
                    continue;
                }

                // Mark the run as in progress so the home page can show a "Refreshing…" indicator.
                // The soonest a subsequent run could begin is one interval after this one started.
                nextRefreshUtc = startedUtc + interval;
                await WriteStatusAsync(new DashboardRefreshStatus
                {
                    Enabled = true,
                    IsRunning = true,
                    LastStartedUtc = startedUtc,
                    LastCompletedUtc = lastCompletedUtc,
                    LastDurationSeconds = lastDurationSeconds,
                    LastSkippedCount = lastSkippedCount,
                    NextRefreshUtc = nextRefreshUtc,
                }, stoppingToken);

                // Run unbounded (no request-path time budget) so the cached result is complete even
                // for large estates; this path is off the HTTP request and has no attempt timeout.
                // When the §10 read-model is enabled the figures come entirely from SQL (no live
                // Channel API fan-out at all — the read-model sync is the sole quota consumer);
                // otherwise we run the live aggregation and publish partial snapshots as it fills in.
                DashboardSummary summary;
                DashboardOverview overview;
                if (opts.UseReadModel)
                {
                    using var scope = scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<GChannelDbContext>();
                    summary = await DashboardEndpoints.BuildReadModelSummaryAsync(dbContext, stoppingToken);
                    overview = await DashboardEndpoints.BuildReadModelOverviewAsync(dbContext, stoppingToken);
                }
                else
                {
                    // Publish a partial snapshot to the live cache every few customers so the polling
                    // UI can watch the figures fill in during a long recompute (no extra API cost).
                    summary = await client.GetDashboardSummaryAsync(
                        stoppingToken,
                        applyTimeBudget: false,
                        onPartial: partial => PublishPartialAsync(partial, ttl, stoppingToken),
                        partialEvery: PartialPublishEvery);

                    // Recompute the full overview so the cached copy the UI reads at phase 1 carries
                    // every headline + breakdown figure. It re-lists customers and links — cheap and
                    // quota-light — and runs off the request path, so there's no budget pressure.
                    overview = await client.GetDashboardOverviewAsync(stoppingToken);
                }

                // Write the cache the user-facing endpoint reads, plus the long-lived "last known
                // good" fallback. Under the §10 read-model the endpoint serves from short-lived "live"
                // keys (it recomputes cheaply from SQL so the dashboard always reflects the full
                // persisted estate), so warm THOSE with the same short TTL — keeping the background
                // compute useful (a pre-warm + stale fallback) instead of writing keys nothing reads.
                // The live/fan-out path (read-model off) warms the long-lived keys as before.
                if (opts.UseReadModel)
                {
                    var liveTtl = TimeSpan.FromSeconds(Math.Max(1, opts.ReadModelDashboardCacheSeconds));
                    await WarmAsync(DashboardEndpoints.LiveCacheKey, summary, liveTtl, stoppingToken);
                    await WarmAsync(DashboardEndpoints.LiveOverviewCacheKey, overview, liveTtl, stoppingToken);
                }
                else
                {
                    await WarmAsync(DashboardEndpoints.CacheKey, summary, ttl, stoppingToken);
                    await WarmAsync(DashboardEndpoints.OverviewCacheKey, overview, ttl, stoppingToken);
                }

                // Record completion so the home page can show "Updated X ago".
                lastCompletedUtc = DateTimeOffset.UtcNow;
                var duration = Stopwatch.GetElapsedTime(startedAt);
                lastDurationSeconds = (int)Math.Round(duration.TotalSeconds);
                lastSkippedCount = summary.SkippedCustomerCount;
                // Estimate when the next run begins: normally one interval after this run STARTED, but
                // if the run outran the interval the cooldown lock pushes the next start to one
                // interval after COMPLETION (matching the re-arm logic in the finally block below).
                nextRefreshUtc = (duration >= interval ? lastCompletedUtc.Value : startedUtc) + interval;
                await WriteStatusAsync(new DashboardRefreshStatus
                {
                    Enabled = true,
                    IsRunning = false,
                    LastStartedUtc = startedUtc,
                    LastCompletedUtc = lastCompletedUtc,
                    LastDurationSeconds = lastDurationSeconds,
                    LastSkippedCount = lastSkippedCount,
                    NextRefreshUtc = nextRefreshUtc,
                }, stoppingToken);

                logger.LogInformation("Dashboard summary refreshed in background ({Skipped} customer(s) skipped).",
                    summary.SkippedCustomerCount);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let a transient failure kill the worker; log and try again next tick. Clear
                // the in-progress flag so the UI doesn't show "Refreshing…" forever.
                logger.LogWarning(ex, "Background dashboard refresh failed; will retry on the next interval.");
                if (acquired)
                {
                    await TryWriteStatusAsync(new DashboardRefreshStatus
                    {
                        Enabled = true,
                        IsRunning = false,
                        LastStartedUtc = startedUtc,
                        LastCompletedUtc = lastCompletedUtc,
                        LastDurationSeconds = lastDurationSeconds,
                        LastSkippedCount = lastSkippedCount,
                        NextRefreshUtc = nextRefreshUtc,
                    }, stoppingToken);
                }
            }
            finally
            {
                // If an unbounded refresh outran its interval the lock set at the start has already
                // expired, so the next timer tick would re-run it immediately and saturate the
                // Channel API (starving interactive calls). Re-arm the lock from completion to force
                // at least one interval of cooldown; fast runs keep their original per-interval lock.
                if (acquired && !stoppingToken.IsCancellationRequested
                    && Stopwatch.GetElapsedTime(startedAt) >= interval)
                {
                    try
                    {
                        await db.StringSetAsync(RefreshLockKey, Environment.MachineName, interval);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Could not re-arm the dashboard refresh cooldown lock.");
                    }
                }
            }
        }
        while (await WaitForNextDueAsync(db, interval, stoppingToken));
    }

    /// <summary>
    /// Sleeps until the next refresh is actually due, then returns <c>true</c> (or <c>false</c> on
    /// shutdown). The wait is the <em>remaining TTL of the shared Redis lock</em> rather than a fixed
    /// interval measured from this process — so the cadence stays aligned to the cluster-wide schedule
    /// across worker restarts and redeploys. A worker that starts (or skips because a pre-redeploy run
    /// still holds the lock) waits only until the lock expires and then refreshes, instead of resetting
    /// the clock and delaying the next refresh by up to a full interval. Falls back to a full interval
    /// when the lock is absent (e.g. the very first cycle).
    /// </summary>
    private static async Task<bool> WaitForNextDueAsync(IDatabase db, TimeSpan interval, CancellationToken stoppingToken)
    {
        TimeSpan wait;
        try
        {
            wait = await db.KeyTimeToLiveAsync(RefreshLockKey) ?? interval;
        }
        catch
        {
            wait = interval;
        }

        // Clamp: never busy-loop on a near-expired lock, never overshoot one interval.
        if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
        else if (wait > interval) wait = interval;

        try
        {
            await Task.Delay(wait, stoppingToken);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Caches <paramref name="value"/> under both the live key (short TTL) and its long-lived
    /// "last known good" fallback key, keeping the background path in sync with the endpoint's
    /// stale-fallback behaviour.
    /// </summary>
    private async Task WarmAsync<T>(string cacheKey, T value, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(value);

        await cache.SetStringAsync(
            cacheKey,
            json,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
            cancellationToken);

        await cache.SetStringAsync(
            DashboardEndpoints.StaleKey(cacheKey),
            json,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = DashboardEndpoints.StaleTtl },
            cancellationToken);
    }

    /// <summary>
    /// Publishes a mid-run snapshot to the live summary cache only (never the "last known good"
    /// fallback, which must stay equal to the last <em>complete</em> run). Lets the polling UI watch
    /// the figures fill in while the quota-paced aggregation is still in flight.
    /// </summary>
    private async Task PublishPartialAsync(DashboardSummary partial, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(partial);
        await cache.SetStringAsync(
            DashboardEndpoints.CacheKey,
            json,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
            cancellationToken);
    }

    /// <summary>Writes the refresher's run status to the cache (kept well past one interval).</summary>
    private Task WriteStatusAsync(DashboardRefreshStatus status, CancellationToken cancellationToken) =>
        cache.SetStringAsync(
            DashboardEndpoints.StatusKey,
            JsonSerializer.Serialize(status),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = DashboardEndpoints.StaleTtl },
            cancellationToken);

    /// <summary>Best-effort status write used on the failure path so a hiccup can't surface an error.</summary>
    private async Task TryWriteStatusAsync(DashboardRefreshStatus status, CancellationToken cancellationToken)
    {
        try
        {
            await WriteStatusAsync(status, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not write the dashboard refresh status.");
        }
    }
}
