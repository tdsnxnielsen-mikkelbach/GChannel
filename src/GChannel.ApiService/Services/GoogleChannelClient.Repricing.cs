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

// Repricing / rebilling margin — see IGoogleChannelClient for the contract documentation.
public sealed partial class GoogleChannelClient
{
    public async Task<RepricingConfigsResult> ListCustomerRepricingConfigsAsync(string customerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        EnsureAccountConfigured();
        using var service = CreateService();

        var configs = new List<RepricingConfig>();
        string? pageToken = null;
        do
        {
            var request = service.Accounts.Customers.CustomerRepricingConfigs.List(CustomerName(customerId));
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);

            foreach (var config in response.CustomerRepricingConfigs ?? [])
            {
                configs.Add(MapRepricingConfig(config.Name, config.RepricingConfig, config.UpdateTimeDateTimeOffset));
            }

            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return new RepricingConfigsResult { Configs = configs };
    }

    public async Task<RepricingConfig> CreateCustomerRepricingConfigAsync(string customerId, SaveRepricingConfigRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentNullException.ThrowIfNull(request);
        // Customer configs always target a specific entitlement (entitlement granularity).
        ArgumentException.ThrowIfNullOrWhiteSpace(request.EntitlementId);
        EnsureAccountConfigured();
        using var service = CreateService();

        var entitlementName = EntitlementName(customerId, request.EntitlementId!);
        var body = new GoogleCloudChannelV1CustomerRepricingConfig
        {
            RepricingConfig = ToGoogleRepricingConfig(request, entitlementName)
        };

        logger.LogInformation("Creating customer repricing config for {Customer} entitlement {Entitlement}", customerId, request.EntitlementId);

        var response = await service.Accounts.Customers.CustomerRepricingConfigs
            .Create(body, CustomerName(customerId))
            .ExecuteAsync(cancellationToken);

        return MapRepricingConfig(response.Name, response.RepricingConfig, response.UpdateTimeDateTimeOffset);
    }

    public async Task<RepricingConfig> UpdateCustomerRepricingConfigAsync(string customerId, string configId, SaveRepricingConfigRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(configId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.EntitlementId);
        EnsureAccountConfigured();
        using var service = CreateService();

        var configName = CustomerRepricingConfigName(customerId, configId);
        var entitlementName = EntitlementName(customerId, request.EntitlementId!);
        // patch overwrites the existing config; carry the resource name so Google targets the right one.
        var body = new GoogleCloudChannelV1CustomerRepricingConfig
        {
            Name = configName,
            RepricingConfig = ToGoogleRepricingConfig(request, entitlementName)
        };

        logger.LogInformation("Updating customer repricing config {Config} for {Customer}", configId, customerId);

        var response = await service.Accounts.Customers.CustomerRepricingConfigs
            .Patch(body, configName)
            .ExecuteAsync(cancellationToken);

        return MapRepricingConfig(response.Name, response.RepricingConfig, response.UpdateTimeDateTimeOffset);
    }

    public async Task DeleteCustomerRepricingConfigAsync(string customerId, string configId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(configId);
        EnsureAccountConfigured();
        using var service = CreateService();

        await service.Accounts.Customers.CustomerRepricingConfigs
            .Delete(CustomerRepricingConfigName(customerId, configId))
            .ExecuteAsync(cancellationToken);
    }

    public async Task<RepricingConfigsResult> ListChannelPartnerRepricingConfigsAsync(string linkId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkId);
        EnsureAccountConfigured();
        using var service = CreateService();

        var configs = new List<RepricingConfig>();
        string? pageToken = null;
        do
        {
            var request = service.Accounts.ChannelPartnerLinks.ChannelPartnerRepricingConfigs.List(ChannelPartnerLinkName(linkId));
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);

            foreach (var config in response.ChannelPartnerRepricingConfigs ?? [])
            {
                configs.Add(MapRepricingConfig(config.Name, config.RepricingConfig, config.UpdateTimeDateTimeOffset));
            }

            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return new RepricingConfigsResult { Configs = configs };
    }

    public async Task<RepricingConfig> CreateChannelPartnerRepricingConfigAsync(string linkId, SaveRepricingConfigRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkId);
        ArgumentNullException.ThrowIfNull(request);
        EnsureAccountConfigured();
        using var service = CreateService();

        var body = new GoogleCloudChannelV1ChannelPartnerRepricingConfig
        {
            // No entitlement id ⇒ the config applies to the whole partner's bill.
            RepricingConfig = ToGoogleRepricingConfig(request, ChannelPartnerEntitlementName(request.EntitlementId))
        };

        logger.LogInformation("Creating channel partner repricing config for link {Link}", linkId);

        var response = await service.Accounts.ChannelPartnerLinks.ChannelPartnerRepricingConfigs
            .Create(body, ChannelPartnerLinkName(linkId))
            .ExecuteAsync(cancellationToken);

        return MapRepricingConfig(response.Name, response.RepricingConfig, response.UpdateTimeDateTimeOffset);
    }

    public async Task<RepricingConfig> UpdateChannelPartnerRepricingConfigAsync(string linkId, string configId, SaveRepricingConfigRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkId);
        ArgumentException.ThrowIfNullOrWhiteSpace(configId);
        ArgumentNullException.ThrowIfNull(request);
        EnsureAccountConfigured();
        using var service = CreateService();

        var configName = ChannelPartnerRepricingConfigName(linkId, configId);
        var body = new GoogleCloudChannelV1ChannelPartnerRepricingConfig
        {
            Name = configName,
            RepricingConfig = ToGoogleRepricingConfig(request, ChannelPartnerEntitlementName(request.EntitlementId))
        };

        logger.LogInformation("Updating channel partner repricing config {Config} for link {Link}", configId, linkId);

        var response = await service.Accounts.ChannelPartnerLinks.ChannelPartnerRepricingConfigs
            .Patch(body, configName)
            .ExecuteAsync(cancellationToken);

        return MapRepricingConfig(response.Name, response.RepricingConfig, response.UpdateTimeDateTimeOffset);
    }

    public async Task DeleteChannelPartnerRepricingConfigAsync(string linkId, string configId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkId);
        ArgumentException.ThrowIfNullOrWhiteSpace(configId);
        EnsureAccountConfigured();
        using var service = CreateService();

        await service.Accounts.ChannelPartnerLinks.ChannelPartnerRepricingConfigs
            .Delete(ChannelPartnerRepricingConfigName(linkId, configId))
            .ExecuteAsync(cancellationToken);
    }
}
