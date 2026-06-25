using GChannel.ApiService.Configuration;
using GChannel.Shared.Contracts;
using Google.Apis.Cloudchannel.v1;
using Google.Apis.Cloudchannel.v1.Data;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Util;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net;

namespace GChannel.ApiService.Services;

/// <summary>
/// Abstraction over the Google Cloud Channel API. The UI talks to these methods only and
/// never sees the underlying REST shapes.
/// </summary>
public interface IGoogleChannelClient
{
    Task<CheckCloudIdentityResult> CheckCloudIdentityAsync(CheckCloudIdentityRequest request, CancellationToken cancellationToken);

    /// <summary>Lists the products the reseller is authorized to sell (<c>products.list</c>).</summary>
    Task<CatalogProductsResult> ListProductsAsync(CancellationToken cancellationToken);

    /// <summary>Lists the SKUs for a product (<c>products.skus.list</c>).</summary>
    Task<CatalogSkusResult> ListSkusAsync(string productId, CancellationToken cancellationToken);

    /// <summary>Lists the offers the reseller can sell (<c>accounts.offers.list</c>).</summary>
    Task<CatalogOffersResult> ListOffersAsync(CancellationToken cancellationToken);

    /// <summary>Lists the rebilling SKU groups (<c>accounts.skuGroups.list</c>).</summary>
    Task<CatalogSkuGroupsResult> ListSkuGroupsAsync(CancellationToken cancellationToken);

    /// <summary>Lists the billable SKUs in a SKU group (<c>accounts.skuGroups.billableSkus.list</c>).</summary>
    Task<CatalogBillableSkusResult> ListBillableSkusAsync(string skuGroupId, CancellationToken cancellationToken);

    /// <summary>Lists the reseller's customers (<c>accounts.customers.list</c>).</summary>
    Task<CustomersResult> ListCustomersAsync(CancellationToken cancellationToken);

    /// <summary>Gets a single customer (<c>accounts.customers.get</c>).</summary>
    Task<Customer> GetCustomerAsync(string customerId, CancellationToken cancellationToken);

    /// <summary>Creates a customer (<c>accounts.customers.create</c>).</summary>
    Task<Customer> CreateCustomerAsync(SaveCustomerRequest request, CancellationToken cancellationToken);

    /// <summary>Updates a customer (<c>accounts.customers.patch</c>).</summary>
    Task<Customer> UpdateCustomerAsync(SaveCustomerRequest request, CancellationToken cancellationToken);

    /// <summary>Deletes a customer (<c>accounts.customers.delete</c>).</summary>
    Task DeleteCustomerAsync(string customerId, CancellationToken cancellationToken);

    /// <summary>Lists a customer's purchasable SKUs for a product (<c>customers.listPurchasableSkus</c>).</summary>
    Task<PurchasableSkusResult> ListPurchasableSkusAsync(string customerId, string productId, CancellationToken cancellationToken);

    /// <summary>Lists a customer's purchasable offers for a SKU (<c>customers.listPurchasableOffers</c>).</summary>
    Task<PurchasableOffersResult> ListPurchasableOffersAsync(string customerId, string productId, string skuId, CancellationToken cancellationToken);

    /// <summary>Lists a customer's entitlements (<c>entitlements.list</c>).</summary>
    Task<EntitlementsResult> ListEntitlementsAsync(string customerId, CancellationToken cancellationToken);

    /// <summary>Gets a single entitlement (<c>entitlements.get</c>).</summary>
    Task<Entitlement> GetEntitlementAsync(string customerId, string entitlementId, CancellationToken cancellationToken);

    /// <summary>Lists an entitlement's change history (<c>entitlements.listEntitlementChanges</c>).</summary>
    Task<EntitlementChangesResult> ListEntitlementChangesAsync(string customerId, string entitlementId, CancellationToken cancellationToken);

    /// <summary>Looks up the Offer backing an entitlement (<c>entitlements.lookupOffer</c>).</summary>
    Task<CatalogOffer> LookupEntitlementOfferAsync(string customerId, string entitlementId, CancellationToken cancellationToken);

    /// <summary>Purchases (creates) an entitlement (<c>entitlements.create</c>).</summary>
    Task<EntitlementOperation> CreateEntitlementAsync(string customerId, PurchaseEntitlementRequest request, CancellationToken cancellationToken);

    /// <summary>Changes the Offer of an entitlement (<c>entitlements.changeOffer</c>).</summary>
    Task<EntitlementOperation> ChangeEntitlementOfferAsync(string customerId, string entitlementId, ChangeOfferRequest request, CancellationToken cancellationToken);

