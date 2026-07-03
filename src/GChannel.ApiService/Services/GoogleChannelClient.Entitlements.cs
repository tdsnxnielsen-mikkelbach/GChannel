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

// Entitlement lifecycle — see IGoogleChannelClient for the contract documentation.
public sealed partial class GoogleChannelClient
{
    public async Task<EntitlementsResult> ListEntitlementsAsync(string customerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        EnsureAccountConfigured();
        using var service = CreateService();

        var entitlements = new List<Entitlement>();
        var lookups = await BuildCatalogLookupsAsync(service, cancellationToken, includeSkus: true);
        string? pageToken = null;
        do
        {
            var request = service.Accounts.Customers.Entitlements.List(CustomerName(customerId));
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);

            foreach (var entitlement in response.Entitlements ?? [])
            {
                entitlements.Add(MapEntitlement(entitlement, lookups));
            }

            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return new EntitlementsResult { Entitlements = entitlements };
    }

    /// <summary>
    /// §10 read-model sync helper: lists a customer's entitlements WITHOUT resolving catalog display
    /// names (saves the products/offers/skus list calls — the read-model stores raw ids and the UI
    /// resolves names from the cached catalog). Paced through the ListEntitlements bucket.
    /// </summary>
    public async Task<EntitlementsResult> ListEntitlementsForSyncAsync(
        string customerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        EnsureAccountConfigured();
        using var service = CreateService();

        var pacer = _options.DashboardRequestsPerMinute > 0
            ? new RequestPacer(TimeSpan.FromSeconds(60.0 / _options.DashboardRequestsPerMinute))
            : null;
        var entitlements = new List<Entitlement>();
        string? pageToken = null;
        do
        {
            if (pacer is not null)
            {
                await pacer.WaitAsync(cancellationToken);
            }

            var request = service.Accounts.Customers.Entitlements.List(CustomerName(customerId));
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);
            foreach (var entitlement in response.Entitlements ?? [])
            {
                entitlements.Add(MapEntitlement(entitlement));
            }
            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return new EntitlementsResult { Entitlements = entitlements };
    }

    public async Task<Entitlement> GetEntitlementAsync(string customerId, string entitlementId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entitlementId);
        EnsureAccountConfigured();
        using var service = CreateService();

        var response = await service.Accounts.Customers.Entitlements
            .Get(EntitlementName(customerId, entitlementId))
            .ExecuteAsync(cancellationToken);

