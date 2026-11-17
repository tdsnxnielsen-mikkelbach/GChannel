using System.Net;
using System.Text;
using GChannel.Core.Configuration;
using GChannel.Core.GoogleApis;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace GChannel.Tests;

public sealed class GoogleChannelCatalogTests
{
    [Fact]
    public async Task Single_entitlement_loads_skus_only_for_its_product()
    {
        using var handler = new CatalogHttpHandler();
        var client = CreateClient(handler);

        var entitlement = await client.GetEntitlementAsync("customer", "entitlement", TestContext.Current.CancellationToken);

        entitlement.ProductDisplayName.ShouldBe("Product one");
        entitlement.SkuDisplayName.ShouldBe("SKU one");
        handler.Requests.Count(path => path == "/v1/products").ShouldBe(1);
        handler.Requests.Count(path => path == "/v1/accounts/test/offers").ShouldBe(1);
        handler.Requests.Count(path => path == "/v1/products/p1/skus").ShouldBe(1);
        handler.Requests.ShouldNotContain("/v1/products/p2/skus");
    }

    [Fact]
    public async Task Single_entitlement_load_reuses_shared_catalog_cache_across_clients()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();

        using var firstHandler = new CatalogHttpHandler();
        var firstClient = CreateClient(firstHandler, cache);
        var firstEntitlement = await firstClient.GetEntitlementAsync(
            "customer", "entitlement", TestContext.Current.CancellationToken);

        using var secondHandler = new CatalogHttpHandler();
        var secondClient = CreateClient(secondHandler, cache);
        var secondEntitlement = await secondClient.GetEntitlementAsync(
            "customer", "entitlement", TestContext.Current.CancellationToken);

