namespace GChannel.Core.Messaging;

/// <summary>Service Bus entity names — must match the topology declared in the Aspire AppHost.</summary>
public static class MessagingTopology
{
    /// <summary>Aspire resource / connection-string name of the Service Bus namespace.</summary>
    public const string ConnectionName = "messaging";

    public const string EstateEventsTopic = "estate-events";
    public const string WorkerEstateSubscription = "worker-estate";
    public const string ApiCacheSubscription = "api-cache";
    public const string EstateSyncQueue = "estate-sync";
    public const string JobsQueue = "jobs";
    public const string OperationsQueue = "operations";

    /// <summary>
    /// Wolverine's shared dead-letter queue: a message that exhausts its retries is <em>sent</em> here (durable
    /// listeners have already completed the broker message when they persisted it to the inbox, so the native
    /// per-entity dead-letter sub-queue cannot be used). Declared in the AppHost like every other entity.
    /// </summary>
    public const string DeadLetterQueue = "wolverine-dead-letter-queue";
}

/// <summary>Names of the self-rescheduling recurring jobs (ids of their <c>RecurringJobState</c> documents).</summary>
public static class RecurringJobs
{
    public const string EstateSync = "estate-sync";
    public const string DashboardSnapshot = "dashboard-snapshot";
    public const string BillingSync = "billing-sync";
    public const string BillingPostureScan = "billing-posture-scan";

    /// <summary>
    /// Execution budgets of the tick handlers, in seconds (Wolverine's <c>[MessageTimeout]</c> takes a constant). Every
    /// job is paced by Google per-minute quotas and stalls further on 429 cooldowns, so these are generous ceilings
    /// rather than expected durations; the worker's watchdog treats a run that outlives its budget as stuck.
    /// </summary>
    public const int EstateSyncBudgetSeconds = 2 * 60 * 60;
    public const int DashboardSnapshotBudgetSeconds = 2 * 60 * 60;
    public const int BillingSyncBudgetSeconds = 60 * 60;
    public const int BillingPostureScanBudgetSeconds = 3 * 60 * 60;
}
