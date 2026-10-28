using GChannel.Shared.Contracts;

namespace GChannel.Core.Persistence.Documents;

/// <summary>
/// Per-customer sync bookkeeping, deliberately kept OUT of the event stream (a sync pass that observes no
/// change appends no events, but still has to advance the staleness rotation).
/// </summary>
public sealed class CustomerSyncState
{
    /// <summary>The bare customer id.</summary>
    public string Id { get; set; } = string.Empty;
    public string? OwningLinkId { get; set; }
    public bool IsDeleted { get; set; }
    /// <summary>When the customer's ENTITLEMENTS were last synced (or skipped). New rows start at MinValue so they head the rotation queue.</summary>
    public DateTimeOffset LastSyncedUtc { get; set; } = DateTimeOffset.MinValue;
    /// <summary>When the customer's metadata was last observed by any path.</summary>
    public DateTimeOffset LastObservedUtc { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Per-channel-partner-link sync bookkeeping (customer count, freshness, last fan-out error).</summary>
public sealed class LinkSyncState
{
    /// <summary>The bare link id.</summary>
    public string Id { get; set; } = string.Empty;
    public int CustomerCount { get; set; }
    public DateTimeOffset LastSyncedUtc { get; set; } = DateTimeOffset.MinValue;
    public string? SyncError { get; set; }
}

/// <summary>Per-scope sync bookkeeping (e.g. "links").</summary>
public sealed class SyncCursor
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset? LastFullPassUtc { get; set; }
    public DateTimeOffset? LastCycleUtc { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// State of one self-rescheduling recurring job. The handler that runs a job tick re-schedules the next
/// tick through Wolverine and records the new <see cref="RunId"/> + <see cref="NextRunUtc"/> here in the
/// same transaction; a tick whose run id no longer matches is a duplicate chain and is ignored, and the
/// startup bootstrap only starts a chain when none is pending.
/// </summary>
public sealed class RecurringJobState
{
    /// <summary>Job name (see <c>RecurringJobs</c>).</summary>
    public string Id { get; set; } = string.Empty;
    public Guid RunId { get; set; }
    public DateTimeOffset? NextRunUtc { get; set; }
    public DateTimeOffset? LastStartedUtc { get; set; }
    public DateTimeOffset? LastCompletedUtc { get; set; }
    public double? LastDurationSeconds { get; set; }
    public string? LastError { get; set; }
    public bool IsRunning { get; set; }
}

/// <summary>Persisted dashboard refresh status (replaces the former Redis <c>dashboard:refresh:status</c> key).</summary>
public sealed class DashboardRefreshState
{
    public const string SingletonId = "dashboard";
    public string Id { get; set; } = SingletonId;
    public DashboardRefreshStatus Status { get; set; } = new();
}

/// <summary>Audit record written each time a Cloud Identity existence check runs.</summary>
public sealed class IdentityCheckLog
{
    public Guid Id { get; set; }
    public string Domain { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public int AccountsFound { get; set; }
    public string? PerformedBy { get; set; }
    public DateTimeOffset PerformedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One Channel change notification received from Google Pub/Sub (the feed, newest first).</summary>
public sealed class ChannelNotificationRecord
{
    /// <summary>The Pub/Sub message id (natural de-duplication key).</summary>
    public string Id { get; set; } = string.Empty;
    public ChannelNotification Notification { get; set; } = null!;
    public DateTimeOffset ReceivedAt { get; set; }
    public string? CustomerId { get; set; }
    public string? EntitlementId { get; set; }
}
