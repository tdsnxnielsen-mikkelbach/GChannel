using GChannel.Core.Estate;
using GChannel.Core.Messaging;
using GChannel.Core.Persistence.Documents;
using Marten;
using System.Diagnostics;
using Wolverine;

namespace GChannel.Worker.Infrastructure;

/// <summary>
/// Shared body of every recurring-job tick handler. A tick is only honoured when its <c>RunId</c> matches
/// the job's persisted <see cref="RecurringJobState"/> (so a duplicate or superseded chain dies quietly),
/// the job body runs with its failures captured into the state, and the next tick is scheduled through
/// Wolverine with a fresh RunId — the state update and the scheduled message commit together via the
/// Marten outbox, so there is always exactly one live chain per job.
/// </summary>
public static class RecurringJobRunner
{
    public static async Task RunAsync(
        IRecurringJobTick tick,
        RecurringJobDefinition job,
        IDocumentSession session,
        OutgoingMessages outgoing,
        ILogger logger,
        Func<CancellationToken, Task<string?>> body,
        CancellationToken ct)
    {
        var state = await session.LoadAsync<RecurringJobState>(job.Name, ct);
        if (state is null || state.RunId != tick.RunId)
        {
            logger.LogInformation("Ignoring stale {Job} tick {RunId} (current chain is {Current}).", job.Name, tick.RunId, state?.RunId);
            return;
        }

        if (!job.IsEnabled())
        {
            logger.LogInformation("{Job} is disabled by configuration; ending its chain (the bootstrap restarts it when re-enabled).", job.Name);
            state.NextRunUtc = null;
            session.Store(state);
            return;
        }

        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        state.IsRunning = true;
        state.LastStartedUtc = started;
        session.Store(state);
        // Commit the "running" marker right away so the status is visible during a long run.
        await session.SaveChangesAsync(ct);

        string? error;
        try
        {
            error = await body(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Job} run failed; the next tick is still scheduled.", job.Name);
            error = Truncate(EstateObserver.Flatten(ex), 1000);
        }

        var completed = DateTimeOffset.UtcNow;
        var nextRunId = Guid.NewGuid();
        var nextRun = job.NextRunAfter(completed);

        state.IsRunning = false;
        state.LastCompletedUtc = completed;
        state.LastDurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
        state.LastError = error;
        state.RunId = nextRunId;
        state.NextRunUtc = nextRun;
        session.Store(state);

        var delay = nextRun - DateTimeOffset.UtcNow;
        outgoing.Delay(job.CreateTick(nextRunId), delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay);
        logger.LogInformation("{Job} run finished in {Seconds:0.#}s (error: {Error}); next run at {Next:u}.", job.Name, stopwatch.Elapsed.TotalSeconds, error ?? "none", nextRun);
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;
}
