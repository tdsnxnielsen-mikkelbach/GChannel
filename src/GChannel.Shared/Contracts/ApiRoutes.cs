namespace GChannel.Shared.Contracts;

/// <summary>
/// Well-known route fragments shared between the Web client and the API service.
/// Keeps the abstraction in one place so UI code never references Google REST paths.
/// </summary>
public static class ApiRoutes
{
    public const string CheckCloudIdentity = "/api/accounts/check-cloud-identity";

    // Catalog (read-only) browsing.
    public const string Products = "/api/catalog/products";
    public const string Offers = "/api/catalog/offers";
    public const string SkuGroups = "/api/catalog/sku-groups";

    /// <summary>SKUs for a given product id.</summary>
    public static string ProductSkus(string productId) => $"/api/catalog/products/{productId}/skus";

    /// <summary>Billable SKUs for a given SKU group id.</summary>
    public static string BillableSkus(string skuGroupId) => $"/api/catalog/sku-groups/{skuGroupId}/billable-skus";
}
