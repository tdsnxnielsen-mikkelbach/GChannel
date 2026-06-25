namespace GChannel.Shared.Contracts;

// Channel partner link contracts (§5 — distributor / n-tier). These mirror a trimmed, UI-friendly
// subset of accounts.channelPartnerLinks so UI code never references Google REST shapes directly. A
// channel partner link is how a distributor links a downstream reseller (the "channel partner") to
// their account; customers can then be owned by that partner (see Customer.ChannelPartnerId).

/// <summary>A link between a distributor and a downstream reseller. Maps to <c>accounts.channelPartnerLinks</c>.</summary>
public sealed record ChannelPartnerLink
{
    /// <summary>Resource name, e.g. "accounts/{account}/channelPartnerLinks/{id}".</summary>
    public required string Name { get; init; }

    /// <summary>Short id (the last path segment of <see cref="Name"/>). Matches <c>Customer.ChannelPartnerId</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Cloud Identity ID of the linked reseller (the channel partner).</summary>
    public string? ResellerCloudIdentityId { get; init; }

    /// <summary>State of the link, e.g. <c>INVITED</c>, <c>ACTIVE</c>, <c>REVOKED</c>, <c>SUSPENDED</c>.</summary>
    public string? LinkState { get; init; }

    /// <summary>URI of the page where the partner accepts the link invitation (output only).</summary>
    public string? InviteLinkUri { get; init; }

    /// <summary>Public identifier that a reseller shares to identify themselves (output only).</summary>
    public string? PublicId { get; init; }

    public DateTimeOffset? CreateTime { get; init; }
    public DateTimeOffset? UpdateTime { get; init; }

    /// <summary>Cloud Identity summary of the channel partner (output only).</summary>
    public ChannelPartnerCloudIdentity? ChannelPartner { get; init; }
}

/// <summary>Read-only Cloud Identity summary for a channel partner. Maps to <c>CloudIdentityInfo</c>.</summary>
public sealed record ChannelPartnerCloudIdentity
{
    public string? CustomerType { get; init; }
    public string? PrimaryDomain { get; init; }
    public bool IsDomainVerified { get; init; }
    public string? AlternateEmail { get; init; }
}

/// <summary>Result of listing channel partner links.</summary>
public sealed record ChannelPartnerLinksResult
{
    public IReadOnlyList<ChannelPartnerLink> Links { get; init; } = [];
}

/// <summary>Create payload for a channel partner link (<c>channelPartnerLinks.create</c>).</summary>
public sealed record CreateChannelPartnerLinkRequest
{
    /// <summary>Cloud Identity ID of the reseller to invite. Required.</summary>
    public required string ResellerCloudIdentityId { get; init; }
}

/// <summary>Update payload for a channel partner link's state (<c>channelPartnerLinks.patch</c>).</summary>
public sealed record UpdateChannelPartnerLinkRequest
{
    /// <summary>The new link state, e.g. <c>ACTIVE</c>, <c>SUSPENDED</c>, <c>REVOKED</c>. Required.</summary>
    public required string LinkState { get; init; }
}
