namespace GChannel.Shared.Contracts;

// Catalog (read-only) contracts shared between the Web client and the API service. These mirror a
// trimmed, UI-friendly subset of the Cloud Channel catalog resources (products, SKUs, offers and
// SKU groups) so UI code never references Google REST shapes directly.

/// <summary>A product the reseller is authorized to sell. Maps to <c>products.list</c>.</summary>
public sealed record CatalogProduct
{
    /// <summary>Resource name, e.g. "products/{product_id}".</summary>
    public required string Name { get; init; }

    /// <summary>Short identifier (the last path segment of <see cref="Name"/>).</summary>
    public required string Id { get; init; }

    public string? DisplayName { get; init; }
    public string? Description { get; init; }
}

/// <summary>Result of listing the sellable products.</summary>
public sealed record CatalogProductsResult
{
    public IReadOnlyList<CatalogProduct> Products { get; init; } = [];
}

/// <summary>A SKU within a product. Maps to <c>products.skus.list</c>.</summary>
public sealed record CatalogSku
{
    /// <summary>Resource name, e.g. "products/{product_id}/skus/{sku_id}".</summary>
    public required string Name { get; init; }

    public required string Id { get; init; }

    /// <summary>Id of the product this SKU belongs to (for navigation back to the product).</summary>
    public string? ProductId { get; init; }

    public string? DisplayName { get; init; }
    public string? Description { get; init; }
}

/// <summary>Result of listing the SKUs for a product.</summary>
public sealed record CatalogSkusResult
{
    public IReadOnlyList<CatalogSku> Skus { get; init; } = [];
}

/// <summary>An offer the reseller can sell. Maps to <c>accounts.offers.list</c>.</summary>
public sealed record CatalogOffer
{
    /// <summary>Resource name, e.g. "accounts/{account_id}/offers/{offer_id}".</summary>
    public required string Name { get; init; }

    /// <summary>Short offer id (the last path segment of <see cref="Name"/>).</summary>
    public string? OfferId { get; init; }

    public string? DisplayName { get; init; }
    public string? Description { get; init; }

    /// <summary>The SKU resource name this offer relates to.</summary>
    public string? SkuName { get; init; }

    /// <summary>Id of the SKU this offer relates to (for navigation to the product/SKU).</summary>
    public string? SkuId { get; init; }

    /// <summary>Human-friendly SKU name resolved from the Catalog (falls back to <see cref="SkuId"/> in the UI).</summary>
    public string? SkuDisplayName { get; init; }

    /// <summary>Id of the product the related SKU belongs to.</summary>
    public string? ProductId { get; init; }

    public string? DealCode { get; init; }

    /// <summary>Wholesale list pricing for each priced resource (seats, GB, etc.). Empty if not exposed.</summary>
    public IReadOnlyList<OfferPrice> Pricing { get; init; } = [];

    /// <summary>Payment plan (e.g. COMMITMENT, FLEXIBLE) from the offer plan.</summary>
    public string? PaymentPlan { get; init; }

    /// <summary>Human-friendly payment cycle (e.g. "Monthly", "Annual").</summary>
    public string? PaymentCycle { get; init; }
}

/// <summary>A monetary amount mapped from <c>google.type.Money</c> (currency + whole units + nano fraction).</summary>
public sealed record MoneyAmount
{
    public required string CurrencyCode { get; init; }
    public long Units { get; init; }
    public int Nanos { get; init; }

    /// <summary>Decimal value (units + nanos/1e9), useful for arithmetic.</summary>
    public decimal Value => Units + (Nanos / 1_000_000_000m);

    /// <summary>Pre-formatted display string, e.g. "EUR 4.32".</summary>
    public string Display => $"{CurrencyCode} {Value:0.00}";
}

/// <summary>Wholesale price for one resource type on an offer (the reseller's cost from Google).</summary>
public sealed record OfferPrice
{
    /// <summary>Priced resource: SEAT, MAU, GB, LICENSED_USER, MINUTES, IAAS_USAGE, SUBSCRIPTION, …</summary>
    public string? ResourceType { get; init; }

    public MoneyAmount? BasePrice { get; init; }
    public MoneyAmount? EffectivePrice { get; init; }

    /// <summary>Discount as a percentage (0.2 = 20%) applied to base price.</summary>
    public decimal DiscountPercent { get; init; }

    /// <summary>Optional per-resource tier banding (e.g. seat-count price breaks).</summary>
    public IReadOnlyList<OfferPriceTier> Tiers { get; init; } = [];
}

/// <summary>A per-resource price tier (first/last resource count and its effective price).</summary>
public sealed record OfferPriceTier
{
    public int FirstResource { get; init; }
    public int LastResource { get; init; }
    public MoneyAmount? EffectivePrice { get; init; }
}

/// <summary>Result of listing the sellable offers.</summary>
public sealed record CatalogOffersResult
{
    public IReadOnlyList<CatalogOffer> Offers { get; init; } = [];
}

/// <summary>A rebilling-supported SKU group. Maps to <c>accounts.skuGroups.list</c>.</summary>
public sealed record CatalogSkuGroup
{
    /// <summary>Resource name, e.g. "accounts/{account_id}/skuGroups/{sku_group_id}".</summary>
    public required string Name { get; init; }

    public required string Id { get; init; }
    public string? DisplayName { get; init; }
}

/// <summary>Result of listing the SKU groups.</summary>
public sealed record CatalogSkuGroupsResult
{
    public IReadOnlyList<CatalogSkuGroup> SkuGroups { get; init; } = [];
}

/// <summary>A billable SKU within a SKU group. Maps to <c>accounts.skuGroups.billableSkus.list</c>.</summary>
public sealed record CatalogBillableSku
{
    /// <summary>SKU resource name, e.g. "products/{product}/skus/{sku}".</summary>
    public required string Sku { get; init; }

    /// <summary>Id of the SKU (for navigation to the product/SKU and its offers).</summary>
    public string? SkuId { get; init; }

    /// <summary>Id of the product the SKU belongs to.</summary>
    public string? ProductId { get; init; }

    public string? SkuDisplayName { get; init; }

    /// <summary>Service resource name, e.g. "services/{service}".</summary>
    public string? Service { get; init; }

    public string? ServiceDisplayName { get; init; }
}

/// <summary>Result of listing the billable SKUs in a SKU group.</summary>
public sealed record CatalogBillableSkusResult
{
    public IReadOnlyList<CatalogBillableSku> BillableSkus { get; init; } = [];
}
