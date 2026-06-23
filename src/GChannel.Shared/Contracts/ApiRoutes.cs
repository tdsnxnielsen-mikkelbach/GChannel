namespace GChannel.Shared.Contracts;

/// <summary>
/// Well-known route fragments shared between the Web client and the API service.
/// Keeps the abstraction in one place so UI code never references Google REST paths.
/// </summary>
public static class ApiRoutes
{
    public const string CheckCloudIdentity = "/api/accounts/check-cloud-identity";

    /// <summary>Recent Cloud Identity checks (latest result per domain).</summary>
    public const string IdentityCheckHistory = "/api/accounts/check-cloud-identity/history";

    // Catalog (read-only) browsing.
    public const string Products = "/api/catalog/products";
    public const string Offers = "/api/catalog/offers";
    public const string SkuGroups = "/api/catalog/sku-groups";

    /// <summary>SKUs for a given product id.</summary>
    public static string ProductSkus(string productId) => $"/api/catalog/products/{productId}/skus";

    /// <summary>Billable SKUs for a given SKU group id.</summary>
    public static string BillableSkus(string skuGroupId) => $"/api/catalog/sku-groups/{skuGroupId}/billable-skus";

    // Customer management.
    public const string Customers = "/api/customers";

    /// <summary>A single customer by id.</summary>
    public static string Customer(string customerId) => $"/api/customers/{customerId}";

    /// <summary>Purchasable SKUs for a customer within a product.</summary>
    public static string CustomerPurchasableSkus(string customerId, string productId) =>
        $"/api/customers/{customerId}/purchasable-skus?productId={productId}";

    /// <summary>Purchasable offers for a customer for a specific SKU.</summary>
    public static string CustomerPurchasableOffers(string customerId, string productId, string skuId) =>
        $"/api/customers/{customerId}/purchasable-offers?productId={productId}&skuId={skuId}";
}
