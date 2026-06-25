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
}

/// <summary>Result of listing long-running operations.</summary>
public sealed record ChannelOperationsResult
{
    public IReadOnlyList<ChannelOperation> Operations { get; init; } = [];
}
