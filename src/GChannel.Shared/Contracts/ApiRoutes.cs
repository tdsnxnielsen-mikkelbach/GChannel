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

    // Channel partner links (§5 — distributor / n-tier). A distributor links downstream resellers
    // ("channel partners") to their account; customers can then be owned by a partner.

    /// <summary>Lists the reseller account's channel partner links.</summary>
    public const string ChannelPartnerLinks = "/api/channel-partner-links";

    /// <summary>A single channel partner link by id.</summary>
    public static string ChannelPartnerLink(string linkId) => $"/api/channel-partner-links/{linkId}";

    /// <summary>Updates a channel partner link's state.</summary>
    public static string ChannelPartnerLinkState(string linkId) => $"/api/channel-partner-links/{linkId}/state";

    /// <summary>Lists the customers owned by a channel partner link.</summary>
    public static string ChannelPartnerCustomers(string linkId) => $"/api/channel-partner-links/{linkId}/customers";

    /// <summary>A single customer owned by a channel partner link (get/update/delete).</summary>
    public static string ChannelPartnerCustomer(string linkId, string customerId) =>
        $"/api/channel-partner-links/{linkId}/customers/{customerId}";

    /// <summary>Imports a pre-existing Cloud Identity customer under a channel partner link.</summary>
    public static string ChannelPartnerCustomerImport(string linkId) =>
        $"/api/channel-partner-links/{linkId}/customers/import";

    // §10 read-model estate views: server-side paged/sorted/filtered queries against SQL, plus a
    // "refresh now" action that prioritises a link/customer to the front of the sync queue.

    /// <summary>Paged/sorted/filtered customers from the read-model.</summary>
    public const string EstateCustomers = "/api/estate/customers";

    /// <summary>Paged/sorted/filtered resellers (channel-partner-links) from the read-model.</summary>
    public const string EstateResellers = "/api/estate/resellers";

    /// <summary>Paged/sorted/filtered entitlements (subscriptions) from the read-model.</summary>
    public const string EstateEntitlements = "/api/estate/entitlements";

    /// <summary>Prioritise a reseller link to the front of the sync queue.</summary>
    public static string EstateResyncLink(string linkId) => $"/api/estate/resellers/{linkId}/resync";

    /// <summary>Prioritise a customer to the front of the sync queue.</summary>
    public static string EstateResyncCustomer(string customerId) => $"/api/estate/customers/{customerId}/resync";

    // Repricing / rebilling margin (§6). Configs hang off a customer (the reseller's margin on that
    // customer's bill) or a channel partner link (a distributor's margin on a downstream reseller's
    // bill). They return the config resource directly (not long-running operations).

    /// <summary>Lists a customer's repricing configs.</summary>
    public static string CustomerRepricingConfigs(string customerId) =>
        $"/api/customers/{customerId}/repricing-configs";

    /// <summary>A single customer repricing config by id.</summary>
    public static string CustomerRepricingConfig(string customerId, string configId) =>
        $"/api/customers/{customerId}/repricing-configs/{configId}";

    /// <summary>Lists a channel partner link's repricing configs.</summary>
    public static string ChannelPartnerRepricingConfigs(string linkId) =>
        $"/api/channel-partner-links/{linkId}/repricing-configs";

    /// <summary>A single channel partner repricing config by id.</summary>
    public static string ChannelPartnerRepricingConfig(string linkId, string configId) =>
        $"/api/channel-partner-links/{linkId}/repricing-configs/{configId}";

    // Eventing & operations (§7). Long-running operations expose the async status of mutating calls;
    // Pub/Sub subscriber management plus a feed of received Channel change notifications close the
    // loop so the UI reacts to entitlement/customer events instead of polling.

    /// <summary>Lists recent long-running operations.</summary>
    public const string Operations = "/api/operations";

    /// <summary>A single long-running operation by id (the segment after "operations/").</summary>
    public static string Operation(string operationId) => $"/api/operations/{operationId}";

    /// <summary>Requests cancellation of a long-running operation.</summary>
    public static string OperationCancel(string operationId) => $"/api/operations/{operationId}/cancel";

    /// <summary>Recent Channel change notifications received from Pub/Sub.</summary>
    public const string Notifications = "/api/notifications";

    /// <summary>The Pub/Sub subscriber registration (topic + registered service accounts).</summary>
    public const string PubSubSubscribers = "/api/notifications/subscribers";

    /// <summary>A single Pub/Sub subscriber registration (by service-account email).</summary>
    public static string PubSubSubscriber(string serviceAccount) =>
        $"/api/notifications/subscribers/{serviceAccount}";
}
