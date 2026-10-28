using GChannel.Core.Configuration;
using GChannel.Core.Messaging;
using GChannel.Core.Persistence.Documents;
using GChannel.Shared.Contracts;
using GChannel.Worker.Infrastructure;
using Marten;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text.Json;
using Wolverine;

namespace GChannel.Worker.Handlers;

/// <summary>
/// Live-path dashboard refresh (read model OFF): recomputes the dashboard summary + overview with the service
/// account, unbounded by the request-path time budget, publishing partial snapshots as the paced aggregation
/// fills in, and warms the Redis keys the API serves (plus their long-lived "last known good" copies). With
/// the read model on, the dashboard is derived from Marten instead and this job is disabled.
/// </summary>
public sealed class DashboardRefreshHandler(
    IOptions<GoogleChannelOptions> options,
    ServiceAccountChannelClientProvider clients,
    IDistributedCache cache,
    IDocumentStore store,
    RecurringJobCatalog catalog,
    ILogger<DashboardRefreshHandler> logger)
{
    public const string CacheKey = "dashboard:summary";
    public const string OverviewCacheKey = "dashboard:overview";
    public static readonly TimeSpan StaleTtl = TimeSpan.FromHours(24);
    private const int PartialPublishEvery = 10;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<OutgoingMessages> Handle(RefreshDashboardSnapshot tick, IDocumentSession session, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        await RecurringJobRunner.RunAsync(tick, catalog[RecurringJobs.DashboardSnapshot], session, outgoing, logger, RunAsync, ct);
        return outgoing;
    }

    private async Task<string?> RunAsync(CancellationToken ct)
    {
        var client = clients.Client;
        if (client is null)
        {
            return "No service-account credential configured.";
        }

        var opts = options.Value;
        var interval = TimeSpan.FromSeconds(Math.Max(30, opts.BackgroundRefreshSeconds));
        var ttl = interval * 2;
        var startedAt = Stopwatch.GetTimestamp();
        var startedUtc = DateTimeOffset.UtcNow;

        await WriteStatusAsync(s => s with { Enabled = true, IsRunning = true, LastStartedUtc = startedUtc, NextRefreshUtc = startedUtc + interval }, ct);

        try
        {
            var summary = await client.GetDashboardSummaryAsync(
                ct,
                applyTimeBudget: false,
                onPartial: partial => PublishPartialAsync(partial, ttl, ct),
                partialEvery: PartialPublishEvery);
            var overview = await client.GetDashboardOverviewAsync(ct);

            await WarmAsync(CacheKey, summary, ttl, ct);
            await WarmAsync(OverviewCacheKey, overview, ttl, ct);

            var completed = DateTimeOffset.UtcNow;
            var duration = Stopwatch.GetElapsedTime(startedAt);
            await WriteStatusAsync(s => s with
            {
                Enabled = true,
                IsRunning = false,
                LastStartedUtc = startedUtc,
                LastCompletedUtc = completed,
                LastDurationSeconds = (int)Math.Round(duration.TotalSeconds),
                LastSkippedCount = summary.SkippedCustomerCount,
                NextRefreshUtc = completed + interval,
            }, ct);

            logger.LogInformation("Dashboard summary refreshed in {Seconds}s ({Customers} customers, {Skipped} skipped).",
                (int)duration.TotalSeconds, summary.CustomerCount, summary.SkippedCustomerCount);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await WriteStatusAsync(s => s with { IsRunning = false, NextRefreshUtc = DateTimeOffset.UtcNow + interval }, ct);
            throw;
        }
    }

    private async Task WarmAsync<T>(string cacheKey, T value, TimeSpan ttl, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(value, Json);
        await cache.SetStringAsync(cacheKey, json, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }, ct);
        await cache.SetStringAsync(cacheKey + ":last", json, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = StaleTtl }, ct);
    }

    // Mid-run snapshot to the live key only (never the last-known-good copy, which must stay equal to the last complete run).
    private Task PublishPartialAsync(DashboardSummary partial, TimeSpan ttl, CancellationToken ct)
        => cache.SetStringAsync(CacheKey, JsonSerializer.Serialize(partial, Json), new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl }, ct);

    private async Task WriteStatusAsync(Func<DashboardRefreshStatus, DashboardRefreshStatus> update, CancellationToken ct)
    {
        try
        {
            await using var s = store.LightweightSession();
            var state = await s.LoadAsync<DashboardRefreshState>(DashboardRefreshState.SingletonId, ct) ?? new DashboardRefreshState();
            state.Status = update(state.Status);
            s.Store(state);
            await s.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not write the dashboard refresh status.");
        }
    }
}