    /// <summary>Changes the parameters (e.g. seats) of an entitlement (<c>entitlements.changeParameters</c>).</summary>
    Task<EntitlementOperation> ChangeEntitlementParametersAsync(string customerId, string entitlementId, ChangeParametersRequest request, CancellationToken cancellationToken);

    /// <summary>Changes the renewal settings of an entitlement (<c>entitlements.changeRenewalSettings</c>).</summary>
    Task<EntitlementOperation> ChangeEntitlementRenewalAsync(string customerId, string entitlementId, ChangeRenewalSettingsRequest request, CancellationToken cancellationToken);

    /// <summary>Activates a suspended entitlement (<c>entitlements.activate</c>).</summary>
    Task<EntitlementOperation> ActivateEntitlementAsync(string customerId, string entitlementId, CancellationToken cancellationToken);

    /// <summary>Suspends an entitlement (<c>entitlements.suspend</c>).</summary>
    Task<EntitlementOperation> SuspendEntitlementAsync(string customerId, string entitlementId, CancellationToken cancellationToken);

    /// <summary>Cancels an entitlement (<c>entitlements.cancel</c>).</summary>
    Task<EntitlementOperation> CancelEntitlementAsync(string customerId, string entitlementId, CancellationToken cancellationToken);

    /// <summary>Starts paid service for a trial entitlement (<c>entitlements.startPaidService</c>).</summary>
    Task<EntitlementOperation> StartPaidServiceAsync(string customerId, string entitlementId, CancellationToken cancellationToken);

    /// <summary>Lists the SKUs a customer currently holds that could be transferred in (<c>accounts.listTransferableSkus</c>).</summary>
    Task<TransferableSkusResult> ListTransferableSkusAsync(string customerId, CancellationToken cancellationToken);

    /// <summary>Lists the offers a customer is eligible to transfer in for a SKU (<c>accounts.listTransferableOffers</c>).</summary>
    Task<TransferableOffersResult> ListTransferableOffersAsync(string customerId, string productId, string skuId, CancellationToken cancellationToken);

    /// <summary>Transfers entitlements to this reseller (<c>customers.transferEntitlements</c>).</summary>
    Task<EntitlementOperation> TransferEntitlementsAsync(string customerId, TransferEntitlementsRequest request, CancellationToken cancellationToken);

    /// <summary>Transfers entitlements back to Google (direct) billing (<c>customers.transferEntitlementsToGoogle</c>).</summary>
    Task<EntitlementOperation> TransferEntitlementsToGoogleAsync(string customerId, TransferEntitlementsToGoogleRequest request, CancellationToken cancellationToken);

    /// <summary>Lists the reseller account's channel partner links (<c>accounts.channelPartnerLinks.list</c>).</summary>
    Task<ChannelPartnerLinksResult> ListChannelPartnerLinksAsync(CancellationToken cancellationToken);

    /// <summary>Gets a single channel partner link (<c>accounts.channelPartnerLinks.get</c>).</summary>
    Task<ChannelPartnerLink> GetChannelPartnerLinkAsync(string linkId, CancellationToken cancellationToken);

    /// <summary>Invites a downstream reseller by creating a channel partner link (<c>accounts.channelPartnerLinks.create</c>).</summary>
    Task<ChannelPartnerLink> CreateChannelPartnerLinkAsync(CreateChannelPartnerLinkRequest request, CancellationToken cancellationToken);

    /// <summary>Updates a channel partner link's state (<c>accounts.channelPartnerLinks.patch</c>).</summary>
    Task<ChannelPartnerLink> UpdateChannelPartnerLinkStateAsync(string linkId, UpdateChannelPartnerLinkRequest request, CancellationToken cancellationToken);

    /// <summary>Lists the customers owned by a channel partner link (<c>accounts.channelPartnerLinks.customers.list</c>).</summary>
    Task<CustomersResult> ListChannelPartnerCustomersAsync(string linkId, CancellationToken cancellationToken);

    /// <summary>Lists a customer's repricing configs (<c>customers.customerRepricingConfigs.list</c>).</summary>
    Task<RepricingConfigsResult> ListCustomerRepricingConfigsAsync(string customerId, CancellationToken cancellationToken);

    /// <summary>Creates a customer repricing config (<c>customers.customerRepricingConfigs.create</c>).</summary>
    Task<RepricingConfig> CreateCustomerRepricingConfigAsync(string customerId, SaveRepricingConfigRequest request, CancellationToken cancellationToken);

    /// <summary>Updates a customer repricing config (<c>customers.customerRepricingConfigs.patch</c>).</summary>
    Task<RepricingConfig> UpdateCustomerRepricingConfigAsync(string customerId, string configId, SaveRepricingConfigRequest request, CancellationToken cancellationToken);

