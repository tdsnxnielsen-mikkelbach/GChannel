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
}

/// <summary>
/// Builds a <see cref="CloudchannelService"/> using a credential from the injected
/// <see cref="IGoogleChannelCredentialSource"/> (the signed-in user's forwarded token for normal
/// requests, or a service-account credential for the background refresher).
/// </summary>
public sealed class GoogleChannelClient(
    IGoogleChannelCredentialSource credentialSource,
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

    public async Task<DashboardOverview> GetDashboardOverviewAsync(CancellationToken cancellationToken)
    {
        EnsureAccountConfigured();
        using var service = CreateService();

        // Cheap, quota-light first phase of the dashboard: just the customer list (one paginated
        // call set) drives the headline customer count and the onboarded-over-time chart, so the UI
        // can render these immediately while the slow per-customer entitlement aggregation loads.
        var customers = await ListAllCustomersAsync(service, cancellationToken);

        return new DashboardOverview
        {
            CustomerCount = customers.Count,
            CustomersOnboarded = BuildMonthlyOnboarded(customers)
        };
    }

    public async Task<DashboardSummary> GetDashboardSummaryAsync(
        CancellationToken cancellationToken,
        bool applyTimeBudget = true,
        Func<DashboardSummary, Task>? onPartial = null,
        int partialEvery = 0)
    {
        EnsureAccountConfigured();
        using var service = CreateService();

        // §3 entitlements — one List call per customer is unavoidable (there is no cross-customer
        // list). To guarantee the endpoint responds well within the caller's HTTP attempt timeout
        // (so it never gets cut off mid-flight and the cached result can warm up), the whole
        // aggregation runs under a single time budget with bounded parallelism. Customers not reached
        // within the budget, or that error out, are reported as skipped together with the reason why.
        // The background refresher (applyTimeBudget: false) runs unbounded so its cached result is
        // complete even for large estates where the on-demand budget would otherwise truncate it.
        var budgetSeconds = Math.Max(5, _options.DashboardBudgetSeconds);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (applyTimeBudget)
        {
            budget.CancelAfter(TimeSpan.FromSeconds(budgetSeconds));
        }
        var budgetToken = budget.Token;

        List<Customer> customers;
        CatalogLookups lookups;
        try
        {
            // §2 customers — also drives the onboarded-over-time chart (bucket by create time).
            customers = await ListAllCustomersAsync(service, budgetToken);

            // §1 catalog — products.list + offers.list + products.skus.list give the full
            // offer→SKU→product fallback chain so the Product mix labels resolve even when an
            // entitlement's specific offer is no longer listed.
            lookups = await BuildCatalogLookupsAsync(service, budgetToken, includeSkus: true);
        }
        catch (OperationCanceledException) when (applyTimeBudget && !cancellationToken.IsCancellationRequested)
        {
            // The budget tripped while loading the customer list / catalog — almost always because the
            // per-minute quota was exhausted and the request was stuck in retry back-off. Fail fast
            // with a non-cancellation exception so the endpoint serves the last-known-good cached
            // result instead of the caller blowing its HTTP timeout waiting for a doomed aggregation.
            throw new TimeoutException(
                $"Dashboard aggregation did not load the customer list within the {budgetSeconds}s time budget.");
        }

        using var throttle = new SemaphoreSlim(Math.Max(1, _options.DashboardMaxConcurrency));

        // Pace the per-customer entitlements.list calls to stay under the Channel API's per-minute
        // "ListEntitlements" quota. Without this, bounded concurrency alone still bursts past the
        // quota on large estates and the project gets a wave of 429s. Disabled when set to 0.
        var pacer = _options.DashboardRequestsPerMinute > 0
            ? new RequestPacer(TimeSpan.FromSeconds(60.0 / _options.DashboardRequestsPerMinute))
            : null;

        // The onboarding chart is constant across the run; compute it once for every snapshot.
        var onboarded = BuildMonthlyOnboarded(customers);

        // Accumulators merged as each customer completes (rather than after the whole Task.WhenAll) so
        // the background refresher can publish a growing partial summary while the quota-paced
        // aggregation is still in flight. All mutation happens under the gate.
        var gate = new object();
        var active = 0;
        var trials = 0;
        var suspended = 0;
        long activeSeats = 0;
        var notReached = 0;
        var failed = 0;
        var completed = 0;
        var failureReasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var productMix = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Builds a summary from the accumulators as they currently stand. Caller must hold the gate.
        DashboardSummary BuildSnapshotLocked() => new()
        {
            CustomerCount = customers.Count,
            ActiveEntitlementCount = active,
            TrialEntitlementCount = trials,
            SuspendedEntitlementCount = suspended,
            ActiveSeats = activeSeats,
            SkippedCustomerCount = notReached + failed,
            IncompleteReason = BuildIncompleteReason(notReached, failed, failureReasons, budgetSeconds),
            CustomersOnboarded = onboarded,
            ProductMix = productMix
                .OrderByDescending(kv => kv.Value)
                .Take(8)
                .Select(kv => new DashboardProductSlice { Product = kv.Key, Count = kv.Value })
                .ToList()
        };

        await Task.WhenAll(customers.Select(async customer =>
        {
            CustomerLoadResult result;
            var acquired = false;
            try
            {
                await throttle.WaitAsync(budgetToken);
                acquired = true;
                var aggregate = await AggregateCustomerEntitlementsAsync(
                    service, customer.Id, lookups, pacer, budgetToken);
                result = CustomerLoadResult.Loaded(aggregate);
            }
            catch (OperationCanceledException)
            {
                // Either the caller went away (request token) or the time budget elapsed. Swallow
                // per task so the debugger doesn't flag each in-flight parallel call as
                // user-unhandled; the genuine-cancellation check below decides whether to abort.
                result = CustomerLoadResult.NotReached;
            }
            catch (Google.GoogleApiException ex)
            {
                // One customer failing to list entitlements (e.g. permission/transient) must not
                // sink the whole dashboard. Record the reason and skip it.
                logger.LogWarning(ex, "Skipping customer {Customer} in dashboard summary: {Status}", customer.Id, ex.HttpStatusCode);
                result = CustomerLoadResult.Failed(DescribeApiError(ex));
            }
            finally
            {
                if (acquired)
                {
                    throttle.Release();
                }
            }

            DashboardSummary? snapshot = null;
            lock (gate)
            {
                switch (result.Outcome)
                {
                    case CustomerLoadOutcome.NotReachedInTime:
                        notReached++;
                        break;
                    case CustomerLoadOutcome.Failed:
                        failed++;
                        var reason = result.FailureReason ?? "unknown error";
                        failureReasons[reason] = failureReasons.GetValueOrDefault(reason) + 1;
                        break;
                    default:
                        var aggregate = result.Aggregate;
                        active += aggregate.Active;
                        trials += aggregate.Trials;
                        suspended += aggregate.Suspended;
                        activeSeats += aggregate.ActiveSeats;
                        foreach (var (label, count) in aggregate.ProductMix)
                        {
                            productMix[label] = productMix.GetValueOrDefault(label) + count;
                        }
                        break;
                }

                completed++;
                if (onPartial is not null && partialEvery > 0 && completed % partialEvery == 0)
                {
                    snapshot = BuildSnapshotLocked();
                }
            }

            if (snapshot is not null)
            {
                try
                {
                    await onPartial!(snapshot);
                }
                catch
                {
                    // Publishing progress is best-effort; never let it disturb the aggregation.
                }
            }
        }));

        // Only abort if the caller actually went away; a tripped time budget is expected and yields
        // a partial (but timely) summary rather than a failure.
        cancellationToken.ThrowIfCancellationRequested();

        DashboardSummary summary;
        lock (gate)
        {
            summary = BuildSnapshotLocked();
        }

        if (summary.IncompleteReason is not null)
        {
            logger.LogWarning("Dashboard summary incomplete: {Reason}", summary.IncompleteReason);
        }

        return summary;
    }

    private enum CustomerLoadOutcome { Loaded, NotReachedInTime, Failed }

    /// <summary>Outcome of loading one customer's entitlements during the dashboard aggregation.</summary>
    private readonly record struct CustomerLoadResult(
        CustomerLoadOutcome Outcome, EntitlementAggregate Aggregate, string? FailureReason)
    {
        public static CustomerLoadResult Loaded(EntitlementAggregate aggregate) =>
            new(CustomerLoadOutcome.Loaded, aggregate, null);

        public static readonly CustomerLoadResult NotReached =
            new(CustomerLoadOutcome.NotReachedInTime, default, null);

        public static CustomerLoadResult Failed(string reason) =>
            new(CustomerLoadOutcome.Failed, default, reason);
    }

    /// <summary>Turns a Channel API error into a short reason string for the "incomplete" note.</summary>
    private static string DescribeApiError(Google.GoogleApiException ex)
    {
        var code = (int)ex.HttpStatusCode;
        return code > 0 ? $"{code} {ex.HttpStatusCode}" : "API error";
    }

    /// <summary>Builds the human-readable "why is this incomplete" note, or null when nothing was skipped.</summary>
    private static string? BuildIncompleteReason(
        int notReached, int failed, IReadOnlyDictionary<string, int> failureReasons, int budgetSeconds)
    {
        if (notReached == 0 && failed == 0)
        {
            return null;
        }

        var parts = new List<string>();
        if (notReached > 0)
        {
            parts.Add($"{notReached} not loaded within the {budgetSeconds}s time budget");
        }

        if (failed > 0)
        {
            var detail = string.Join(", ", failureReasons
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Value}\u00d7 {kv.Key}"));
            parts.Add($"{failed} failed ({detail})");
        }

        return string.Join("; ", parts) + ".";
    }

    private readonly record struct EntitlementAggregate(
        int Active, int Trials, int Suspended, long ActiveSeats, IReadOnlyDictionary<string, int> ProductMix);

    /// <summary>Paginates a single customer's entitlements and returns its partial dashboard aggregate.</summary>
    private async Task<EntitlementAggregate> AggregateCustomerEntitlementsAsync(
        CloudchannelService service,
        string customerId,
        CatalogLookups lookups,
        RequestPacer? pacer,
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
            // Each page is one ListEntitlements request against the per-minute quota, so pace it.
            if (pacer is not null)
            {
                await pacer.WaitAsync(cancellationToken);
            }

            var request = service.Accounts.Customers.Entitlements.List(CustomerName(customerId));
            request.PageToken = entitlementToken;
            var response = await request.ExecuteAsync(cancellationToken);

            foreach (var raw in response.Entitlements ?? [])
            {
                var entitlement = MapEntitlement(raw, lookups);
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

                    // MapEntitlement already resolved the product name from the offer/SKU/product
                    // catalogs; fall back to the raw product id only when nothing could be resolved.
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

    /// <summary>Paginates the full reseller customer list (shared by the overview and summary).</summary>
    private async Task<List<Customer>> ListAllCustomersAsync(CloudchannelService service, CancellationToken cancellationToken)
    {
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

        return customers;
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

        var lookups = await BuildCatalogLookupsAsync(service, cancellationToken, includeSkus: true);
        return MapEntitlement(response, lookups);
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

    /// <summary>Maps a Google entitlement resource to the UI-facing <see cref="Entitlement"/> contract.</summary>
    private static Entitlement MapEntitlement(GoogleCloudChannelV1Entitlement entitlement, CatalogLookups lookups = default)
    {
        var offerId = LastSegment(entitlement.Offer);
        var productId = entitlement.ProvisionedService?.ProductId;
        var skuId = entitlement.ProvisionedService?.SkuId;

        string? offerDisplayName = null;
        string? skuDisplayName = null;
        string? productDisplayName = null;

        // 1. Offer catalog (offers.list) — the richest source: resolves offer, SKU and product names
        //    in one hit. Only present while the entitlement's specific offer is still listed.
        if (!string.IsNullOrEmpty(offerId) && lookups.Offers is { } offers && offers.TryGetValue(offerId, out var offerDisplay))
        {
            offerDisplayName = offerDisplay.OfferDisplayName;
            skuDisplayName = offerDisplay.SkuDisplayName;
            productDisplayName = offerDisplay.ProductDisplayName;
        }

        // 2. SKU catalog (products.skus.list) — covers entitlements whose offer is no longer listed,
        //    which is the usual reason the UI fell back to raw SKU/product ids.
        if (string.IsNullOrEmpty(skuDisplayName) && !string.IsNullOrEmpty(skuId)
            && lookups.Skus is { } skus && skus.TryGetValue(skuId, out var skuDisplay))
        {
            skuDisplayName = skuDisplay.SkuDisplayName;
            if (string.IsNullOrEmpty(productDisplayName))
            {
                productDisplayName = skuDisplay.ProductDisplayName;
            }
        }

        // 3. Product catalog (products.list) — last resort for the product name.
        if (string.IsNullOrEmpty(productDisplayName) && !string.IsNullOrEmpty(productId)
            && lookups.Products is { } productNames && productNames.TryGetValue(productId, out var productName))
        {
            productDisplayName = productName;
        }

        return new()
        {
            Name = entitlement.Name ?? string.Empty,
            Id = LastSegment(entitlement.Name),
            OfferName = entitlement.Offer,
            OfferId = offerId,
            OfferDisplayName = offerDisplayName,
            ProductId = productId,
            ProductDisplayName = productDisplayName,
            SkuId = skuId,
            SkuDisplayName = skuDisplayName,
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

    /// <summary>Friendly display names for a SKU (and its product), resolved from <c>products.skus.list</c>.</summary>
    private readonly record struct SkuDisplay(string? SkuDisplayName, string? ProductDisplayName);

    /// <summary>Catalog display-name lookups for entitlement and dashboard labels.</summary>
    private readonly record struct CatalogLookups(
        IReadOnlyDictionary<string, OfferDisplay> Offers,
        IReadOnlyDictionary<string, string> Products,
        IReadOnlyDictionary<string, SkuDisplay> Skus);

    /// <summary>
    /// Builds offer-, product- and (optionally) SKU-level display-name lookups from the reseller's
    /// catalog. The product map is seeded from the full <c>products.list</c> catalog (authoritative,
    /// so it covers products whose specific offer is no longer listed) and supplemented from
    /// <c>offers.list</c>, which also yields the offer map that turns opaque entitlement offer ids
    /// into names. When <paramref name="includeSkus"/> is set, a SKU-id -> name map is also built
    /// from <c>products.skus.list</c> per product, so an entitlement's SKU/product names resolve even
    /// when its specific offer is no longer listed. Failures are non-fatal: callers fall back to raw ids.
    /// </summary>
    private async Task<CatalogLookups> BuildCatalogLookupsAsync(
        CloudchannelService service, CancellationToken cancellationToken, bool includeSkus = false)
    {
        var offers = new Dictionary<string, OfferDisplay>(StringComparer.OrdinalIgnoreCase);
        var products = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Authoritative product-id -> name map from the full product catalog. Covers products whose
        // specific offer is no longer listed (the dashboard would otherwise show raw product ids).
        try
        {
            string? pageToken = null;
            do
            {
                var request = service.Products.List();
                request.Account = _options.AccountName;
                request.PageToken = pageToken;
                var response = await request.ExecuteAsync(cancellationToken);

                foreach (var product in response.Products ?? [])
                {
                    var productId = LastSegment(product.Name);
                    var productName = product.MarketingInfo?.DisplayName;
                    if (!string.IsNullOrEmpty(productId) && !string.IsNullOrEmpty(productName))
                    {
                        products[productId] = productName;
                    }
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
            logger.LogDebug(ex, "Could not list products for display-name resolution.");
        }

        // Offer-id -> display names (also supplements the product map for any product not already
        // covered above).
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
                    if (!string.IsNullOrEmpty(offerId))
                    {
                        offers[offerId] = new OfferDisplay(
                            offer.MarketingInfo?.DisplayName,
                            offer.Sku?.MarketingInfo?.DisplayName,
                            offer.Sku?.Product?.MarketingInfo?.DisplayName);
                    }

                    var productId = LastSegment(offer.Sku?.Product?.Name);
                    var productName = offer.Sku?.Product?.MarketingInfo?.DisplayName;
                    if (!string.IsNullOrEmpty(productId) && !string.IsNullOrEmpty(productName))
                    {
                        products.TryAdd(productId, productName);
                    }
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
            logger.LogDebug(ex, "Could not resolve catalog offer display names; entitlements may show ids.");
        }

        // SKU-id -> name map (and its product name) from the authoritative per-product SKU catalog.
        // This is what lets an entitlement resolve its SKU/product names when its specific offer is no
        // longer listed in offers.list (the common reason the UI shows raw ids). Bounded concurrency
        // keeps the per-product fan-out quick; each product is independently graceful.
        var skus = new Dictionary<string, SkuDisplay>(StringComparer.OrdinalIgnoreCase);
        if (includeSkus && products.Count > 0)
        {
            var skuGate = new object();
            using var throttle = new SemaphoreSlim(Math.Max(1, _options.DashboardMaxConcurrency));
            await Task.WhenAll(products.Select(async kv =>
            {
                var productId = kv.Key;
                var productName = kv.Value;
                await throttle.WaitAsync(cancellationToken);
                try
                {
                    string? skuToken = null;
                    do
                    {
                        var request = service.Products.Skus.List($"products/{productId}");
                        request.Account = _options.AccountName;
                        request.PageToken = skuToken;
                        var response = await request.ExecuteAsync(cancellationToken);

                        foreach (var sku in response.Skus ?? [])
                        {
                            var skuId = LastSegment(sku.Name);
                            if (string.IsNullOrEmpty(skuId))
                            {
                                continue;
                            }

                            var display = new SkuDisplay(sku.MarketingInfo?.DisplayName, productName);
                            lock (skuGate)
                            {
                                skus[skuId] = display;
                            }
                        }

                        skuToken = response.NextPageToken;
                    }
                    while (!string.IsNullOrEmpty(skuToken));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not list SKUs for product {Product} during display-name resolution.", productId);
                }
                finally
                {
                    throttle.Release();
                }
            }));
        }

        return new CatalogLookups(offers, products, skus);
    }

    /// <summary>Offer id -&gt; friendly display names (a thin view over <see cref="BuildCatalogLookupsAsync"/>).</summary>
    private async Task<IReadOnlyDictionary<string, OfferDisplay>> BuildOfferDisplayLookupAsync(
        CloudchannelService service, CancellationToken cancellationToken)
        => (await BuildCatalogLookupsAsync(service, cancellationToken)).Offers;

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
        var credential = credentialSource.GetCredential();
        var service = new CloudchannelService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = _options.ApplicationName,
            // We install our own back-off handler below (covering 429 and 503 and honouring the
            // server's Retry-After), so turn off the library default which only retries 503.
            DefaultExponentialBackOffPolicy = ExponentialBackOffPolicy.None
        });

        // Retry throttling (429 Too Many Requests) and transient 503s, honouring the server's
        // Retry-After header when present (Channel API quota errors include it) and otherwise using
        // exponential back-off with jitter. If retries are exhausted the original 429/503 surfaces
        // and is mapped to a clean response upstream.
        if (_options.MaxRetryAttempts > 0)
        {
            // ConfigurableMessageHandler caps total tries independently of the handler, so widen it.
            service.HttpClient.MessageHandler.NumTries =
                Math.Max(service.HttpClient.MessageHandler.NumTries, _options.MaxRetryAttempts + 1);
            service.HttpClient.MessageHandler.AddUnsuccessfulResponseHandler(
                new RetryAfterBackOffHandler(
                    _options.MaxRetryAttempts,
                    TimeSpan.FromSeconds(Math.Max(1, _options.MaxRetryDelaySeconds))));
        }

        return service;
    }

    private void EnsureAccountConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.AccountName))
        {
            throw new InvalidOperationException(
                "GoogleChannel:AccountId is not configured. Set the reseller account resource name (accounts/...).");
        }
    }

    /// <summary>
    /// Unsuccessful-response handler that retries 429/503 responses, honouring the server's
    /// <c>Retry-After</c> header (the Channel API sends it on quota errors) and otherwise falling back
    /// to exponential back-off with jitter. Waits are capped and cancellable.
    /// </summary>
    private sealed class RetryAfterBackOffHandler(int maxRetries, TimeSpan maxDelay) : IHttpUnsuccessfulResponseHandler
    {
        public async Task<bool> HandleResponseAsync(HandleUnsuccessfulResponseArgs args)
        {
            if (!args.SupportsRetry
                || args.CurrentFailedTry > maxRetries
                || args.Response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable))
            {
                return false;
            }

            var delay = RetryAfterDelay(args.Response) ?? ExponentialBackOff(args.CurrentFailedTry);
            if (delay > maxDelay)
            {
                delay = maxDelay;
            }

            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, args.CancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            return true;
        }

        private static TimeSpan? RetryAfterDelay(HttpResponseMessage response)
        {
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter is null)
            {
                return null;
            }

            if (retryAfter.Delta is { } delta)
            {
                return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
            }

            if (retryAfter.Date is { } date)
            {
                var until = date - DateTimeOffset.UtcNow;
                return until > TimeSpan.Zero ? until : TimeSpan.Zero;
            }

            return null;
        }

        private static TimeSpan ExponentialBackOff(int attempt)
        {
            // 1s, 2s, 4s, ... plus up to 1s of jitter to de-correlate concurrent retries.
            var seconds = Math.Pow(2, Math.Max(0, attempt - 1));
            return TimeSpan.FromSeconds(seconds + Random.Shared.NextDouble());
        }
    }

    /// <summary>
    /// Paces calls to at most one per <c>interval</c> (a token-bucket of size 1) so a burst of
    /// concurrent dashboard <c>entitlements.list</c> calls stays under the Channel API's per-minute
    /// quota instead of triggering 429s. Thread-safe; <c>WaitAsync</c> returns when the caller's slot
    /// is due (or throws if cancelled first).
    /// </summary>
    private sealed class RequestPacer(TimeSpan interval)
    {
        private readonly Lock _gate = new();
        private DateTimeOffset _nextSlot = DateTimeOffset.MinValue;

        public Task WaitAsync(CancellationToken cancellationToken)
        {
            DateTimeOffset slot;
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                slot = _nextSlot > now ? _nextSlot : now;
                _nextSlot = slot + interval;
            }

            var delay = slot - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? Task.Delay(delay, cancellationToken) : Task.CompletedTask;
        }
    }
}

/// <summary>Thrown when the inbound request carries no Google access token.</summary>
public sealed class MissingGoogleTokenException()
    : InvalidOperationException("No Google access token was supplied on the request.");
