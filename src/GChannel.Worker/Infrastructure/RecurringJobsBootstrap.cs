using GChannel.Core.Persistence.Documents;
using Marten;
using Wolverine;
using Wolverine.Runtime;

namespace GChannel.Worker.Infrastructure;

/// <summary>
/// Starts each enabled recurring job's chain when none is pending and acts as a watchdog afterwards: a job
/// whose next run is overdue (its scheduled tick was lost), whose run began in a previous worker process
/// (the process died or was replaced mid-run), or that has been "running" longer than its execution budget
/// gets a fresh chain. A fresh chain supersedes the old one — ticks carrying the old RunId are ignored by the
/// handlers, so a tick the durable inbox replays later is harmless.
/// </summary>
public sealed class RecurringJobsBootstrap(
    RecurringJobCatalog catalog,
    IDocumentStore store,
    IWolverineRuntime runtime,
    ILogger<RecurringJobsBootstrap> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan OverdueGrace = TimeSpan.FromMinutes(3);

    // The worker runs as a single replica, so a run that started before this process did cannot still be
    // executing anywhere — except briefly during a revision rollover, or until the durable inbox replays the
    // interrupted tick on this node. Wait this long before declaring such a run abandoned.
    private static readonly TimeSpan PreviousProcessGrace = TimeSpan.FromMinutes(5);

    private readonly DateTimeOffset _processStartedUtc = DateTimeOffset.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(InitialDelay, stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureChainsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Recurring-job watchdog pass failed; retrying next interval.");
            }

            try { await Task.Delay(WatchdogInterval, stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task EnsureChainsAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var bus = new MessageBus(runtime);

        foreach (var job in catalog.Jobs)
        {
            if (!job.IsEnabled())
            {
                continue;
            }

            await using var session = store.LightweightSession();
            var state = await session.LoadAsync<RecurringJobState>(job.Name, ct);

            string? reason = null;
            if (state is null || state.NextRunUtc is null)
            {
                reason = state is null ? "first start" : "chain ended";
            }
            else if (!state.IsRunning && state.NextRunUtc < now - OverdueGrace)
            {
                reason = $"overdue since {state.NextRunUtc:u}";
            }
            else if (state.IsRunning && state.LastStartedUtc is { } startedAt && startedAt < now - job.StuckAfter)
            {
                reason = $"stuck since {startedAt:u}";
            }
            else if (state.IsRunning && state.LastStartedUtc is { } previous && previous < _processStartedUtc
                     && now - _processStartedUtc >= PreviousProcessGrace)
            {
                reason = $"run started {previous:u} by a previous worker process";
            }

            if (reason is null)
            {
                continue;
            }

            var runId = Guid.NewGuid();
            state ??= new RecurringJobState { Id = job.Name };
            state.RunId = runId;
            state.NextRunUtc = now;
            state.IsRunning = false;
            session.Store(state);
            await session.SaveChangesAsync(ct);

            await bus.PublishAsync(job.CreateTick(runId));
            logger.LogInformation("Started recurring job chain {Job} ({Reason}) with run {RunId}.", job.Name, reason, runId);
        }
    }
}
