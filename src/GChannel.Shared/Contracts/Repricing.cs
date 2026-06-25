namespace GChannel.Shared.Contracts;

// Repricing / rebilling-margin contracts (§6). These mirror a trimmed, UI-friendly subset of
// accounts.customers.customerRepricingConfigs and
// accounts.channelPartnerLinks.channelPartnerRepricingConfigs so UI code never references Google
// REST shapes directly. A repricing config is how a reseller (or distributor) marks up or discounts
// what a customer (or downstream channel partner) is billed for a given invoice month — i.e. the
// reseller's margin. Customer configs hang off a Customer (§2) and target one of its Entitlements
// (§3); channel-partner configs hang off a channel partner link (§5).

/// <summary>How the relative cost of a repricing config is computed (Google's <c>RebillingBasis</c>).</summary>
public static class RebillingBases
{
    /// <summary>Bill from the list price an end customer would pay buying directly from Google.</summary>
    public const string CostAtList = "COST_AT_LIST";

    /// <summary>Bill from the direct customer cost (all discounts except the Reseller Program Discount).</summary>
    public const string DirectCustomerCost = "DIRECT_CUSTOMER_COST";
}

/// <summary>The level a repricing config applies at.</summary>
public static class RepricingGranularities
{
    /// <summary>Applies to a single entitlement (the recommended granularity).</summary>
    public const string Entitlement = "ENTITLEMENT";

    /// <summary>Applies to a whole channel partner's bill (channel-partner configs only).</summary>
    public const string ChannelPartner = "CHANNEL_PARTNER";
}

/// <summary>
/// A repricing (rebilling-margin) configuration. Maps to either
/// <c>accounts.customers.customerRepricingConfigs</c> or
/// <c>accounts.channelPartnerLinks.channelPartnerRepricingConfigs</c> — the two share the same shape.
/// </summary>
public sealed record RepricingConfig
{
    /// <summary>Resource name, e.g. "accounts/{a}/customers/{c}/customerRepricingConfigs/{id}".</summary>
    public required string Name { get; init; }

    /// <summary>Short id (the last path segment of <see cref="Name"/>).</summary>
    public required string Id { get; init; }

    /// <summary>Invoice month the config takes effect from (year component).</summary>
    public int EffectiveInvoiceYear { get; init; }

    /// <summary>Invoice month the config takes effect from (1–12).</summary>
    public int EffectiveInvoiceMonth { get; init; }

    /// <summary>The percentage mark-up (positive) or discount (negative) applied. 0 = pass-through.</summary>
    public decimal PercentageAdjustment { get; init; }

    /// <summary>The <see cref="RebillingBases"/> value driving the relative cost.</summary>
    public string? RebillingBasis { get; init; }

    /// <summary>The <see cref="RepricingGranularities"/> level this config applies at.</summary>
    public string Granularity { get; init; } = RepricingGranularities.Entitlement;

    /// <summary>Full entitlement resource name targeted (entitlement granularity only).</summary>
    public string? EntitlementName { get; init; }

    /// <summary>Short entitlement id targeted (for navigation/correlation; entitlement granularity only).</summary>
    public string? EntitlementId { get; init; }

    /// <summary>Number of conditional overrides attached to the config (surfaced read-only).</summary>
    public int ConditionalOverrideCount { get; init; }

    public DateTimeOffset? UpdateTime { get; init; }
}

/// <summary>Result of listing repricing configs.</summary>
public sealed record RepricingConfigsResult
{
    public IReadOnlyList<RepricingConfig> Configs { get; init; } = [];
}

/// <summary>
/// Create/update payload for a repricing config. When <see cref="EntitlementId"/> is set the config
/// uses entitlement granularity (required for customer configs); when it is blank a channel-partner
/// config falls back to whole-partner (channel-partner) granularity.
/// </summary>
public sealed record SaveRepricingConfigRequest
{
    /// <summary>Invoice month the config takes effect from (year). Must be the current or a future month.</summary>
    public required int EffectiveInvoiceYear { get; init; }

    /// <summary>Invoice month the config takes effect from (1–12). Must be the current or a future month.</summary>
    public required int EffectiveInvoiceMonth { get; init; }

    /// <summary>The percentage mark-up (positive) or discount (negative). 0 = pass-through.</summary>
    public required decimal PercentageAdjustment { get; init; }

    /// <summary>The <see cref="RebillingBases"/> value to bill from. Required.</summary>
    public required string RebillingBasis { get; init; }

    /// <summary>Short entitlement id to target (entitlement granularity). Required for customer configs.</summary>
    public string? EntitlementId { get; init; }
}
