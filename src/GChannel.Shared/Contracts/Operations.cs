namespace GChannel.Shared.Contracts;

/// <summary>
/// A Cloud Channel long-running operation (LRO). Mutating calls (entitlement create/change/state
/// changes, transfers) return one of these; poll it by id until <see cref="Done"/> is true to know
/// when Google has finished provisioning.
/// </summary>
public sealed record ChannelOperation
{
    /// <summary>Full resource name of the operation, e.g. "operations/...".</summary>
    public required string Name { get; init; }

    /// <summary>The short operation id (last path segment of <see cref="Name"/>).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>True once Google has finished the operation.</summary>
    public bool Done { get; init; }

    /// <summary>Error message when the operation failed.</summary>
    public string? Error { get; init; }

    /// <summary>gRPC status code when the operation failed (0/null = no error).</summary>
    public int? ErrorCode { get; init; }

    /// <summary>The operation type from the operation metadata, e.g. "CREATE_ENTITLEMENT".</summary>
    public string? OperationType { get; init; }

    /// <summary>The resource the operation acted on (entitlement/customer name) when resolvable.</summary>
    public string? ResourceName { get; init; }

    /// <summary>Customer id parsed from <see cref="ResourceName"/> (for deep-linking to §2).</summary>
    public string? CustomerId { get; init; }

    /// <summary>Entitlement id parsed from <see cref="ResourceName"/> (for deep-linking to §3).</summary>
    public string? EntitlementId { get; init; }

    /// <summary>Google's structured error details (ErrorInfo reason, field violations, debug/trace info) as compact JSON.</summary>
    public IReadOnlyList<string> ErrorDetails { get; init; } = [];

    /// <summary>Resources the finished operation produced, e.g. the entitlements a transfer created.</summary>
    public IReadOnlyList<string> ResultResources { get; init; } = [];

    /// <summary>What GChannel recorded when the operation was submitted from the app; null for operations it never saw.</summary>
    public OperationTracking? Tracking { get; init; }
}

/// <summary>
/// The app's own record of an operation it submitted: who started it, from which action, with which
/// inputs, and how the worker's tracking went. Google's API cannot list operations, so this is what the
/// Operations page lists and correlates.
/// </summary>
public sealed record OperationTracking
{
    /// <summary>The app action that returned the operation, e.g. "transferEntitlements" or "changeParameters".</summary>
    public required string Action { get; init; }

    public string? RequestedBy { get; init; }
    public DateTimeOffset SubmittedAt { get; init; }

    /// <summary>When the outcome was first known (inline at submit, or by the worker's poll).</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>When the worker last polled Google for the operation.</summary>
    public DateTimeOffset? LastCheckedAt { get; init; }
    public int PollAttempts { get; init; }

    public string? CustomerId { get; init; }
    public string? CustomerName { get; init; }
    public string? CustomerDomain { get; init; }
    public string? EntitlementId { get; init; }

    /// <summary>Human description of the affected entitlement (product · SKU · offer) when known.</summary>
    public string? Subject { get; init; }

    /// <summary>The request inputs (offer, seats, billing account, …). Secrets such as transfer tokens are never recorded.</summary>
    public IReadOnlyList<OperationDetail> Details { get; init; } = [];

    /// <summary>Tracking problems, e.g. a failed poll or an abandoned tracking window.</summary>
    public string? Note { get; init; }
}

/// <summary>One labelled request input recorded with an operation.</summary>
public sealed record OperationDetail(string Label, string Value);

/// <summary>Result of listing long-running operations.</summary>
public sealed record ChannelOperationsResult
{
    public IReadOnlyList<ChannelOperation> Operations { get; init; } = [];
}