    /// <summary>Deletes a customer repricing config (<c>customers.customerRepricingConfigs.delete</c>).</summary>
    Task DeleteCustomerRepricingConfigAsync(string customerId, string configId, CancellationToken cancellationToken);

    /// <summary>Lists a channel partner link's repricing configs (<c>channelPartnerLinks.channelPartnerRepricingConfigs.list</c>).</summary>
    Task<RepricingConfigsResult> ListChannelPartnerRepricingConfigsAsync(string linkId, CancellationToken cancellationToken);

    /// <summary>Creates a channel partner repricing config (<c>channelPartnerLinks.channelPartnerRepricingConfigs.create</c>).</summary>
    Task<RepricingConfig> CreateChannelPartnerRepricingConfigAsync(string linkId, SaveRepricingConfigRequest request, CancellationToken cancellationToken);

    /// <summary>Updates a channel partner repricing config (<c>channelPartnerLinks.channelPartnerRepricingConfigs.patch</c>).</summary>
    Task<RepricingConfig> UpdateChannelPartnerRepricingConfigAsync(string linkId, string configId, SaveRepricingConfigRequest request, CancellationToken cancellationToken);

    /// <summary>Deletes a channel partner repricing config (<c>channelPartnerLinks.channelPartnerRepricingConfigs.delete</c>).</summary>
    Task DeleteChannelPartnerRepricingConfigAsync(string linkId, string configId, CancellationToken cancellationToken);

    /// <summary>Builds the aggregated home-dashboard figures from customers + entitlements.</summary>
    /// <param name="applyTimeBudget">
    /// When <see langword="true"/> (the default, used on the request path) the per-customer phase is
    /// capped by a time budget so the endpoint always responds within the HTTP timeout. The background
    /// refresher passes <see langword="false"/> to run unbounded and produce a complete result.
    /// </param>
    /// <param name="onPartial">
    /// Optional progress callback invoked with a running snapshot of the summary every
    /// <paramref name="partialEvery"/> customers, so the background refresher can publish partial
    /// results that the UI can poll while a long recompute is still in flight. Best-effort; failures
    /// are ignored.
    /// </param>
    /// <param name="partialEvery">
    /// How many customers to aggregate between <paramref name="onPartial"/> invocations. 0 (the default)
    /// disables progress reporting.
    /// </param>
    Task<DashboardSummary> GetDashboardSummaryAsync(
        CancellationToken cancellationToken,
        bool applyTimeBudget = true,
        Func<DashboardSummary, Task>? onPartial = null,
        int partialEvery = 0);

    /// <summary>
    /// Builds the cheap, quota-light first phase of the dashboard (customer count + onboarded-over-time)
    /// so the UI can render those immediately before the slower entitlement aggregation completes.
    /// </summary>
    Task<DashboardOverview> GetDashboardOverviewAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Counts the reseller's channel partner links (account-level, quota-light). Used by the
    /// background refresher to warm the dashboard's "Channel links" headline figure.
    /// </summary>
    Task<int> CountChannelPartnerLinksAsync(CancellationToken cancellationToken);

    // Eventing & operations (§7).

    /// <summary>Lists recent Cloud Channel long-running operations (<c>operations.list</c>).</summary>
    Task<ChannelOperationsResult> ListOperationsAsync(CancellationToken cancellationToken);

    /// <summary>Gets a single long-running operation by id; poll until done (<c>operations.get</c>).</summary>
    Task<ChannelOperation> GetOperationAsync(string operationId, CancellationToken cancellationToken);

    /// <summary>Requests cancellation of a long-running operation and returns its current state
    /// (<c>operations.cancel</c>).</summary>
    Task<ChannelOperation> CancelOperationAsync(string operationId, CancellationToken cancellationToken);

    /// <summary>Lists the account's Pub/Sub subscriber registration — topic + registered service
    /// accounts (<c>accounts.listSubscribers</c>).</summary>
    Task<SubscriberRegistration> ListSubscribersAsync(CancellationToken cancellationToken);

    /// <summary>Registers a service account as a Pub/Sub subscriber and returns the updated
    /// registration (<c>accounts.register</c>).</summary>
    Task<SubscriberRegistration> RegisterSubscriberAsync(string serviceAccount, CancellationToken cancellationToken);

    /// <summary>Unregisters a Pub/Sub subscriber service account and returns the updated registration
    /// (<c>accounts.unregister</c>).</summary>
    Task<SubscriberRegistration> UnregisterSubscriberAsync(string serviceAccount, CancellationToken cancellationToken);
}