        var lookups = await BuildCatalogLookupsAsync(service, cancellationToken, includeSkus: true);
        return MapEntitlement(response, lookups);
    }

    /// <summary>
    /// §10 read-model helper: fetches ONLY the auto-renew flag for one entitlement via
    /// <c>entitlements.get</c>. Used as a fallback because <c>entitlements.list</c> returns
    /// <c>commitmentSettings.endTime</c> but omits <c>renewalSettings</c> for commitment offers, so the
    /// list-based sync can't otherwise tell whether an entitlement auto-renews. Returns
    /// <see langword="true"/>/<see langword="false"/> when renewal settings are present, or
    /// <see langword="null"/> when the entitlement has none (e.g. flexible/free plans).
    /// </summary>
    public async Task<bool?> GetEntitlementRenewalEnabledAsync(
        string customerId, string entitlementId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entitlementId);
        EnsureAccountConfigured();
        using var service = CreateService();

        var response = await service.Accounts.Customers.Entitlements
            .Get(EntitlementName(customerId, entitlementId))
            .ExecuteAsync(cancellationToken);

        var renewal = response.CommitmentSettings?.RenewalSettings;
        return renewal is null ? null : renewal.EnableRenewal ?? false;
    }

    public async Task<EntitlementChangesResult> ListEntitlementChangesAsync(string customerId, string entitlementId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entitlementId);
        EnsureAccountConfigured();
        using var service = CreateService();

        var changes = new List<EntitlementChange>();
        var offerLookup = await BuildOfferDisplayLookupAsync(service, cancellationToken);
        string? pageToken = null;
        do
        {
            var request = service.Accounts.Customers.Entitlements
                .ListEntitlementChanges(EntitlementName(customerId, entitlementId));
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);

            foreach (var change in response.EntitlementChanges ?? [])
            {
                changes.Add(MapEntitlementChange(change, offerLookup));
            }

            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return new EntitlementChangesResult { Changes = changes };
    }

    public async Task<CatalogOffer> LookupEntitlementOfferAsync(string customerId, string entitlementId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entitlementId);
        EnsureAccountConfigured();
        using var service = CreateService();

        var offer = await service.Accounts.Customers.Entitlements
            .LookupOffer(EntitlementName(customerId, entitlementId))
            .ExecuteAsync(cancellationToken);

        return new CatalogOffer
        {
            Name = offer.Name ?? string.Empty,
            DisplayName = offer.MarketingInfo?.DisplayName,
            Description = offer.MarketingInfo?.Description,
            SkuName = offer.Sku?.Name,
            SkuId = LastSegment(offer.Sku?.Name),
            SkuDisplayName = offer.Sku?.MarketingInfo?.DisplayName,
            ProductId = ProductIdFromResourceName(offer.Sku?.Name),
            ProductDisplayName = offer.Sku?.Product?.MarketingInfo?.DisplayName,
            DealCode = offer.DealCode,
            Pricing = MapOfferPricing(offer),
            PaymentPlan = offer.Plan?.PaymentPlan,
            PaymentCycle = PaymentCycleLabel(offer.Plan)
        };
    }

    public async Task<EntitlementOperation> CreateEntitlementAsync(string customerId, PurchaseEntitlementRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OfferId);
        EnsureAccountConfigured();
        using var service = CreateService();

        var body = new GoogleCloudChannelV1CreateEntitlementRequest
        {
            Entitlement = new GoogleCloudChannelV1Entitlement
            {
                Offer = OfferName(request.OfferId),
                Parameters = ToGoogleParameters(request.Parameters),
                PurchaseOrderId = string.IsNullOrWhiteSpace(request.PurchaseOrderId) ? null : request.PurchaseOrderId,
                BillingAccount = string.IsNullOrWhiteSpace(request.BillingAccount) ? null : request.BillingAccount
            }
        };

        logger.LogInformation("Purchasing entitlement for customer {Customer} on offer {Offer}", customerId, request.OfferId);

        var operation = await service.Accounts.Customers.Entitlements
            .Create(body, CustomerName(customerId))
            .ExecuteAsync(cancellationToken);

        return MapOperation(operation);
    }

    public async Task<EntitlementOperation> ChangeEntitlementOfferAsync(string customerId, string entitlementId, ChangeOfferRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entitlementId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OfferId);
        EnsureAccountConfigured();
        using var service = CreateService();

        var body = new GoogleCloudChannelV1ChangeOfferRequest
        {
            Offer = OfferName(request.OfferId),
            Parameters = ToGoogleParameters(request.Parameters),
            PurchaseOrderId = string.IsNullOrWhiteSpace(request.PurchaseOrderId) ? null : request.PurchaseOrderId
        };

        var operation = await service.Accounts.Customers.Entitlements
            .ChangeOffer(body, EntitlementName(customerId, entitlementId))
            .ExecuteAsync(cancellationToken);

        return MapOperation(operation);
    }

    public async Task<EntitlementOperation> ChangeEntitlementParametersAsync(string customerId, string entitlementId, ChangeParametersRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entitlementId);
        ArgumentNullException.ThrowIfNull(request.Parameters);
        EnsureAccountConfigured();
        using var service = CreateService();

        var body = new GoogleCloudChannelV1ChangeParametersRequest
        {
            Parameters = ToGoogleParameters(request.Parameters),
            PurchaseOrderId = string.IsNullOrWhiteSpace(request.PurchaseOrderId) ? null : request.PurchaseOrderId
        };

        var operation = await service.Accounts.Customers.Entitlements
            .ChangeParameters(body, EntitlementName(customerId, entitlementId))
            .ExecuteAsync(cancellationToken);

        return MapOperation(operation);
    }

    public async Task<EntitlementOperation> ChangeEntitlementRenewalAsync(string customerId, string entitlementId, ChangeRenewalSettingsRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entitlementId);
        EnsureAccountConfigured();
        using var service = CreateService();

        var body = new GoogleCloudChannelV1ChangeRenewalSettingsRequest
        {
            RenewalSettings = new GoogleCloudChannelV1RenewalSettings
            {
                EnableRenewal = request.EnableRenewal
            }
        };

        var operation = await service.Accounts.Customers.Entitlements
            .ChangeRenewalSettings(body, EntitlementName(customerId, entitlementId))
            .ExecuteAsync(cancellationToken);

        return MapOperation(operation);
    }

    public async Task<EntitlementOperation> ActivateEntitlementAsync(string customerId, string entitlementId, CancellationToken cancellationToken)
    {
        EnsureEntitlementArgs(customerId, entitlementId);
        using var service = CreateService();

        var operation = await service.Accounts.Customers.Entitlements
            .Activate(new GoogleCloudChannelV1ActivateEntitlementRequest(), EntitlementName(customerId, entitlementId))
            .ExecuteAsync(cancellationToken);

        return MapOperation(operation);
    }

    public async Task<EntitlementOperation> SuspendEntitlementAsync(string customerId, string entitlementId, CancellationToken cancellationToken)
    {
        EnsureEntitlementArgs(customerId, entitlementId);
        using var service = CreateService();

        var operation = await service.Accounts.Customers.Entitlements
            .Suspend(new GoogleCloudChannelV1SuspendEntitlementRequest(), EntitlementName(customerId, entitlementId))
            .ExecuteAsync(cancellationToken);

        return MapOperation(operation);
    }

    public async Task<EntitlementOperation> CancelEntitlementAsync(string customerId, string entitlementId, CancellationToken cancellationToken)
    {
        EnsureEntitlementArgs(customerId, entitlementId);
        using var service = CreateService();

        var operation = await service.Accounts.Customers.Entitlements
            .Cancel(new GoogleCloudChannelV1CancelEntitlementRequest(), EntitlementName(customerId, entitlementId))
            .ExecuteAsync(cancellationToken);

        return MapOperation(operation);
    }

    public async Task<EntitlementOperation> StartPaidServiceAsync(string customerId, string entitlementId, CancellationToken cancellationToken)
    {
        EnsureEntitlementArgs(customerId, entitlementId);
        using var service = CreateService();

        var operation = await service.Accounts.Customers.Entitlements
            .StartPaidService(new GoogleCloudChannelV1StartPaidServiceRequest(), EntitlementName(customerId, entitlementId))
            .ExecuteAsync(cancellationToken);

        return MapOperation(operation);
    }
}
