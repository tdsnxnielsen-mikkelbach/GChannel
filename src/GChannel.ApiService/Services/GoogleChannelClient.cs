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

    /// <summary>Builds the aggregated home-dashboard figures from customers + entitlements.</summary>
    Task<DashboardSummary> GetDashboardSummaryAsync(CancellationToken cancellationToken);
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
                    OfferId = LastSegment(offer.Name),
                    DisplayName = offer.MarketingInfo?.DisplayName,
                    Description = offer.MarketingInfo?.Description,
                    SkuName = offer.Sku?.Name,
                    SkuId = LastSegment(offer.Sku?.Name),
                    SkuDisplayName = offer.Sku?.MarketingInfo?.DisplayName,
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
                    PriceReferenceId = purchasable.PriceReferenceId
                });
            }

            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return new PurchasableOffersResult { Offers = offers };
    }

    public async Task<EntitlementsResult> ListEntitlementsAsync(string customerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        EnsureAccountConfigured();
        using var service = CreateService();

        var entitlements = new List<Entitlement>();
        var offerLookup = await BuildOfferDisplayLookupAsync(service, cancellationToken);
        string? pageToken = null;
        do
        {
            var request = service.Accounts.Customers.Entitlements.List(CustomerName(customerId));
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);

            foreach (var entitlement in response.Entitlements ?? [])
            {
                entitlements.Add(MapEntitlement(entitlement, offerLookup));
            }

            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return new EntitlementsResult { Entitlements = entitlements };
    }

    public async Task<DashboardSummary> GetDashboardSummaryAsync(CancellationToken cancellationToken)
    {
        EnsureAccountConfigured();
        using var service = CreateService();

        // §2 customers — also drives the onboarded-over-time chart (bucket by create time).
        var customers = new List<Customer>();
        string? pageToken = null;
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

        // §1 catalog — one offer lookup resolves product/SKU names for the donut labels.
        var offerLookup = await BuildOfferDisplayLookupAsync(service, cancellationToken);

        // §3 entitlements — one List call per customer is unavoidable (there is no cross-customer
        // list), so the per-customer aggregation runs with bounded parallelism to keep the whole
        // call within the request timeout and let the cached result warm up. 429s are retried by
        // the shared resilience handler; partials are merged single-threaded to avoid locks.
        using var throttle = new SemaphoreSlim(MaxDashboardConcurrency);

        var partials = await Task.WhenAll(customers.Select(async customer =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                return await AggregateCustomerEntitlementsAsync(service, customer.Id, offerLookup, cancellationToken);
            }
            finally
            {
                throttle.Release();
            }
        }));

        var active = 0;
        var trials = 0;
        var suspended = 0;
        long activeSeats = 0;
        var productMix = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var partial in partials)
        {
            active += partial.Active;
            trials += partial.Trials;
            suspended += partial.Suspended;
            activeSeats += partial.ActiveSeats;

            foreach (var (label, count) in partial.ProductMix)
            {
                productMix[label] = productMix.GetValueOrDefault(label) + count;
            }
        }

        return new DashboardSummary
        {
            CustomerCount = customers.Count,
            ActiveEntitlementCount = active,
            TrialEntitlementCount = trials,
            SuspendedEntitlementCount = suspended,
            ActiveSeats = activeSeats,
            CustomersOnboarded = BuildMonthlyOnboarded(customers),
            ProductMix = productMix
                .OrderByDescending(kv => kv.Value)
                .Take(8)
                .Select(kv => new DashboardProductSlice { Product = kv.Key, Count = kv.Value })
                .ToList()
        };
    }

    /// <summary>Max concurrent per-customer entitlement list calls when building the dashboard summary.</summary>
    private const int MaxDashboardConcurrency = 6;

    private readonly record struct EntitlementAggregate(
        int Active, int Trials, int Suspended, long ActiveSeats, IReadOnlyDictionary<string, int> ProductMix);

    /// <summary>Paginates a single customer's entitlements and returns its partial dashboard aggregate.</summary>
    private async Task<EntitlementAggregate> AggregateCustomerEntitlementsAsync(
        CloudchannelService service,
        string customerId,
        IReadOnlyDictionary<string, OfferDisplay> offerLookup,
        CancellationToken cancellationToken)
    {
        var active = 0;
        var trials = 0;
        var suspended = 0;
        long activeSeats = 0;
        var productMix = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        string? entitlementToken = null;
        do
        {
            var request = service.Accounts.Customers.Entitlements.List(CustomerName(customerId));
            request.PageToken = entitlementToken;
            var response = await request.ExecuteAsync(cancellationToken);

            foreach (var raw in response.Entitlements ?? [])
            {
                var entitlement = MapEntitlement(raw, offerLookup);
                var isActive = string.Equals(entitlement.ProvisioningState, "ACTIVE", StringComparison.OrdinalIgnoreCase);

                if (isActive)
                {
                    active++;

                    var seats = entitlement.Parameters
                        .FirstOrDefault(p => string.Equals(p.Name, "num_units", StringComparison.OrdinalIgnoreCase))?.Value;
                    if (long.TryParse(seats, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    {
                        activeSeats += n;
                    }

                    var label = entitlement.ProductDisplayName ?? entitlement.ProductId ?? "Other";
                    productMix[label] = productMix.GetValueOrDefault(label) + 1;
                }

                if (entitlement.IsTrial)
                {
                    trials++;
                }

                if (string.Equals(entitlement.ProvisioningState, "SUSPENDED", StringComparison.OrdinalIgnoreCase))
                {
                    suspended++;
                }
            }

            entitlementToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(entitlementToken));

        return new EntitlementAggregate(active, trials, suspended, activeSeats, productMix);
    }

    /// <summary>Buckets customers into the trailing six months by their create time (oldest first).</summary>
    private static List<DashboardMonthlyPoint> BuildMonthlyOnboarded(IReadOnlyList<Customer> customers)
    {
        var now = DateTimeOffset.UtcNow;
        var firstOfThisMonth = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

        var points = new List<DashboardMonthlyPoint>(6);
        for (var i = 5; i >= 0; i--)
        {
            var monthStart = firstOfThisMonth.AddMonths(-i);
            var monthEnd = monthStart.AddMonths(1);
            var count = customers.Count(c => c.CreateTime is { } t && t >= monthStart && t < monthEnd);

            points.Add(new DashboardMonthlyPoint
            {
                Month = monthStart.ToString("MMM", CultureInfo.InvariantCulture),
                Customers = count
            });
        }

        return points;
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

        var offerLookup = await BuildOfferDisplayLookupAsync(service, cancellationToken);
        return MapEntitlement(response, offerLookup);
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
            ProductId = ProductIdFromResourceName(offer.Sku?.Name),
            DealCode = offer.DealCode
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
                PurchaseOrderId = string.IsNullOrWhiteSpace(request.PurchaseOrderId) ? null : request.PurchaseOrderId
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

    /// <summary>Maps a Google entitlement resource to the UI-facing <see cref="Entitlement"/> contract.</summary>
    private static Entitlement MapEntitlement(GoogleCloudChannelV1Entitlement entitlement, IReadOnlyDictionary<string, OfferDisplay>? offerLookup = null)
    {
        var offerId = LastSegment(entitlement.Offer);
        var productId = entitlement.ProvisionedService?.ProductId;
        var skuId = entitlement.ProvisionedService?.SkuId;

        OfferDisplay display = default;
        if (offerLookup is not null && !string.IsNullOrEmpty(offerId))
        {
            offerLookup.TryGetValue(offerId, out display);
        }

        return new()
        {
            Name = entitlement.Name ?? string.Empty,
            Id = LastSegment(entitlement.Name),
            OfferName = entitlement.Offer,
            OfferId = offerId,
            OfferDisplayName = display.OfferDisplayName,
            ProductId = productId,
            ProductDisplayName = display.ProductDisplayName,
            SkuId = skuId,
            SkuDisplayName = display.SkuDisplayName,
            ProvisioningState = entitlement.ProvisioningState,
            PurchaseOrderId = entitlement.PurchaseOrderId,
            BillingAccount = entitlement.BillingAccount,
            CreateTime = entitlement.CreateTimeDateTimeOffset,
            UpdateTime = entitlement.UpdateTimeDateTimeOffset,
            SuspensionReasons = entitlement.SuspensionReasons is { } reasons ? [.. reasons] : [],
            IsTrial = entitlement.TrialSettings?.Trial ?? false,
            TrialEndTime = entitlement.TrialSettings?.EndTimeDateTimeOffset,
            Commitment = entitlement.CommitmentSettings is { } commitment
                ? new EntitlementCommitment
                {
                    StartTime = commitment.StartTimeDateTimeOffset,
                    EndTime = commitment.EndTimeDateTimeOffset,
                    RenewalEnabled = commitment.RenewalSettings?.EnableRenewal,
                    PaymentPlan = commitment.RenewalSettings?.PaymentPlan
                }
                : null,
            Parameters = entitlement.Parameters is { } parameters
                ? parameters.Select(MapParameter).ToList()
                : []
        };
    }

    /// <summary>Friendly display names for an offer (and its SKU/product), resolved from the Catalog.</summary>
    private readonly record struct OfferDisplay(string? OfferDisplayName, string? SkuDisplayName, string? ProductDisplayName);

    /// <summary>
    /// Builds a lookup of offer id -> friendly display names from the reseller's offer catalog,
    /// used to turn opaque entitlement ids into human-readable names. A single <c>offers.list</c>
    /// resolves the offer, SKU and product names at once. Failures are non-fatal: entitlements
    /// still render with their ids if the catalog can't be loaded.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, OfferDisplay>> BuildOfferDisplayLookupAsync(
        CloudchannelService service, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, OfferDisplay>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string? pageToken = null;
            do
            {
                var request = service.Accounts.Offers.List(_options.AccountName);
                request.PageToken = pageToken;
                var response = await request.ExecuteAsync(cancellationToken);

                foreach (var offer in response.Offers ?? [])
                {
                    var offerId = LastSegment(offer.Name);
                    if (string.IsNullOrEmpty(offerId))
                    {
                        continue;
                    }

                    map[offerId] = new OfferDisplay(
                        offer.MarketingInfo?.DisplayName,
                        offer.Sku?.MarketingInfo?.DisplayName,
                        offer.Sku?.Product?.MarketingInfo?.DisplayName);
                }

                pageToken = response.NextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not resolve offer display names; entitlements will show ids.");
        }

        return map;
    }

    /// <summary>Maps a Google entitlement-change resource to the UI-facing <see cref="EntitlementChange"/>.</summary>
    private static EntitlementChange MapEntitlementChange(GoogleCloudChannelV1EntitlementChange change, IReadOnlyDictionary<string, OfferDisplay>? offerLookup = null)
    {
        var offerId = LastSegment(change.Offer);
        string? offerDisplayName = null;
        if (offerLookup is not null && !string.IsNullOrEmpty(offerId) && offerLookup.TryGetValue(offerId, out var display))
        {
            offerDisplayName = display.OfferDisplayName;
        }

        return new()
        {
            ChangeType = change.ChangeType,
            OfferId = offerId,
            OfferDisplayName = offerDisplayName,
            OperatorType = change.OperatorType,
            CreateTime = change.CreateTimeDateTimeOffset,
            Reason = change.ActivationReason
                ?? change.CancellationReason
                ?? change.SuspensionReason
                ?? change.OtherChangeReason
        };
    }

    private static EntitlementParameter MapParameter(GoogleCloudChannelV1Parameter parameter) => new()
    {
        Name = parameter.Name ?? string.Empty,
        Value = ValueToString(parameter.Value),
        Editable = parameter.Editable ?? false
    };

    private static string? ValueToString(GoogleCloudChannelV1Value? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value.StringValue is not null)
        {
            return value.StringValue;
        }

        if (value.Int64Value.HasValue)
        {
            return value.Int64Value.Value.ToString(CultureInfo.InvariantCulture);
        }

        if (value.DoubleValue.HasValue)
        {
            return value.DoubleValue.Value.ToString(CultureInfo.InvariantCulture);
        }

        return value.BoolValue.HasValue ? (value.BoolValue.Value ? "true" : "false") : null;
    }

    /// <summary>Translates UI parameter inputs to Google typed parameters (numeric -> int64, else string).</summary>
    private static IList<GoogleCloudChannelV1Parameter>? ToGoogleParameters(IReadOnlyList<EntitlementParameterInput> inputs) =>
        inputs is { Count: > 0 }
            ? inputs.Select(p => new GoogleCloudChannelV1Parameter
            {
                Name = p.Name,
                Value = new GoogleCloudChannelV1Value
                {
                    Int64Value = p.IntValue,
                    StringValue = p.IntValue.HasValue ? null : p.StringValue
                }
            }).ToList()
            : null;

    /// <summary>Wraps a long-running operation into the UI-facing <see cref="EntitlementOperation"/>.</summary>
    private static EntitlementOperation MapOperation(GoogleLongrunningOperation operation) => new()
    {
        OperationName = operation.Name,
        Done = operation.Done ?? false,
        Error = operation.Error?.Message
    };

    private static void EnsureEntitlementArgs(string customerId, string entitlementId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entitlementId);
    }

    /// <summary>Builds the full entitlement resource name for a customer + entitlement id.</summary>
    private string EntitlementName(string customerId, string entitlementId) =>
        $"{_options.AccountName}/customers/{customerId}/entitlements/{entitlementId}";

    /// <summary>Resolves an offer id or full resource name to a full offer resource name.</summary>
    private string OfferName(string offerIdOrName) =>
        offerIdOrName.Contains('/', StringComparison.Ordinal)
            ? offerIdOrName
            : $"{_options.AccountName}/offers/{offerIdOrName}";

    /// <summary>Maps a Google customer resource to the UI-facing <see cref="Customer"/> contract.</summary>
    private Customer MapCustomer(GoogleCloudChannelV1Customer customer) => new()
    {
        Name = customer.Name ?? string.Empty,
        Id = LastSegment(customer.Name),
        OrgDisplayName = customer.OrgDisplayName,
        Domain = customer.Domain,
        CloudIdentityId = customer.CloudIdentityId,
        LanguageCode = customer.LanguageCode,
        ChannelPartnerId = customer.ChannelPartnerId,
        CreateTime = customer.CreateTimeDateTimeOffset,
        PrimaryContact = customer.PrimaryContactInfo is { } contact
            ? new CustomerContact
            {
                FirstName = contact.FirstName,
                LastName = contact.LastName,
                Email = contact.Email,
                Title = contact.Title,
                Phone = contact.Phone
            }
            : null,
        Address = customer.OrgPostalAddress is { } address
            ? new CustomerAddress
            {
                RegionCode = address.RegionCode,
                PostalCode = address.PostalCode,
                AdministrativeArea = address.AdministrativeArea,
                Locality = address.Locality,
                AddressLines = address.AddressLines is { } lines ? [.. lines] : []
            }
            : null,
        CloudIdentity = customer.CloudIdentityInfo is { } info
            ? new CustomerCloudIdentity
            {
                CustomerType = info.CustomerType,
                PrimaryDomain = info.PrimaryDomain,
                IsDomainVerified = info.IsDomainVerified ?? false,
                AlternateEmail = info.AlternateEmail,
                AdminConsoleUri = info.AdminConsoleUri
            }
            : null
    };

    /// <summary>Builds a Google customer body from a save request (shared by create and update).</summary>
    private static GoogleCloudChannelV1Customer ToGoogleCustomer(SaveCustomerRequest request) => new()
    {
        OrgDisplayName = request.OrgDisplayName,
        LanguageCode = string.IsNullOrWhiteSpace(request.LanguageCode) ? null : request.LanguageCode,
        OrgPostalAddress = new GoogleTypePostalAddress
        {
            RegionCode = request.Address.RegionCode,
            PostalCode = request.Address.PostalCode,
            AdministrativeArea = request.Address.AdministrativeArea,
            Locality = request.Address.Locality,
            AddressLines = request.Address.AddressLines is { Count: > 0 } lines ? [.. lines] : null
        },
        PrimaryContactInfo = request.PrimaryContact is { } contact
            ? new GoogleCloudChannelV1ContactInfo
            {
                FirstName = contact.FirstName,
                LastName = contact.LastName,
                Email = contact.Email,
                Title = contact.Title,
                Phone = contact.Phone
            }
            : null
    };

    /// <summary>Builds the full customer resource name for a short customer id.</summary>
    private string CustomerName(string customerId) => $"{_options.AccountName}/customers/{customerId}";

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