        firstEntitlement.ProductDisplayName.ShouldBe("Product one");
        firstEntitlement.SkuDisplayName.ShouldBe("SKU one");
        secondEntitlement.ProductDisplayName.ShouldBe("Product one");
        secondEntitlement.SkuDisplayName.ShouldBe("SKU one");
        secondHandler.Requests.ShouldContain("/v1/accounts/test/customers/customer/entitlements/entitlement");
        secondHandler.Requests.ShouldNotContain("/v1/products");
        secondHandler.Requests.ShouldNotContain("/v1/accounts/test/offers");
        secondHandler.Requests.ShouldNotContain("/v1/products/p1/skus");
    }

    [Fact]
    public async Task Entitlement_change_names_load_offers_without_loading_the_product_catalog()
    {
        using var handler = new CatalogHttpHandler();
        var client = CreateClient(handler);

        var changes = await client.ListEntitlementChangesAsync(
            "customer", "entitlement", TestContext.Current.CancellationToken);

        changes.Changes.ShouldHaveSingleItem().OfferDisplayName.ShouldBe("Offer one");
        handler.Requests.ShouldContain("/v1/accounts/test/offers");
        handler.Requests.ShouldContain("/v1/accounts/test/customers/customer/entitlements/entitlement:listEntitlementChanges");
        handler.Requests.ShouldNotContain("/v1/products");
    }

    [Fact]
    public async Task Entitlement_change_names_reuse_the_shared_offer_catalog_cache()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();

        using var firstHandler = new CatalogHttpHandler();
        var firstClient = CreateClient(firstHandler, cache);
        var firstChanges = await firstClient.ListEntitlementChangesAsync(
            "customer", "first", TestContext.Current.CancellationToken);

        using var secondHandler = new CatalogHttpHandler();
        var secondClient = CreateClient(secondHandler, cache);
        var secondChanges = await secondClient.ListEntitlementChangesAsync(
            "customer", "second", TestContext.Current.CancellationToken);

        firstChanges.Changes.ShouldHaveSingleItem().OfferDisplayName.ShouldBe("Offer one");
        secondChanges.Changes.ShouldHaveSingleItem().OfferDisplayName.ShouldBe("Offer one");
        firstHandler.Requests.Count(path => path == "/v1/accounts/test/offers").ShouldBe(1);
        secondHandler.Requests.Count(path => path == "/v1/accounts/test/offers").ShouldBe(0);
        secondHandler.Requests.ShouldContain("/v1/accounts/test/customers/customer/entitlements/second:listEntitlementChanges");
    }

    [Fact]
    public async Task Transfer_offers_and_transfer_carry_the_selected_billing_account()
    {
        using var handler = new CatalogHttpHandler();
        var client = CreateClient(handler);
        const string billingAccount = "accounts/test/billingAccounts/eur";

        var offers = await client.ListTransferableOffersAsync(
            "customer", "p1", "s1", billingAccount, TestContext.Current.CancellationToken);
        await client.TransferEntitlementsAsync("customer", new GChannel.Shared.Contracts.TransferEntitlementsRequest
        {
            Entitlements = [new GChannel.Shared.Contracts.TransferEntitlementLine { OfferId = "offer1", BillingAccount = billingAccount }]
        }, TestContext.Current.CancellationToken);

        var offer = offers.Offers.ShouldHaveSingleItem();
        offer.PaymentCycle.ShouldBe("Monthly");
        offer.Pricing.ShouldHaveSingleItem().EffectivePrice!.Units.ShouldBe(2);
        handler.Bodies["/v1/accounts/test:listTransferableOffers"].ShouldContain($"\"billingAccount\":\"{billingAccount}\"");
        handler.Bodies["/v1/accounts/test/customers/customer:transferEntitlements"].ShouldContain($"\"billingAccount\":\"{billingAccount}\"");
    }

    private static GoogleChannelClient CreateClient(CatalogHttpHandler handler, IDistributedCache? cache = null)
    {
        var credentialSource = Substitute.For<IGoogleChannelCredentialSource>();
        credentialSource.GetCredential().Returns(GoogleCredential.FromAccessToken("test-token"));

        return new GoogleChannelClient(
            credentialSource,
            Options.Create(new GoogleChannelOptions { AccountId = "test" }),
            NullLogger<GoogleChannelClient>.Instance,
            new StubHttpClientFactory(handler),
            cache);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => handler;
    }

    private sealed class CatalogHttpHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public Dictionary<string, string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            if (request.Content is not null)
            {
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                if (request.Content.Headers.ContentEncoding.Contains("gzip"))
                {
                    using var gzip = new System.IO.Compression.GZipStream(new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress);
                    using var plain = new MemoryStream();
                    await gzip.CopyToAsync(plain, cancellationToken);
                    bytes = plain.ToArray();
                }
                Bodies[path] = Encoding.UTF8.GetString(bytes);
            }
            var body = path switch
            {
                "/v1/accounts/test:listTransferableOffers" =>
                    """{"transferableOffers":[{"offer":{"name":"accounts/test/offers/offer1","marketingInfo":{"displayName":"Annual Plan (Monthly Payment)"},"sku":{"name":"products/p1/skus/s1"},"plan":{"paymentCycle":{"periodType":"MONTH","duration":1}},"priceByResources":[{"resourceType":"SEAT","price":{"effectivePrice":{"currencyCode":"EUR","units":"2","nanos":630000000}}}]}}]}""",
                "/v1/accounts/test/customers/customer:transferEntitlements" =>
                    """{"name":"operations/transfer","done":false}""",
                "/v1/accounts/test/customers/customer/entitlements/entitlement" =>
                    """{"name":"accounts/test/customers/customer/entitlements/entitlement","offer":"accounts/test/offers/offer1","provisionedService":{"productId":"p1","skuId":"s1"}}""",
                "/v1/products" =>
                    """{"products":[{"name":"products/p1","marketingInfo":{"displayName":"Product one"}},{"name":"products/p2","marketingInfo":{"displayName":"Product two"}}]}""",
                "/v1/accounts/test/offers" =>
                    """{"offers":[{"name":"accounts/test/offers/offer1","marketingInfo":{"displayName":"Offer one"},"sku":{"name":"products/p1/skus/s1","marketingInfo":{"displayName":"SKU one"},"product":{"name":"products/p1","marketingInfo":{"displayName":"Product one"}}}}]}""",
                "/v1/products/p1/skus" =>
                    """{"skus":[{"name":"products/p1/skus/s1","marketingInfo":{"displayName":"SKU one"}}]}""",
                _ when path.StartsWith("/v1/accounts/test/customers/customer/entitlements/", StringComparison.Ordinal)
                    && path.EndsWith(":listEntitlementChanges", StringComparison.Ordinal) =>
                    """{"entitlementChanges":[{"changeType":"CREATED","offer":"accounts/test/offers/offer1"}]}""",
                _ => throw new InvalidOperationException($"Unexpected Google API request: {path}")
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
