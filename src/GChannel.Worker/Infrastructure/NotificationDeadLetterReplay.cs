using GChannel.Core.Messaging;
using JasperFx;
using JasperFx.Core.Reflection;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Runtime;
using Wolverine.Util;

namespace GChannel.Worker.Infrastructure;

/// <summary>
/// Idempotent startup repair (runs shortly after every worker start): replays the Channel notifications that the
/// former read-model snapshot collision (see <see cref="StrayReadModelCleanup"/>) dead-lettered with
/// <see cref="DocumentAlreadyExistsException"/>. Marking them replayable is all it does — Wolverine's durability
/// agent moves them back into the durable inbox, and <c>ChannelNotificationArrivedHandler</c> then records each
/// one in the feed under its original received time and re-projects the affected customer. Dead letters of any
/// other message or exception type are only reported. Once the old ones are replayed this is a cheap no-op.
/// </summary>
public sealed class NotificationDeadLetterReplay(IWolverineRuntime runtime, ILogger<NotificationDeadLetterReplay> logger) : BackgroundService
{
    // Let Wolverine (message store, durability agent, listeners) finish starting first.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
            await ReplayAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Best-effort repair: never take the worker down.
            logger.LogError(ex, "Replaying dead-lettered Channel notifications failed");
        }
    }

    /// <summary>Marks the matching dead letters replayable and returns how many were marked.</summary>
    public async Task<int> ReplayAsync(CancellationToken ct)
    {
        var deadLetters = runtime.Storage.DeadLetters;

        // Exactly the strings Wolverine stores for a dead letter: the message type alias and the exception's
        // FullNameInCode. Already-replayable rows are left to the durability agent, so the count is accurate.
        var result = await deadLetters.ReplayMatchingAsync(new DeadLetterEnvelopeQuery
        {
            MessageType = typeof(ChannelNotificationArrived).ToMessageTypeName(),
            ExceptionType = typeof(DocumentAlreadyExistsException).FullNameInCode(),
            Replayable = false,
        }, ct);
        var replayed = result.Affected ?? 0;
        if (replayed > 0)
        {
            logger.LogWarning("Replaying {Count} Channel notifications dead-lettered by the read-model snapshot collision", replayed);
        }

        var remaining = await deadLetters.QueryAsync(new DeadLetterEnvelopeQuery { Replayable = false, PageSize = 100 }, ct);
        if (remaining.TotalCount > 0)
        {
            logger.LogInformation("{Count} other dead letters left in place: {Kinds}", remaining.TotalCount,
                string.Join(", ", remaining.Envelopes.Select(e => $"{e.MessageType} ({e.ExceptionType})").Distinct()));
        }
        else if (replayed == 0)
        {
            logger.LogInformation("No dead letters to replay");
        }

        return replayed;
    }
}
