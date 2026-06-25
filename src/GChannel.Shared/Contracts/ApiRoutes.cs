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

    /// <summary>Aggregated home-dashboard figures derived from customers + entitlements.</summary>
    public const string DashboardSummary = "/api/dashboard/summary";

    /// <summary>Cheap first-phase dashboard figures (customer count + onboarded-over-time).</summary>
    public const string DashboardOverview = "/api/dashboard/overview";

    /// <summary>Freshness/health of the background dashboard refresher (last run + in-progress flag).</summary>
    public const string DashboardStatus = "/api/dashboard/status";

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

    // Entitlement lifecycle (the core selling flow). All entitlement routes are nested under a customer.

    /// <summary>Lists a customer's entitlements.</summary>
    public static string Entitlements(string customerId) => $"/api/customers/{customerId}/entitlements";

    /// <summary>A single entitlement by id.</summary>
    public static string Entitlement(string customerId, string entitlementId) =>
        $"/api/customers/{customerId}/entitlements/{entitlementId}";

    /// <summary>Change history for an entitlement.</summary>
    public static string EntitlementChanges(string customerId, string entitlementId) =>
        $"/api/customers/{customerId}/entitlements/{entitlementId}/changes";

    /// <summary>The Offer currently backing an entitlement (<c>lookupOffer</c>).</summary>
    public static string EntitlementOffer(string customerId, string entitlementId) =>
        $"/api/customers/{customerId}/entitlements/{entitlementId}/offer";

    /// <summary>Change the Offer of an entitlement.</summary>
    public static string EntitlementChangeOffer(string customerId, string entitlementId) =>
        $"/api/customers/{customerId}/entitlements/{entitlementId}/change-offer";

    /// <summary>Change the parameters (e.g. seats) of an entitlement.</summary>
    public static string EntitlementChangeParameters(string customerId, string entitlementId) =>
        $"/api/customers/{customerId}/entitlements/{entitlementId}/change-parameters";

    /// <summary>Change the renewal settings of an entitlement.</summary>
    public static string EntitlementChangeRenewal(string customerId, string entitlementId) =>
        $"/api/customers/{customerId}/entitlements/{entitlementId}/change-renewal";

    /// <summary>Activate a suspended entitlement.</summary>
    public static string EntitlementActivate(string customerId, string entitlementId) =>
        $"/api/customers/{customerId}/entitlements/{entitlementId}/activate";

    /// <summary>Suspend an entitlement.</summary>
    public static string EntitlementSuspend(string customerId, string entitlementId) =>
        $"/api/customers/{customerId}/entitlements/{entitlementId}/suspend";

    /// <summary>Cancel an entitlement.</summary>
    public static string EntitlementCancel(string customerId, string entitlementId) =>
        $"/api/customers/{customerId}/entitlements/{entitlementId}/cancel";

    /// <summary>Start paid service for a trial entitlement.</summary>
    public static string EntitlementStartPaid(string customerId, string entitlementId) =>
        $"/api/customers/{customerId}/entitlements/{entitlementId}/start-paid-service";

    // Transfers (§4). Bring a customer's existing entitlements into this reseller's account, or hand
    // them back to Google. Nested under a customer, mirroring the entitlement lifecycle routes.

    /// <summary>Lists the SKUs a customer currently holds that could be transferred in.</summary>
    public static string TransferableSkus(string customerId) =>
        $"/api/customers/{customerId}/transferable-skus";

    /// <summary>Lists the offers a customer is eligible to transfer in for a SKU.</summary>
    public static string TransferableOffers(string customerId, string productId, string skuId) =>
        $"/api/customers/{customerId}/transferable-offers?productId={productId}&skuId={skuId}";

    /// <summary>Transfers entitlements to this reseller.</summary>
    public static string TransferEntitlements(string customerId) =>
        $"/api/customers/{customerId}/transfer-entitlements";

    /// <summary>Transfers entitlements back to Google (direct) billing.</summary>
    public static string TransferEntitlementsToGoogle(string customerId) =>
        $"/api/customers/{customerId}/transfer-entitlements-to-google";
}
