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

    /// <summary>True when at least one Cloud Identity account exists for the domain.</summary>
    public bool Exists { get; init; }

    public IReadOnlyList<CloudIdentityAccount> Accounts { get; init; } = [];
}

/// <summary>A single Cloud Identity account match.</summary>
public sealed record CloudIdentityAccount
{
    public bool Existing { get; init; }
    public bool Owned { get; init; }
    public string? CustomerName { get; init; }
    public string? CustomerCloudIdentityId { get; init; }
    public string? CustomerType { get; init; }
    public string? ChannelPartnerCloudIdentityId { get; init; }
}
