using GChannel.Core.Configuration;
using GChannel.Core.Messaging;
using Microsoft.Extensions.Options;

namespace GChannel.Worker.Infrastructure;

/// <summary>One self-rescheduling recurring job: when it runs, how the next run time is derived, and the tick message that drives it.</summary>
/// <param name="StuckAfter">
/// How long a run may show as "running" before the watchdog declares it stuck and starts a fresh chain. A run can
/// never legitimately outlive its handler's <c>[MessageTimeout]</c> budget (Wolverine cancels it), so this is that
/// budget plus a grace period for the cancellation / retry bookkeeping.
/// </param>
public sealed record RecurringJobDefinition(
    string Name,
    Func<bool> IsEnabled,
    Func<DateTimeOffset, DateTimeOffset> NextRunAfter,
    Func<Guid, object> CreateTick,
    TimeSpan StuckAfter);

/// <summary>The worker's recurring jobs, derived from the Google Channel / Billing options.</summary>
public sealed class RecurringJobCatalog(IOptions<GoogleChannelOptions> channelOptions, IOptions<GoogleBillingOptions> billingOptions)
{
    private static readonly TimeSpan StuckGrace = TimeSpan.FromMinutes(30);

    public IReadOnlyList<RecurringJobDefinition> Jobs
    {
        get
        {
            var channel = channelOptions.Value;
            var billing = billingOptions.Value;
            var refreshInterval = TimeSpan.FromSeconds(Math.Max(30, channel.BackgroundRefreshSeconds));
            var billingInterval = TimeSpan.FromHours(Math.Max(1, billing.SyncIntervalHours));
            var postureHour = Math.Clamp(billing.PostureScanHourUtc, 0, 23);

            return
            [
                // Rolling estate read-model sync — the single consumer of the contended Channel API quotas.
                new RecurringJobDefinition(
                    RecurringJobs.EstateSync,
                    () => channel.ReadModelSyncEnabled,
                    completed => completed + refreshInterval,
                    runId => new RunEstateSyncCycle(runId),
                    StuckAfter: Budget(RecurringJobs.EstateSyncBudgetSeconds)),

                // Live dashboard aggregation (read model OFF): recompute the summary with the service account
                // and warm the Redis cache. With the read model on, the dashboard derives from Marten instead.
                new RecurringJobDefinition(
                    RecurringJobs.DashboardSnapshot,
                    () => channel.BackgroundRefreshEnabled && !channel.UseReadModel,
                    completed => completed + refreshInterval,
                    runId => new RefreshDashboardSnapshot(runId),
                    StuckAfter: Budget(RecurringJobs.DashboardSnapshotBudgetSeconds)),

                // BigQuery billing-export sync.
                new RecurringJobDefinition(
                    RecurringJobs.BillingSync,
                    () => billing.BigQueryConfigured && channel.HasServiceAccountCredential,
                    completed => completed + billingInterval,
                    runId => new RunBillingSyncCycle(runId),
                    StuckAfter: Budget(RecurringJobs.BillingSyncBudgetSeconds)),

                // Nightly billing IAM security-posture scan at PostureScanHourUtc.
                new RecurringJobDefinition(
                    RecurringJobs.BillingPostureScan,
                    () => billing.PostureScanEnabled && channel.HasServiceAccountCredential && !string.IsNullOrWhiteSpace(channel.ImpersonateUser),
                    completed => NextDailyAt(completed, postureHour),
                    runId => new RunBillingPostureScan(runId),
                    StuckAfter: Budget(RecurringJobs.BillingPostureScanBudgetSeconds)),
            ];
        }
    }

    public RecurringJobDefinition this[string name] => Jobs.First(j => j.Name == name);

    private static TimeSpan Budget(int budgetSeconds) => TimeSpan.FromSeconds(budgetSeconds) + StuckGrace;

    /// <summary>The next occurrence of <paramref name="hourUtc"/>:00 strictly after <paramref name="after"/>.</summary>
    public static DateTimeOffset NextDailyAt(DateTimeOffset after, int hourUtc)
    {
        var next = new DateTimeOffset(after.Year, after.Month, after.Day, hourUtc, 0, 0, TimeSpan.Zero);
        if (next <= after) { next = next.AddDays(1); }
        return next;
    }
}
