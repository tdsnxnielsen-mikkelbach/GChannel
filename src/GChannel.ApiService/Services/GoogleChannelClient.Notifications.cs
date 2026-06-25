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

// Pub/Sub subscriber registration (§7) — see IGoogleChannelClient for the contract documentation.
public sealed partial class GoogleChannelClient
{
    public async Task<SubscriberRegistration> ListSubscribersAsync(CancellationToken cancellationToken)
    {
        EnsureAccountConfigured();
        using var service = CreateService();

        var serviceAccounts = new List<string>();
        string? topic = null;
        string? pageToken = null;
        try
        {
            do
            {
                var request = service.Accounts.ListSubscribers(_options.AccountName);
                request.PageToken = pageToken;
                var response = await request.ExecuteAsync(cancellationToken);

                topic ??= response.Topic;
                if (response.ServiceAccounts is { } accounts)
                {
                    serviceAccounts.AddRange(accounts);
                }

                pageToken = response.NextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken));
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // No topic has been created yet (nothing registered) — report an empty registration.
            return new SubscriberRegistration { Topic = null, ServiceAccounts = [] };
        }

        return new SubscriberRegistration { Topic = topic, ServiceAccounts = serviceAccounts };
    }

    public async Task<SubscriberRegistration> RegisterSubscriberAsync(string serviceAccount, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceAccount);
        EnsureAccountConfigured();
        using var service = CreateService();

        var response = await service.Accounts
            .Register(new GoogleCloudChannelV1RegisterSubscriberRequest { ServiceAccount = serviceAccount }, _options.AccountName)
            .ExecuteAsync(cancellationToken);

        logger.LogInformation(
            "Registered Pub/Sub subscriber {ServiceAccount} on topic {Topic}", serviceAccount, response.Topic);

        return await ListSubscribersAsync(cancellationToken);
    }

    public async Task<SubscriberRegistration> UnregisterSubscriberAsync(string serviceAccount, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceAccount);
        EnsureAccountConfigured();
        using var service = CreateService();

        await service.Accounts
            .Unregister(new GoogleCloudChannelV1UnregisterSubscriberRequest { ServiceAccount = serviceAccount }, _options.AccountName)
            .ExecuteAsync(cancellationToken);

        logger.LogInformation("Unregistered Pub/Sub subscriber {ServiceAccount}", serviceAccount);

        return await ListSubscribersAsync(cancellationToken);
    }
}
