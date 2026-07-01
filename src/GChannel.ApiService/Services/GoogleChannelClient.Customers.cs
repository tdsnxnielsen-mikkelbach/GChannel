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

// Customer management & purchasable SKUs — see IGoogleChannelClient for the contract documentation.
public sealed partial class GoogleChannelClient
{
    public async Task<CustomersResult> ListCustomersAsync(CancellationToken cancellationToken)
    {
        EnsureAccountConfigured();
        using var service = CreateService();

        var customers = new List<Customer>();
        string? pageToken = null;
        try
        {
            do
            {
                var request = service.Accounts.Customers.List(_options.AccountName);
                request.PageToken = pageToken;
                var response = await request.ExecuteAsync(cancellationToken);

                foreach (var customer in response.Customers ?? [])
                {
                    customers.Add(MapCustomer(customer));
                }

                pageToken = response.NextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken));
        }
        catch (OperationCanceledException ex)
        {
            // Benign: the caller (browser/Blazor circuit) went away mid-request. Logged at Debug
            // for traceability, then rethrown so behavior is unchanged.
            logger.LogDebug(ex, "ListCustomers canceled (client disconnected or navigated away).");
            throw;
        }

        return new CustomersResult { Customers = customers };
    }

    public async Task<Customer> GetCustomerAsync(string customerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        EnsureAccountConfigured();
        using var service = CreateService();

        var response = await service.Accounts.Customers
            .Get(CustomerName(customerId))
            .ExecuteAsync(cancellationToken);

        return MapCustomer(response);
    }

    public async Task<Customer> CreateCustomerAsync(SaveCustomerRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OrgDisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Domain);
        EnsureAccountConfigured();
        using var service = CreateService();

        var body = ToGoogleCustomer(request);
        body.Domain = request.Domain;

        logger.LogInformation("Creating customer {Org} ({Domain})", request.OrgDisplayName, request.Domain);

        var response = await service.Accounts.Customers
            .Create(body, _options.AccountName)
            .ExecuteAsync(cancellationToken);

        return MapCustomer(response);
    }

    public async Task<Customer> UpdateCustomerAsync(SaveCustomerRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OrgDisplayName);
        EnsureAccountConfigured();
        using var service = CreateService();

        var body = ToGoogleCustomer(request);

        var patch = service.Accounts.Customers.Patch(body, CustomerName(request.Id!));
        // Restrict the update to editable fields so the immutable domain/cloud-identity are untouched.
        patch.UpdateMask = "org_display_name,org_postal_address,primary_contact_info,language_code";

        var response = await patch.ExecuteAsync(cancellationToken);
        return MapCustomer(response);
    }

    public async Task DeleteCustomerAsync(string customerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        EnsureAccountConfigured();
        using var service = CreateService();

        await service.Accounts.Customers
            .Delete(CustomerName(customerId))
            .ExecuteAsync(cancellationToken);
    }

    public async Task<Customer> ImportCustomerAsync(ImportCustomerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureAccountConfigured();
        using var service = CreateService();

        var body = ToGoogleImportCustomerRequest(request);

        logger.LogInformation(
            "Importing customer ({Identifier}) at the account level",
            request.Domain ?? request.CloudIdentityId ?? request.PrimaryAdminEmail);

        var response = await service.Accounts.Customers
            .Import(body, _options.AccountName)
            .ExecuteAsync(cancellationToken);

        return MapCustomer(response);
    }

    public async Task<ChannelOperation> ProvisionCloudIdentityAsync(string customerId, ProvisionCloudIdentityRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentNullException.ThrowIfNull(request);
        EnsureAccountConfigured();
        using var service = CreateService();

        var body = ToGoogleProvisionCloudIdentityRequest(request);

        logger.LogInformation("Provisioning a Cloud Identity for customer {Customer}", customerId);

        var operation = await service.Accounts.Customers
            .ProvisionCloudIdentity(body, CustomerName(customerId))
            .ExecuteAsync(cancellationToken);

        return MapLongrunningOperation(operation);
    }

    public async Task<PurchasableSkusResult> ListPurchasableSkusAsync(string customerId, string productId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(productId);
        EnsureAccountConfigured();
        using var service = CreateService();

        var skus = new List<PurchasableSku>();
        string? pageToken = null;
        do
        {
            var request = service.Accounts.Customers.ListPurchasableSkus(CustomerName(customerId));
            request.CreateEntitlementPurchaseProduct = $"products/{productId}";
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);

            foreach (var purchasable in response.PurchasableSkus ?? [])
            {
                skus.Add(new PurchasableSku
                {
                    SkuName = purchasable.Sku?.Name ?? string.Empty,
                    SkuId = LastSegment(purchasable.Sku?.Name),
                    ProductId = ProductIdFromResourceName(purchasable.Sku?.Name) is { Length: > 0 } pid ? pid : productId,
                    DisplayName = purchasable.Sku?.MarketingInfo?.DisplayName
                });
            }

            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return new PurchasableSkusResult { Skus = skus };
    }

    public async Task<PurchasableOffersResult> ListPurchasableOffersAsync(string customerId, string productId, string skuId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(productId);
        ArgumentException.ThrowIfNullOrWhiteSpace(skuId);
        EnsureAccountConfigured();
        using var service = CreateService();

        var offers = new List<PurchasableOffer>();
        string? pageToken = null;
        do
        {
            var request = service.Accounts.Customers.ListPurchasableOffers(CustomerName(customerId));
            request.CreateEntitlementPurchaseSku = $"products/{productId}/skus/{skuId}";
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);

            foreach (var purchasable in response.PurchasableOffers ?? [])
            {
                offers.Add(new PurchasableOffer
                {
                    OfferName = purchasable.Offer?.Name ?? string.Empty,
                    DisplayName = purchasable.Offer?.MarketingInfo?.DisplayName,
                    SkuId = LastSegment(purchasable.Offer?.Sku?.Name) is { Length: > 0 } sid ? sid : skuId,
                    ProductId = ProductIdFromResourceName(purchasable.Offer?.Sku?.Name) is { Length: > 0 } pid ? pid : productId,
                    PriceReferenceId = purchasable.PriceReferenceId,
                    Pricing = MapOfferPricing(purchasable.Offer),
                    PaymentCycle = PaymentCycleLabel(purchasable.Offer?.Plan)
                });
            }

            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return new PurchasableOffersResult { Offers = offers };
    }
}
