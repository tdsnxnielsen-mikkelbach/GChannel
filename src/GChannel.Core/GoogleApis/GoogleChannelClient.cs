using GChannel.Core.Configuration;
using GChannel.Shared.Contracts;
using Google.Apis.Cloudchannel.v1;
using Google.Apis.Cloudchannel.v1.Data;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Util;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace GChannel.Core.GoogleApis;

/// <summary>
/// Builds a <see cref="CloudchannelService"/> using a credential from the injected
/// <see cref="IGoogleChannelCredentialSource"/> (the signed-in user's forwarded token for normal
/// requests, or a service-account credential for the background refresher).
/// </summary>
public sealed partial class GoogleChannelClient(
    IGoogleChannelCredentialSource credentialSource,
    IOptions<GoogleChannelOptions> options,
    ILogger<GoogleChannelClient> logger,
    Google.Apis.Http.IHttpClientFactory? googleHttpClientFactory = null,
    IDistributedCache? distributedCache = null) : IGoogleChannelClient
{
    private const string ProductsCacheKey = "catalog:products";
    private const string OffersCacheKey = "catalog:offers";
    private static readonly JsonSerializerOptions CacheJson = new(JsonSerializerDefaults.Web);

    private readonly GoogleChannelOptions _options = options.Value;

    private async Task<T> GetCachedCatalogAsync<T>(
        string cacheKey, Func<Task<T>> fetch, CancellationToken cancellationToken) where T : class
    {
        if (distributedCache is not null)
        {
            var cached = await distributedCache.GetStringAsync(cacheKey, cancellationToken);
            if (cached is not null && JsonSerializer.Deserialize<T>(cached, CacheJson) is { } cachedResult)
            {
                return cachedResult;
            }
        }

        var result = await fetch();
        if (distributedCache is not null)
        {
            await distributedCache.SetStringAsync(
                cacheKey,
                JsonSerializer.Serialize(result, CacheJson),
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(Math.Max(1, _options.CacheSeconds))
                },
                cancellationToken);
        }

        return result;
    }

    private Task<CatalogProductsResult> GetProductsAsync(
        CloudchannelService service, CancellationToken cancellationToken)
        => GetCachedCatalogAsync(ProductsCacheKey, () => FetchProductsAsync(service, cancellationToken), cancellationToken);

    private Task<CatalogSkusResult> GetSkusAsync(
        CloudchannelService service, string productId, CancellationToken cancellationToken)
        => GetCachedCatalogAsync($"catalog:skus:{productId}",
            () => FetchSkusAsync(service, productId, cancellationToken), cancellationToken);

    private Task<CatalogOffersResult> GetOffersAsync(
        CloudchannelService service, CancellationToken cancellationToken)
        => GetCachedCatalogAsync(OffersCacheKey, () => FetchOffersAsync(service, cancellationToken), cancellationToken);

    /// <summary>
    /// The <c>CloudIdentityType</c> enum value for a domain-verified Cloud Identity account.
    /// Only these accounts can be used for downstream reseller actions.
    /// </summary>
    private const string DomainCustomerType = "DOMAIN";

    /// <summary>The <c>link_state</c> a newly created channel partner link starts in.</summary>
    private const string InvitedLinkState = "INVITED";

    private CloudchannelService CreateService()
    {
        var credential = credentialSource.GetCredential();
        var service = new CloudchannelService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            HttpClientFactory = googleHttpClientFactory,
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
}
