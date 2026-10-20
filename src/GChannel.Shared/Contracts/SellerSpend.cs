namespace GChannel.Shared.Contracts;

// Native Google vs third-party (Marketplace / ISV / partner) split of billed spend, plus Marketplace
// orders. §1 comes from the Cloud Billing BigQuery export's transaction_type / seller_name columns;
// §2 from the Cloud Commerce Consumer Procurement API. See docs/billing-permissions.md (Part 4) and
// docs/todos/15-cloud-billing-bigquery-export.md.

/// <summary>Who sold a billed line item, per the export's <c>transaction_type</c>.</summary>
public enum SellerCategory
{
    /// <summary>Line items without a transaction_type (taxes, adjustments, older rows).</summary>
    Unknown = 0,
    /// <summary><c>GOOGLE</c> — native Google Cloud services.</summary>
    Google,
    /// <summary><c>THIRD_PARTY_RESELLER</c> — Marketplace / ISV product resold by Google.</summary>
    ThirdPartyReseller,
    /// <summary><c>THIRD_PARTY_AGENCY</c> — partner sells, Google acts as agent.</summary>
    ThirdPartyAgency,
}

public static class SellerCategories
{
    public static SellerCategory Parse(string? transactionType) => transactionType?.Trim().ToUpperInvariant() switch
    {
        "GOOGLE" => SellerCategory.Google,
        "THIRD_PARTY_RESELLER" => SellerCategory.ThirdPartyReseller,
        "THIRD_PARTY_AGENCY" => SellerCategory.ThirdPartyAgency,
        _ => SellerCategory.Unknown
    };

    public static bool IsThirdParty(SellerCategory c) =>
        c is SellerCategory.ThirdPartyReseller or SellerCategory.ThirdPartyAgency;

    public static string Label(SellerCategory c) => c switch
    {
        SellerCategory.Google => "Google Cloud",
        SellerCategory.ThirdPartyReseller => "Marketplace (resold by Google)",
        SellerCategory.ThirdPartyAgency => "Partner (Google as agent)",
        _ => "Unclassified"
    };
}

/// <summary>Billed spend for one (seller, service) slice over a period, in one currency.</summary>
public sealed record SellerSpendLine
{
    public SellerCategory Category { get; init; }
    /// <summary>Legal seller name from the export (ISV / partner); null for Google rows.</summary>
    public string? SellerName { get; init; }
    /// <summary>Product / service description (e.g. "Compute Engine", "Anthropic Claude 3.5 Sonnet").</summary>
    public string? ServiceDescription { get; init; }
    public required string Currency { get; init; }
    /// <summary>Net cost after credits.</summary>
    public decimal Cost { get; init; }
    public decimal GrossCost { get; init; }
    public decimal Credits { get; init; }
}

/// <summary>Native vs third-party totals for a scope (estate or one customer) over a window.</summary>
public sealed record SellerSpendSummary
{
    /// <summary>Customer id when scoped to one customer; null for the estate.</summary>
    public string? CustomerId { get; init; }
    /// <summary>Invoice months covered (YYYYMM), oldest first.</summary>
    public IReadOnlyList<string> InvoiceMonths { get; init; } = [];

    /// <summary>Totals per category and currency.</summary>
    public IReadOnlyList<SellerSpendLine> ByCategory { get; init; } = [];
    /// <summary>Totals per third-party seller and currency (Google excluded), largest first.</summary>
    public IReadOnlyList<SellerSpendLine> ThirdPartySellers { get; init; } = [];
    /// <summary>Totals per third-party product/service and currency, largest first.</summary>
    public IReadOnlyList<SellerSpendLine> ThirdPartyProducts { get; init; } = [];

    public DateTimeOffset? AsOf { get; init; }
    public bool Unconfigured { get; init; }
}

/// <summary>Per-customer native/third-party split for the estate table.</summary>
public sealed record CustomerSellerSplit
{
    public required string CustomerId { get; init; }
    public string? OwningLinkId { get; init; }
    public required string Currency { get; init; }
    public decimal GoogleCost { get; init; }
    public decimal ThirdPartyCost { get; init; }
    public decimal TotalCost => GoogleCost + ThirdPartyCost;
    public double ThirdPartyPercent => TotalCost == 0 ? 0 : Math.Round(100.0 * (double)(ThirdPartyCost / TotalCost), 1);
    /// <summary>Distinct third-party sellers this customer paid in the window.</summary>
    public IReadOnlyList<string> Sellers { get; init; } = [];
}

public sealed record CustomerSellerSplitResult
{
    public IReadOnlyList<CustomerSellerSplit> Customers { get; init; } = [];
    public DateTimeOffset? AsOf { get; init; }
    public bool Unconfigured { get; init; }
}

// ---- §2 Marketplace orders (Cloud Commerce Consumer Procurement API) ----

/// <summary>A Marketplace order (subscription / plan) on a billing account.</summary>
public sealed record MarketplaceOrder
{
    /// <summary>Full resource name, <c>billingAccounts/{id}/orders/{orderId}</c>.</summary>
    public required string Name { get; init; }
    public required string BillingAccountId { get; init; }
    public string? CustomerId { get; init; }
    public string? DisplayName { get; init; }
    /// <summary>Product external name(s) from the order's line items, e.g. <c>databricks.endpoints.databricks.com</c>.</summary>
    public IReadOnlyList<string> Products { get; init; } = [];
    /// <summary>Offer / plan resource names from the line items.</summary>
    public IReadOnlyList<string> Offers { get; init; } = [];
    /// <summary>Provider id inferred from the product external name (e.g. <c>databricks</c>), when present.</summary>
    public string? Provider { get; init; }
    public DateTimeOffset? CreateTime { get; init; }
    public DateTimeOffset? UpdateTime { get; init; }
    /// <summary>True when the order has a pending or active line item (not fully cancelled).</summary>
    public bool Active { get; init; }
    public DateTimeOffset LastSyncedUtc { get; init; }
}

public sealed record MarketplaceOrdersResult
{
    public IReadOnlyList<MarketplaceOrder> Orders { get; init; } = [];
    /// <summary>Billing accounts whose orders couldn't be listed (permission / API not enabled), with the error.</summary>
    public IReadOnlyDictionary<string, string> Errors { get; init; } = new Dictionary<string, string>();
    public DateTimeOffset? AsOf { get; init; }
    public bool Unconfigured { get; init; }
}
