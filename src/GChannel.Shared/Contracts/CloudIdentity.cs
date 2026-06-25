namespace GChannel.Shared.Contracts;

/// <summary>
/// Request to verify whether a Cloud Identity account already exists for a domain.
/// Maps to accounts.checkCloudIdentityAccountsExist on the Google Cloud Channel API.
/// </summary>
public sealed record CheckCloudIdentityRequest
{
    /// <summary>The customer's primary domain, e.g. "example.com".</summary>
    public required string Domain { get; init; }

    /// <summary>Optional primary admin email used to disambiguate consumer accounts.</summary>
    public string? PrimaryAdminEmail { get; init; }
}

/// <summary>Aggregated result returned to the UI.</summary>
public sealed record CheckCloudIdentityResult
{
    public required string Domain { get; init; }

    /// <summary>
    /// True when at least one domain-verified (<c>DOMAIN</c>) Cloud Identity account exists for the
    /// domain. Only <c>DOMAIN</c> accounts are usable for downstream reseller actions.
    /// </summary>
    public bool Exists { get; init; }

    /// <summary>
    /// True when one or more matched accounts are not domain-verified (e.g. <c>TEAM</c>) and so
    /// cannot be used for reseller actions. Surfaced as a warning in the UI.
    /// </summary>
    public bool HasNonDomainAccounts { get; init; }

    public IReadOnlyList<CloudIdentityAccount> Accounts { get; init; } = [];

    /// <summary>
    /// A channel partner link whose Cloud Identity primary domain matches this domain, if any.
    /// Cross-correlation surfaced in the UI so the operator can see, e.g., a still-pending
    /// (<c>INVITED</c>) partner link invitation for the same domain.
    /// </summary>
    public ChannelPartnerLink? PartnerLink { get; init; }
}

/// <summary>A single Cloud Identity account match.</summary>
public sealed record CloudIdentityAccount
{
    public bool Existing { get; init; }
    public bool Owned { get; init; }
    public string? CustomerName { get; init; }
    public string? CustomerCloudIdentityId { get; init; }
    public string? CustomerType { get; init; }

    /// <summary>
    /// True when <see cref="CustomerType"/> is <c>DOMAIN</c> — the only type usable for downstream
    /// reseller actions (customer creation, transfers, entitlements).
    /// </summary>
    public bool IsDomain { get; init; }

    public string? ChannelPartnerCloudIdentityId { get; init; }
}

/// <summary>A single past Cloud Identity check (latest result per domain), used to offer rechecks.</summary>
public sealed record IdentityCheckHistoryItem
{
    public required string Domain { get; init; }
    public bool Exists { get; init; }
    public int AccountsFound { get; init; }
    public DateTimeOffset PerformedAt { get; init; }
    public string? PerformedBy { get; init; }
}

/// <summary>Result of listing recent Cloud Identity checks.</summary>
public sealed record IdentityCheckHistoryResult
{
    public IReadOnlyList<IdentityCheckHistoryItem> Items { get; init; } = [];
}
