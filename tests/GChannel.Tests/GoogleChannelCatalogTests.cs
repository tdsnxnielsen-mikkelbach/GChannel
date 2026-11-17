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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            var body = path switch
            {
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

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
