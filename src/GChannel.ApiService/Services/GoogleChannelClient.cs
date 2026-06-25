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
/// Builds a <see cref="CloudchannelService"/> using a credential from the injected
/// <see cref="IGoogleChannelCredentialSource"/> (the signed-in user's forwarded token for normal
/// requests, or a service-account credential for the background refresher).
/// </summary>
public sealed partial class GoogleChannelClient(
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

    /// <summary>The <c>link_state</c> a newly created channel partner link starts in.</summary>
    private const string InvitedLinkState = "INVITED";

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
}
