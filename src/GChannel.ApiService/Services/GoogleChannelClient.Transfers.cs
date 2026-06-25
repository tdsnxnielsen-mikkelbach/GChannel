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

// Entitlement transfers — see IGoogleChannelClient for the contract documentation.
public sealed partial class GoogleChannelClient
{
    public async Task<TransferableSkusResult> ListTransferableSkusAsync(string customerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        EnsureAccountConfigured();
        using var service = CreateService();

        // Resolve friendly product names from the catalog (the transferable SKU carries a product
        // resource but not always its marketing name); SKU names come back on the resource itself.
        var lookups = await BuildCatalogLookupsAsync(service, cancellationToken);

        var skus = new List<TransferableSku>();
        string? pageToken = null;
        do
        {
            var body = new GoogleCloudChannelV1ListTransferableSkusRequest
            {
                CustomerName = CustomerName(customerId),
                PageToken = pageToken
            };
            var response = await service.Accounts
                .ListTransferableSkus(body, _options.AccountName)
                .ExecuteAsync(cancellationToken);

            foreach (var transferable in response.TransferableSkus ?? [])
            {
                var skuName = transferable.Sku?.Name;
                var productId = ProductIdFromResourceName(skuName);
                var productName = !string.IsNullOrEmpty(productId) && lookups.Products.TryGetValue(productId, out var pn)
                    ? pn
                    : transferable.Sku?.Product?.MarketingInfo?.DisplayName;

                skus.Add(new TransferableSku
                {
                    SkuName = skuName ?? string.Empty,
                    SkuId = LastSegment(skuName),
                    ProductId = productId is { Length: > 0 } ? productId : null,
                    SkuDisplayName = transferable.Sku?.MarketingInfo?.DisplayName,
                    ProductDisplayName = productName,
                    IsEligible = transferable.TransferEligibility?.IsEligible ?? false,
                    IneligibilityReason = transferable.TransferEligibility?.IneligibilityReason,
                    EligibilityDescription = transferable.TransferEligibility?.Description,
                    LegacySku = LastSegment(transferable.LegacySku?.Name) is { Length: > 0 } legacy ? legacy : null
                });
            }

            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return new TransferableSkusResult { Skus = skus };
    }

    public async Task<TransferableOffersResult> ListTransferableOffersAsync(string customerId, string productId, string skuId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(productId);
        ArgumentException.ThrowIfNullOrWhiteSpace(skuId);
        EnsureAccountConfigured();
        using var service = CreateService();

        var offers = new List<TransferableOffer>();
        string? pageToken = null;
        do
        {
            var body = new GoogleCloudChannelV1ListTransferableOffersRequest
            {
                CustomerName = CustomerName(customerId),
                Sku = $"products/{productId}/skus/{skuId}",
                PageToken = pageToken
            };
            var response = await service.Accounts
                .ListTransferableOffers(body, _options.AccountName)
                .ExecuteAsync(cancellationToken);

            foreach (var transferable in response.TransferableOffers ?? [])
            {
                var offer = transferable.Offer;
                var offerSkuName = offer?.Sku?.Name;
                offers.Add(new TransferableOffer
                {
                    OfferName = offer?.Name ?? string.Empty,
                    OfferId = LastSegment(offer?.Name),
                    OfferDisplayName = offer?.MarketingInfo?.DisplayName,
                    SkuId = LastSegment(offerSkuName) is { Length: > 0 } sid ? sid : skuId,
                    SkuDisplayName = offer?.Sku?.MarketingInfo?.DisplayName,
                    ProductId = ProductIdFromResourceName(offerSkuName) is { Length: > 0 } pid ? pid : productId
                });
            }

            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return new TransferableOffersResult { Offers = offers };
    }

    public async Task<EntitlementOperation> TransferEntitlementsAsync(string customerId, TransferEntitlementsRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Entitlements is not { Count: > 0 })
        {
            throw new ArgumentException("At least one entitlement is required to transfer.", nameof(request));
        }
        EnsureAccountConfigured();
        using var service = CreateService();

        var body = new GoogleCloudChannelV1TransferEntitlementsRequest
        {
            Entitlements = request.Entitlements.Select(ToGoogleTransferEntitlement).ToList(),
            AuthToken = string.IsNullOrWhiteSpace(request.AuthToken) ? null : request.AuthToken
        };

        logger.LogInformation("Transferring {Count} entitlement(s) in for customer {Customer}", body.Entitlements.Count, customerId);

        var operation = await service.Accounts.Customers
            .TransferEntitlements(body, CustomerName(customerId))
            .ExecuteAsync(cancellationToken);

        return MapOperation(operation);
    }

    public async Task<EntitlementOperation> TransferEntitlementsToGoogleAsync(string customerId, TransferEntitlementsToGoogleRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Entitlements is not { Count: > 0 })
        {
            throw new ArgumentException("At least one entitlement is required to transfer.", nameof(request));
        }
        EnsureAccountConfigured();
        using var service = CreateService();

        var body = new GoogleCloudChannelV1TransferEntitlementsToGoogleRequest
        {
            Entitlements = request.Entitlements.Select(ToGoogleTransferEntitlement).ToList()
        };

        logger.LogInformation("Transferring {Count} entitlement(s) to Google for customer {Customer}", body.Entitlements.Count, customerId);

        var operation = await service.Accounts.Customers
            .TransferEntitlementsToGoogle(body, CustomerName(customerId))
            .ExecuteAsync(cancellationToken);

        return MapOperation(operation);
    }

    /// <summary>Builds a Google entitlement body (offer + seats/PO) for a transfer line.</summary>
    private GoogleCloudChannelV1Entitlement ToGoogleTransferEntitlement(TransferEntitlementLine line) => new()
    {
        Offer = OfferName(line.OfferId),
        Parameters = ToGoogleParameters(line.Parameters),
        PurchaseOrderId = string.IsNullOrWhiteSpace(line.PurchaseOrderId) ? null : line.PurchaseOrderId
    };
}
