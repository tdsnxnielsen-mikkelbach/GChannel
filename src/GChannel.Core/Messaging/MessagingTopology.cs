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
}

/// <summary>Names of the self-rescheduling recurring jobs (ids of their <c>RecurringJobState</c> documents).</summary>
public static class RecurringJobs
{
    public const string EstateSync = "estate-sync";
    public const string DashboardSnapshot = "dashboard-snapshot";
    public const string BillingSync = "billing-sync";
    public const string BillingPostureScan = "billing-posture-scan";
}
