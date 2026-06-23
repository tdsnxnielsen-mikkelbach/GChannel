using GChannel.ApiService.Configuration;
using GChannel.Shared.Contracts;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Cloudchannel.v1;
using Google.Apis.Cloudchannel.v1.Data;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Util;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
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
}

/// <summary>
/// Builds a per-request <see cref="CloudchannelService"/> using the caller's Google OAuth
/// access token (forwarded as a Bearer token by the Blazor front end).
/// </summary>
public sealed class GoogleChannelClient(
    IHttpContextAccessor httpContextAccessor,
    IOptions<GoogleChannelOptions> options,
    ILogger<GoogleChannelClient> logger) : IGoogleChannelClient
{
    private readonly GoogleChannelOptions _options = options.Value;

    /// <summary>
    /// The <c>CloudIdentityType</c> enum value for a domain-verified Cloud Identity account.
    /// Only these accounts can be used for downstream reseller actions.
    /// </summary>
    private const string DomainCustomerType = "DOMAIN";

    public async Task<CheckCloudIdentityResult> CheckCloudIdentityAsync(
        CheckCloudIdentityRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Domain);
        EnsureAccountConfigured();

        using var service = CreateService();

        var body = new GoogleCloudChannelV1CheckCloudIdentityAccountsExistRequest
        {
            Domain = request.Domain,
            PrimaryAdminEmail = request.PrimaryAdminEmail
        };

        logger.LogInformation("Checking Cloud Identity accounts for domain {Domain}", request.Domain);

        var response = await service.Accounts
            .CheckCloudIdentityAccountsExist(body, _options.AccountName)
            .ExecuteAsync(cancellationToken);

        // All matched accounts are returned, but only DOMAIN-type accounts are usable for downstream
        // reseller actions (customer creation, transfers, entitlements). Non-DOMAIN matches (e.g.
        // TEAM / unspecified) are flagged via IsDomain / HasNonDomainAccounts so the UI can warn.
        var accounts = (response.CloudIdentityAccounts ?? [])
            .Select(a => new CloudIdentityAccount
            {
                Existing = a.Existing ?? false,
                Owned = a.Owned ?? false,
                CustomerName = a.CustomerName,
                CustomerCloudIdentityId = a.CustomerCloudIdentityId,
                CustomerType = a.CustomerType,
                IsDomain = string.Equals(a.CustomerType, DomainCustomerType, StringComparison.OrdinalIgnoreCase),
                ChannelPartnerCloudIdentityId = a.ChannelPartnerCloudIdentityId
            })
            .ToList();

        return new CheckCloudIdentityResult
        {
            Domain = request.Domain,
            Exists = accounts.Any(a => a.IsDomain && a.Existing),
            HasNonDomainAccounts = accounts.Any(a => !a.IsDomain),
            Accounts = accounts
        };
    }

    public async Task<CatalogProductsResult> ListProductsAsync(CancellationToken cancellationToken)
    {
        EnsureAccountConfigured();
        using var service = CreateService();

        var products = new List<CatalogProduct>();
        string? pageToken = null;
        do
        {
            var request = service.Products.List();
            request.Account = _options.AccountName;
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);

            foreach (var product in response.Products ?? [])
            {
                products.Add(new CatalogProduct
                {
                    Name = product.Name ?? string.Empty,
                    Id = LastSegment(product.Name),
                    DisplayName = product.MarketingInfo?.DisplayName,
                    Description = product.MarketingInfo?.Description
                });
            }

            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return new CatalogProductsResult { Products = products };
    }

    public async Task<CatalogSkusResult> ListSkusAsync(string productId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productId);
        EnsureAccountConfigured();
        using var service = CreateService();

        var skus = new List<CatalogSku>();
        string? pageToken = null;
        do
        {
            var request = service.Products.Skus.List($"products/{productId}");
            request.Account = _options.AccountName;
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);

            foreach (var sku in response.Skus ?? [])
            {
                skus.Add(new CatalogSku
                {
                    Name = sku.Name ?? string.Empty,
                    Id = LastSegment(sku.Name),
                    ProductId = ProductIdFromResourceName(sku.Name) is { Length: > 0 } pid ? pid : productId,
                    DisplayName = sku.MarketingInfo?.DisplayName,
                    Description = sku.MarketingInfo?.Description
                });
            }

            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return new CatalogSkusResult { Skus = skus };
    }

    public async Task<CatalogOffersResult> ListOffersAsync(CancellationToken cancellationToken)
    {
        EnsureAccountConfigured();
        using var service = CreateService();

        var offers = new List<CatalogOffer>();
        string? pageToken = null;
        do
        {
            var request = service.Accounts.Offers.List(_options.AccountName);
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);

            foreach (var offer in response.Offers ?? [])
            {
                offers.Add(new CatalogOffer
                {
                    Name = offer.Name ?? string.Empty,
                    DisplayName = offer.MarketingInfo?.DisplayName,
                    Description = offer.MarketingInfo?.Description,
                    SkuName = offer.Sku?.Name,
                    SkuId = LastSegment(offer.Sku?.Name),
                    ProductId = ProductIdFromResourceName(offer.Sku?.Name),
                    DealCode = offer.DealCode
                });
            }

            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return new CatalogOffersResult { Offers = offers };
    }

    public async Task<CatalogSkuGroupsResult> ListSkuGroupsAsync(CancellationToken cancellationToken)
    {
        EnsureAccountConfigured();
        using var service = CreateService();

        var groups = new List<CatalogSkuGroup>();
        string? pageToken = null;
        do
        {
            var request = service.Accounts.SkuGroups.List(_options.AccountName);
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);

            foreach (var group in response.SkuGroups ?? [])
            {
                groups.Add(new CatalogSkuGroup
                {
                    Name = group.Name ?? string.Empty,
                    Id = LastSegment(group.Name),
                    DisplayName = group.DisplayName
                });
            }

            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return new CatalogSkuGroupsResult { SkuGroups = groups };
    }

    public async Task<CatalogBillableSkusResult> ListBillableSkusAsync(string skuGroupId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skuGroupId);
        EnsureAccountConfigured();
        using var service = CreateService();

        var billableSkus = new List<CatalogBillableSku>();
        string? pageToken = null;
        do
        {
            var request = service.Accounts.SkuGroups.BillableSkus.List($"{_options.AccountName}/skuGroups/{skuGroupId}");
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);

            foreach (var billable in response.BillableSkus ?? [])
            {
                billableSkus.Add(new CatalogBillableSku
                {
                    Sku = billable.Sku ?? string.Empty,
                    SkuId = LastSegment(billable.Sku),
                    ProductId = ProductIdFromResourceName(billable.Sku),
                    SkuDisplayName = billable.SkuDisplayName,
                    Service = billable.Service,
                    ServiceDisplayName = billable.ServiceDisplayName
                });
            }

            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return new CatalogBillableSkusResult { BillableSkus = billableSkus };
    }

    /// <summary>Returns the last "/"-separated segment of a resource name (its short id).</summary>
    private static string LastSegment(string? resourceName) =>
        string.IsNullOrEmpty(resourceName)
            ? string.Empty
            : resourceName[(resourceName.LastIndexOf('/') + 1)..];

    /// <summary>
    /// Extracts the product id from a SKU resource name of the form
    /// <c>products/{product}/skus/{sku}</c>. Returns an empty string when not present.
    /// </summary>
    private static string ProductIdFromResourceName(string? resourceName)
    {
        if (string.IsNullOrEmpty(resourceName))
        {
            return string.Empty;
        }

        var segments = resourceName.Split('/');
        var index = Array.IndexOf(segments, "products");
        return index >= 0 && index + 1 < segments.Length ? segments[index + 1] : string.Empty;
    }

    private CloudchannelService CreateService()
    {
        var credential = GoogleCredential.FromAccessToken(GetAccessToken());
        var service = new CloudchannelService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = _options.ApplicationName,
            // We install our own back-off handler below (covering 429 and 503), so turn off the
            // library default which only retries 503.
            DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.None
        });

        // Retry throttling (429 Too Many Requests) and transient 503s with exponential back-off so a
        // burst of catalog reads degrades gracefully instead of failing outright. If retries are
        // exhausted the original 429/503 surfaces and is mapped to a clean response upstream.
        if (_options.MaxRetryAttempts > 0)
        {
            var backOff = new BackOffHandler(new BackOffHandler.Initializer(
                new ExponentialBackOff(TimeSpan.FromMilliseconds(500), _options.MaxRetryAttempts))
            {
                HandleUnsuccessfulResponseFunc = static response =>
                    response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
            });
            service.HttpClient.MessageHandler.AddUnsuccessfulResponseHandler(backOff);
        }

        return service;
    }

    private string GetAccessToken()
    {
        var header = httpContextAccessor.HttpContext?.Request.Headers[HeaderNames.Authorization].ToString();
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            throw new MissingGoogleTokenException();
        }

        return header["Bearer ".Length..].Trim();
    }

    private void EnsureAccountConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.AccountName))
        {
            throw new InvalidOperationException(
                "GoogleChannel:AccountId is not configured. Set the reseller account resource name (accounts/...).");
        }
    }
}

/// <summary>Thrown when the inbound request carries no Google access token.</summary>
public sealed class MissingGoogleTokenException()
    : InvalidOperationException("No Google access token was supplied on the request.");
