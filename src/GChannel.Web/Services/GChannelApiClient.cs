using System.Net.Http.Headers;
using System.Net.Http.Json;
using GChannel.Shared.Contracts;
using Microsoft.AspNetCore.Components.Authorization;

namespace GChannel.Web.Services;

/// <summary>
/// Typed client the UI uses to talk to the API service. It transparently attaches the
/// signed-in user's Google access token so Razor components never touch tokens or REST paths.
/// </summary>
public sealed class GChannelApiClient(
    HttpClient http,
    AuthenticationStateProvider authState,
    GoogleTokenProvider tokenProvider)
{
    public async Task<CheckCloudIdentityResult?> CheckCloudIdentityAsync(
        CheckCloudIdentityRequest request,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var route = forceRefresh
            ? $"{ApiRoutes.CheckCloudIdentity}?refresh=true"
            : ApiRoutes.CheckCloudIdentity;

        using var message = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(request)
        };

        await AttachGoogleTokenAsync(message);

        using var response = await http.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CheckCloudIdentityResult>(cancellationToken);
    }

    /// <summary>Lists recently checked domains (latest result per domain).</summary>
    public Task<IdentityCheckHistoryResult?> GetIdentityCheckHistoryAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IdentityCheckHistoryResult>(ApiRoutes.IdentityCheckHistory, cancellationToken);

    /// <summary>Gets the aggregated home-dashboard figures (customers + entitlements).</summary>
    public Task<DashboardSummary?> GetDashboardSummaryAsync(CancellationToken cancellationToken = default) =>
        GetAsync<DashboardSummary>(ApiRoutes.DashboardSummary, cancellationToken);

    /// <summary>Gets the cheap first-phase dashboard figures (customer count + onboarded-over-time).</summary>
    public Task<DashboardOverview?> GetDashboardOverviewAsync(CancellationToken cancellationToken = default) =>
        GetAsync<DashboardOverview>(ApiRoutes.DashboardOverview, cancellationToken);

    /// <summary>Gets the freshness/health of the background dashboard refresher (last run + in-progress flag).</summary>
    public Task<DashboardRefreshStatus?> GetDashboardStatusAsync(CancellationToken cancellationToken = default) =>
        GetAsync<DashboardRefreshStatus>(ApiRoutes.DashboardStatus, cancellationToken);

    /// <summary>Lists the products the reseller is authorized to sell.</summary>
    public Task<CatalogProductsResult?> ListProductsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<CatalogProductsResult>(ApiRoutes.Products, cancellationToken);

    /// <summary>Lists the SKUs for a product.</summary>
    public Task<CatalogSkusResult?> ListSkusAsync(string productId, CancellationToken cancellationToken = default) =>
        GetAsync<CatalogSkusResult>(ApiRoutes.ProductSkus(productId), cancellationToken);

    /// <summary>Lists the offers the reseller can sell.</summary>
    public Task<CatalogOffersResult?> ListOffersAsync(CancellationToken cancellationToken = default) =>
        GetAsync<CatalogOffersResult>(ApiRoutes.Offers, cancellationToken);

    /// <summary>Lists the rebilling-supported SKU groups.</summary>
    public Task<CatalogSkuGroupsResult?> ListSkuGroupsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<CatalogSkuGroupsResult>(ApiRoutes.SkuGroups, cancellationToken);

    /// <summary>Lists the billable SKUs in a SKU group.</summary>
    public Task<CatalogBillableSkusResult?> ListBillableSkusAsync(string skuGroupId, CancellationToken cancellationToken = default) =>
        GetAsync<CatalogBillableSkusResult>(ApiRoutes.BillableSkus(skuGroupId), cancellationToken);

    /// <summary>Lists the reseller's customers.</summary>
    public Task<CustomersResult?> ListCustomersAsync(CancellationToken cancellationToken = default) =>
        GetAsync<CustomersResult>(ApiRoutes.Customers, cancellationToken);

    /// <summary>Gets a single customer.</summary>
    public Task<Customer?> GetCustomerAsync(string customerId, CancellationToken cancellationToken = default) =>
        GetAsync<Customer>(ApiRoutes.Customer(customerId), cancellationToken);

    /// <summary>Creates a customer and returns the created resource.</summary>
    public Task<Customer?> CreateCustomerAsync(SaveCustomerRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<Customer>(HttpMethod.Post, ApiRoutes.Customers, request, cancellationToken);

    /// <summary>Updates a customer and returns the updated resource.</summary>
    public Task<Customer?> UpdateCustomerAsync(string customerId, SaveCustomerRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<Customer>(HttpMethod.Put, ApiRoutes.Customer(customerId), request, cancellationToken);

    /// <summary>Deletes a customer.</summary>
    public async Task DeleteCustomerAsync(string customerId, CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Delete, ApiRoutes.Customer(customerId));
        await AttachGoogleTokenAsync(message);

        using var response = await http.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Imports a pre-existing Cloud Identity customer (pre-transfer); returns the customer.</summary>
    public Task<Customer?> ImportCustomerAsync(ImportCustomerRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<Customer>(HttpMethod.Post, ApiRoutes.CustomerImport, request, cancellationToken);

    /// <summary>Provisions a new Cloud Identity for a customer; returns the tracking operation.</summary>
    public Task<ChannelOperation?> ProvisionCloudIdentityAsync(string customerId, ProvisionCloudIdentityRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<ChannelOperation>(HttpMethod.Post, ApiRoutes.CustomerProvisionCloudIdentity(customerId), request, cancellationToken);

    /// <summary>Lists the SKUs a customer is eligible to purchase within a product.</summary>
    public Task<PurchasableSkusResult?> ListPurchasableSkusAsync(string customerId, string productId, CancellationToken cancellationToken = default) =>
        GetAsync<PurchasableSkusResult>(ApiRoutes.CustomerPurchasableSkus(customerId, productId), cancellationToken);

    /// <summary>Lists the offers a customer is eligible to purchase for a SKU.</summary>
    public Task<PurchasableOffersResult?> ListPurchasableOffersAsync(string customerId, string productId, string skuId, CancellationToken cancellationToken = default) =>
        GetAsync<PurchasableOffersResult>(ApiRoutes.CustomerPurchasableOffers(customerId, productId, skuId), cancellationToken);

    /// <summary>Queries which billing accounts a customer may use for the given SKUs (GCP / n-tier billing).</summary>
    public Task<EligibleBillingAccountsResult?> QueryEligibleBillingAccountsAsync(string customerId, IReadOnlyList<string> skus, CancellationToken cancellationToken = default)
    {
        var query = string.Join("&", skus.Select(s => $"skus={Uri.EscapeDataString(s)}"));
        var route = ApiRoutes.CustomerEligibleBillingAccounts(customerId) + (query.Length > 0 ? $"?{query}" : string.Empty);
        return GetAsync<EligibleBillingAccountsResult>(route, cancellationToken);
    }

    /// <summary>Lists a customer's entitlements.</summary>
    public Task<EntitlementsResult?> ListEntitlementsAsync(string customerId, CancellationToken cancellationToken = default) =>
        GetAsync<EntitlementsResult>(ApiRoutes.Entitlements(customerId), cancellationToken);

    /// <summary>Gets a single entitlement.</summary>
    public Task<Entitlement?> GetEntitlementAsync(string customerId, string entitlementId, CancellationToken cancellationToken = default) =>
        GetAsync<Entitlement>(ApiRoutes.Entitlement(customerId, entitlementId), cancellationToken);

    /// <summary>Lists an entitlement's change history.</summary>
    public Task<EntitlementChangesResult?> ListEntitlementChangesAsync(string customerId, string entitlementId, CancellationToken cancellationToken = default) =>
        GetAsync<EntitlementChangesResult>(ApiRoutes.EntitlementChanges(customerId, entitlementId), cancellationToken);

    /// <summary>Looks up the Offer backing an entitlement.</summary>
    public Task<CatalogOffer?> LookupEntitlementOfferAsync(string customerId, string entitlementId, CancellationToken cancellationToken = default) =>
        GetAsync<CatalogOffer>(ApiRoutes.EntitlementOffer(customerId, entitlementId), cancellationToken);

    /// <summary>Purchases (creates) an entitlement.</summary>
    public Task<EntitlementOperation?> PurchaseEntitlementAsync(string customerId, PurchaseEntitlementRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<EntitlementOperation>(HttpMethod.Post, ApiRoutes.Entitlements(customerId), request, cancellationToken);

    /// <summary>Changes the Offer of an entitlement.</summary>
    public Task<EntitlementOperation?> ChangeEntitlementOfferAsync(string customerId, string entitlementId, ChangeOfferRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<EntitlementOperation>(HttpMethod.Post, ApiRoutes.EntitlementChangeOffer(customerId, entitlementId), request, cancellationToken);

    /// <summary>Changes the parameters (e.g. seats) of an entitlement.</summary>
    public Task<EntitlementOperation?> ChangeEntitlementParametersAsync(string customerId, string entitlementId, ChangeParametersRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<EntitlementOperation>(HttpMethod.Post, ApiRoutes.EntitlementChangeParameters(customerId, entitlementId), request, cancellationToken);

    /// <summary>Changes the renewal settings of an entitlement.</summary>
    public Task<EntitlementOperation?> ChangeEntitlementRenewalAsync(string customerId, string entitlementId, ChangeRenewalSettingsRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<EntitlementOperation>(HttpMethod.Post, ApiRoutes.EntitlementChangeRenewal(customerId, entitlementId), request, cancellationToken);

    /// <summary>Activates a suspended entitlement.</summary>
    public Task<EntitlementOperation?> ActivateEntitlementAsync(string customerId, string entitlementId, CancellationToken cancellationToken = default) =>
        SendAsync<EntitlementOperation>(HttpMethod.Post, ApiRoutes.EntitlementActivate(customerId, entitlementId), EmptyBody, cancellationToken);

    /// <summary>Suspends an entitlement.</summary>
    public Task<EntitlementOperation?> SuspendEntitlementAsync(string customerId, string entitlementId, CancellationToken cancellationToken = default) =>
        SendAsync<EntitlementOperation>(HttpMethod.Post, ApiRoutes.EntitlementSuspend(customerId, entitlementId), EmptyBody, cancellationToken);

    /// <summary>Cancels an entitlement.</summary>
    public Task<EntitlementOperation?> CancelEntitlementAsync(string customerId, string entitlementId, CancellationToken cancellationToken = default) =>
        SendAsync<EntitlementOperation>(HttpMethod.Post, ApiRoutes.EntitlementCancel(customerId, entitlementId), EmptyBody, cancellationToken);

    /// <summary>Starts paid service for a trial entitlement.</summary>
    public Task<EntitlementOperation?> StartPaidServiceAsync(string customerId, string entitlementId, CancellationToken cancellationToken = default) =>
        SendAsync<EntitlementOperation>(HttpMethod.Post, ApiRoutes.EntitlementStartPaid(customerId, entitlementId), EmptyBody, cancellationToken);

    /// <summary>Lists the SKUs a customer currently holds that could be transferred in.</summary>
    public Task<TransferableSkusResult?> ListTransferableSkusAsync(string customerId, CancellationToken cancellationToken = default) =>
        GetAsync<TransferableSkusResult>(ApiRoutes.TransferableSkus(customerId), cancellationToken);

    /// <summary>Lists the offers a customer is eligible to transfer in for a SKU.</summary>
    public Task<TransferableOffersResult?> ListTransferableOffersAsync(string customerId, string productId, string skuId, CancellationToken cancellationToken = default) =>
        GetAsync<TransferableOffersResult>(ApiRoutes.TransferableOffers(customerId, productId, skuId), cancellationToken);

    /// <summary>Transfers entitlements to this reseller.</summary>
    public Task<EntitlementOperation?> TransferEntitlementsAsync(string customerId, TransferEntitlementsRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<EntitlementOperation>(HttpMethod.Post, ApiRoutes.TransferEntitlements(customerId), request, cancellationToken);

    /// <summary>Transfers entitlements back to Google (direct) billing.</summary>
    public Task<EntitlementOperation?> TransferEntitlementsToGoogleAsync(string customerId, TransferEntitlementsToGoogleRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<EntitlementOperation>(HttpMethod.Post, ApiRoutes.TransferEntitlementsToGoogle(customerId), request, cancellationToken);

    /// <summary>Lists the reseller account's channel partner links.</summary>
    public Task<ChannelPartnerLinksResult?> ListChannelPartnerLinksAsync(CancellationToken cancellationToken = default) =>
        GetAsync<ChannelPartnerLinksResult>(ApiRoutes.ChannelPartnerLinks, cancellationToken);

    /// <summary>Gets a single channel partner link.</summary>
    public Task<ChannelPartnerLink?> GetChannelPartnerLinkAsync(string linkId, CancellationToken cancellationToken = default) =>
        GetAsync<ChannelPartnerLink>(ApiRoutes.ChannelPartnerLink(linkId), cancellationToken);

    /// <summary>Lists the customers owned by a channel partner link.</summary>
    public Task<CustomersResult?> ListChannelPartnerCustomersAsync(string linkId, CancellationToken cancellationToken = default) =>
        GetAsync<CustomersResult>(ApiRoutes.ChannelPartnerCustomers(linkId), cancellationToken);

    /// <summary>Gets a single customer owned by a channel partner link.</summary>
    public Task<Customer?> GetChannelPartnerCustomerAsync(string linkId, string customerId, CancellationToken cancellationToken = default) =>
        GetAsync<Customer>(ApiRoutes.ChannelPartnerCustomer(linkId, customerId), cancellationToken);

    /// <summary>Creates a customer under a channel partner link and returns the created resource.</summary>
    public Task<Customer?> CreateChannelPartnerCustomerAsync(string linkId, SaveCustomerRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<Customer>(HttpMethod.Post, ApiRoutes.ChannelPartnerCustomers(linkId), request, cancellationToken);

    /// <summary>Updates a customer owned by a channel partner link and returns the updated resource.</summary>
    public Task<Customer?> UpdateChannelPartnerCustomerAsync(string linkId, string customerId, SaveCustomerRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<Customer>(HttpMethod.Put, ApiRoutes.ChannelPartnerCustomer(linkId, customerId), request, cancellationToken);

    /// <summary>Deletes a customer owned by a channel partner link.</summary>
    public Task DeleteChannelPartnerCustomerAsync(string linkId, string customerId, CancellationToken cancellationToken = default) =>
        DeleteAsync(ApiRoutes.ChannelPartnerCustomer(linkId, customerId), cancellationToken);

    /// <summary>Imports a pre-existing Cloud Identity customer under a channel partner link.</summary>
    public Task<Customer?> ImportChannelPartnerCustomerAsync(string linkId, ImportCustomerRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<Customer>(HttpMethod.Post, ApiRoutes.ChannelPartnerCustomerImport(linkId), request, cancellationToken);

    // §10 read-model estate views: server-side paged/sorted/filtered queries against SQL.

    /// <summary>Pages/sorts/filters customers from the read-model.</summary>
    public Task<PagedEstateResult<EstateCustomer>?> ListEstateCustomersAsync(int page, int pageSize, string? sort, bool desc, string? search, string? linkId, CancellationToken cancellationToken = default) =>
        GetAsync<PagedEstateResult<EstateCustomer>>(
            $"{ApiRoutes.EstateCustomers}?page={page}&pageSize={pageSize}&sort={Uri.EscapeDataString(sort ?? "")}&desc={desc}&search={Uri.EscapeDataString(search ?? "")}&linkId={Uri.EscapeDataString(linkId ?? "")}",
            cancellationToken);

    /// <summary>Pages/sorts/filters resellers (channel-partner-links) from the read-model.</summary>
    public Task<PagedEstateResult<EstateReseller>?> ListEstateResellersAsync(int page, int pageSize, string? sort, bool desc, string? search, string? state, CancellationToken cancellationToken = default) =>
        GetAsync<PagedEstateResult<EstateReseller>>(
            $"{ApiRoutes.EstateResellers}?page={page}&pageSize={pageSize}&sort={Uri.EscapeDataString(sort ?? "")}&desc={desc}&search={Uri.EscapeDataString(search ?? "")}&state={Uri.EscapeDataString(state ?? "")}",
            cancellationToken);

    /// <summary>Estimated estate value (wholesale/revenue/margin) across a reseller's customers.</summary>
    public Task<ResellerEstateValue?> GetResellerValueAsync(string linkId, CancellationToken cancellationToken = default) =>
        GetAsync<ResellerEstateValue>(ApiRoutes.EstateResellerValue(linkId), cancellationToken);

    /// <summary>Estimated monthly value (wholesale/revenue/margin) for a single customer from the read-model.</summary>
    public Task<CustomerEstateValue?> GetCustomerValueAsync(string customerId, CancellationToken cancellationToken = default) =>
        GetAsync<CustomerEstateValue>(ApiRoutes.EstateCustomerValue(customerId), cancellationToken);

    /// <summary>Pages/sorts/filters estate-wide entitlements (subscriptions) from the read-model.</summary>
    public Task<PagedEstateResult<EstateEntitlement>?> ListEstateEntitlementsAsync(int page, int pageSize, string? sort, bool desc, string? search, string? state, string? scope, CancellationToken cancellationToken = default) =>
        GetAsync<PagedEstateResult<EstateEntitlement>>(
            $"{ApiRoutes.EstateEntitlements}?page={page}&pageSize={pageSize}&sort={Uri.EscapeDataString(sort ?? "")}&desc={desc}&search={Uri.EscapeDataString(search ?? "")}&state={Uri.EscapeDataString(state ?? "")}&scope={Uri.EscapeDataString(scope ?? "")}",
            cancellationToken);

    /// <summary>Prioritises a reseller link to the front of the sync queue.</summary>
    public Task ResyncLinkAsync(string linkId, CancellationToken cancellationToken = default) =>
        PostNoResponseAsync(ApiRoutes.EstateResyncLink(linkId), cancellationToken);

    /// <summary>Prioritises a customer to the front of the sync queue.</summary>
    public Task ResyncCustomerAsync(string customerId, CancellationToken cancellationToken = default) =>
        PostNoResponseAsync(ApiRoutes.EstateResyncCustomer(customerId), cancellationToken);

    /// <summary>Invites a downstream reseller by creating a channel partner link.</summary>
    public Task<ChannelPartnerLink?> CreateChannelPartnerLinkAsync(CreateChannelPartnerLinkRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<ChannelPartnerLink>(HttpMethod.Post, ApiRoutes.ChannelPartnerLinks, request, cancellationToken);

    /// <summary>Updates a channel partner link's state.</summary>
    public Task<ChannelPartnerLink?> UpdateChannelPartnerLinkStateAsync(string linkId, UpdateChannelPartnerLinkRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<ChannelPartnerLink>(HttpMethod.Put, ApiRoutes.ChannelPartnerLinkState(linkId), request, cancellationToken);

    /// <summary>Lists a customer's repricing (rebilling-margin) configs.</summary>
    public Task<RepricingConfigsResult?> ListCustomerRepricingConfigsAsync(string customerId, CancellationToken cancellationToken = default) =>
        GetAsync<RepricingConfigsResult>(ApiRoutes.CustomerRepricingConfigs(customerId), cancellationToken);

    /// <summary>Creates a customer repricing config.</summary>
    public Task<RepricingConfig?> CreateCustomerRepricingConfigAsync(string customerId, SaveRepricingConfigRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<RepricingConfig>(HttpMethod.Post, ApiRoutes.CustomerRepricingConfigs(customerId), request, cancellationToken);

    /// <summary>Updates a customer repricing config.</summary>
    public Task<RepricingConfig?> UpdateCustomerRepricingConfigAsync(string customerId, string configId, SaveRepricingConfigRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<RepricingConfig>(HttpMethod.Put, ApiRoutes.CustomerRepricingConfig(customerId, configId), request, cancellationToken);

    /// <summary>Deletes a customer repricing config.</summary>
    public Task DeleteCustomerRepricingConfigAsync(string customerId, string configId, CancellationToken cancellationToken = default) =>
        DeleteAsync(ApiRoutes.CustomerRepricingConfig(customerId, configId), cancellationToken);

    /// <summary>Lists a channel partner link's repricing (rebilling-margin) configs.</summary>
    public Task<RepricingConfigsResult?> ListChannelPartnerRepricingConfigsAsync(string linkId, CancellationToken cancellationToken = default) =>
        GetAsync<RepricingConfigsResult>(ApiRoutes.ChannelPartnerRepricingConfigs(linkId), cancellationToken);

    /// <summary>Creates a channel partner repricing config.</summary>
    public Task<RepricingConfig?> CreateChannelPartnerRepricingConfigAsync(string linkId, SaveRepricingConfigRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<RepricingConfig>(HttpMethod.Post, ApiRoutes.ChannelPartnerRepricingConfigs(linkId), request, cancellationToken);

    /// <summary>Updates a channel partner repricing config.</summary>
    public Task<RepricingConfig?> UpdateChannelPartnerRepricingConfigAsync(string linkId, string configId, SaveRepricingConfigRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<RepricingConfig>(HttpMethod.Put, ApiRoutes.ChannelPartnerRepricingConfig(linkId, configId), request, cancellationToken);

    /// <summary>Deletes a channel partner repricing config.</summary>
    public Task DeleteChannelPartnerRepricingConfigAsync(string linkId, string configId, CancellationToken cancellationToken = default) =>
        DeleteAsync(ApiRoutes.ChannelPartnerRepricingConfig(linkId, configId), cancellationToken);

    // Eventing & operations (§7).

    /// <summary>Lists recent long-running operations.</summary>
    public Task<ChannelOperationsResult?> ListOperationsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<ChannelOperationsResult>(ApiRoutes.Operations, cancellationToken);

    /// <summary>Gets a single long-running operation by id (poll until done).</summary>
    public Task<ChannelOperation?> GetOperationAsync(string operationId, CancellationToken cancellationToken = default) =>
        GetAsync<ChannelOperation>(ApiRoutes.Operation(operationId), cancellationToken);

    /// <summary>Requests cancellation of a long-running operation.</summary>
    public Task<ChannelOperation?> CancelOperationAsync(string operationId, CancellationToken cancellationToken = default) =>
        SendAsync<ChannelOperation>(HttpMethod.Post, ApiRoutes.OperationCancel(operationId), EmptyBody, cancellationToken);

    /// <summary>Lists recent Channel change notifications received from Pub/Sub.</summary>
    public Task<ChannelNotificationsResult?> ListNotificationsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<ChannelNotificationsResult>(ApiRoutes.Notifications, cancellationToken);

    /// <summary>Lists the account's Pub/Sub subscriber registration (topic + service accounts).</summary>
    public Task<SubscriberRegistration?> ListSubscribersAsync(CancellationToken cancellationToken = default) =>
        GetAsync<SubscriberRegistration>(ApiRoutes.PubSubSubscribers, cancellationToken);

    /// <summary>Registers a service account as a Pub/Sub subscriber.</summary>
    public Task<SubscriberRegistration?> RegisterSubscriberAsync(RegisterSubscriberRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<SubscriberRegistration>(HttpMethod.Post, ApiRoutes.PubSubSubscribers, request, cancellationToken);

    /// <summary>Unregisters a Pub/Sub subscriber service account.</summary>
    public Task<SubscriberRegistration?> UnregisterSubscriberAsync(string serviceAccount, CancellationToken cancellationToken = default) =>
        SendAsync<SubscriberRegistration>(HttpMethod.Delete, ApiRoutes.PubSubSubscriber(serviceAccount), EmptyBody, cancellationToken);

    /// <summary>Lists the reseller's Cloud Billing accounts + sub-accounts (discovered or configured).</summary>
    public Task<BillingAccountsResult?> ListBillingAccountsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<BillingAccountsResult>(ApiRoutes.BillingAccounts, cancellationToken);

    /// <summary>Lists budgets for a billing account.</summary>
    public Task<BudgetsResult?> ListBudgetsAsync(string billingAccountId, CancellationToken cancellationToken = default) =>
        GetAsync<BudgetsResult>(ApiRoutes.BillingBudgets(billingAccountId), cancellationToken);

    /// <summary>Creates (no budget id) or updates (budget id set) a budget.</summary>
    public Task<BudgetInfo?> SaveBudgetAsync(SaveBudgetRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<BudgetInfo>(HttpMethod.Post, ApiRoutes.BillingBudgetSave, request, cancellationToken);

    /// <summary>Deletes a budget.</summary>
    public Task DeleteBudgetAsync(string billingAccountId, string budgetId, CancellationToken cancellationToken = default) =>
        DeleteAsync(ApiRoutes.BillingBudget(billingAccountId, budgetId), cancellationToken);

    /// <summary>Shared empty JSON body for state-change POSTs that carry no payload.</summary>
    private static readonly object EmptyBody = new();

    private async Task DeleteAsync(string route, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Delete, route);
        await AttachGoogleTokenAsync(message);

        using var response = await http.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<T?> GetAsync<T>(string route, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, route);
        await AttachGoogleTokenAsync(message);

        using var response = await http.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string route, object body, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(method, route)
        {
            Content = JsonContent.Create(body)
        };
        await AttachGoogleTokenAsync(message);

        using var response = await http.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    /// <summary>POSTs a state-change request that returns no response body (e.g. 202 Accepted) — does not deserialize.</summary>
    private async Task PostNoResponseAsync(string route, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, route);
        await AttachGoogleTokenAsync(message);

        using var response = await http.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task AttachGoogleTokenAsync(HttpRequestMessage message)
    {
        var state = await authState.GetAuthenticationStateAsync();
        var token = await tokenProvider.GetAccessTokenAsync(state.User);
        if (!string.IsNullOrEmpty(token))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var email = state.User.Identity?.Name;
        if (!string.IsNullOrEmpty(email))
        {
            message.Headers.Add("X-User-Email", email);
        }
    }
}
