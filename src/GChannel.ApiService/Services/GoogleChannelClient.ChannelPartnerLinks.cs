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

// Channel partner links (n-tier) — see IGoogleChannelClient for the contract documentation.
public sealed partial class GoogleChannelClient
{
    public async Task<int> CountChannelPartnerLinksAsync(CancellationToken cancellationToken)
    {
        EnsureAccountConfigured();
        using var service = CreateService();
        return await CountChannelPartnerLinksAsync(service, cancellationToken);
    }

    /// <summary>Counts the reseller's channel partner links (BASIC view; account-level, quota-light).</summary>
    private async Task<int> CountChannelPartnerLinksAsync(CloudchannelService service, CancellationToken cancellationToken)
    {
        var count = 0;
        string? pageToken = null;
        do
        {
            var request = service.Accounts.ChannelPartnerLinks.List(_options.AccountName);
            request.View = AccountsResource.ChannelPartnerLinksResource.ListRequest.ViewEnum.BASIC;
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);
            count += response.ChannelPartnerLinks?.Count ?? 0;
            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return count;
    }

    /// <summary>
    /// Lists the reseller's channel partner links (BASIC view; account-level, quota-light) and returns
    /// both the total and a per-link-state breakdown for the dashboard. The BASIC view already carries
    /// <c>link_state</c>, so the breakdown costs no extra quota over a plain count.
    /// </summary>
    private async Task<(int Total, IReadOnlyList<DashboardChannelLinkState> ByState)> SummarizeChannelPartnerLinksAsync(
        CloudchannelService service, CancellationToken cancellationToken)
    {
        var total = 0;
        var byState = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        string? pageToken = null;
        do
        {
            var request = service.Accounts.ChannelPartnerLinks.List(_options.AccountName);
            request.View = AccountsResource.ChannelPartnerLinksResource.ListRequest.ViewEnum.BASIC;
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);

            foreach (var link in response.ChannelPartnerLinks ?? [])
            {
                total++;
                var state = string.IsNullOrWhiteSpace(link.LinkState) ? "UNSPECIFIED" : link.LinkState;
                byState[state] = byState.GetValueOrDefault(state) + 1;
            }

            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        var states = byState
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new DashboardChannelLinkState { State = kv.Key, Count = kv.Value })
            .ToList();

        return (total, states);
    }

    /// <summary>
    /// Counts the downstream end customers across every ACTIVE channel partner link \u2014 the indirect
    /// (reseller-owned) customer estate. Lists the links (BASIC, quota-light), then fans out one
    /// <c>channelPartnerLinks.customers.list</c> per ACTIVE link with bounded parallelism, tolerating a
    /// single link failing (e.g. a permission error) rather than sinking the whole figure. Every
    /// per-link list call is paced through the shared ListCustomers quota bucket.
    /// </summary>
    private async Task<int> CountIndirectCustomersAsync(
        CloudchannelService service, RequestPacer? customerListPacer, CancellationToken cancellationToken)
    {
        var activeLinkNames = await ListActiveChannelPartnerLinkNamesAsync(service, cancellationToken);
        if (activeLinkNames.Count == 0)
        {
            return 0;
        }

        using var throttle = new SemaphoreSlim(Math.Max(1, _options.DashboardMaxConcurrency));

        var counts = await Task.WhenAll(activeLinkNames.Select(async linkName =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                return await CountLinkCustomersAsync(service, linkName, customerListPacer, cancellationToken);
            }
            catch (Google.GoogleApiException ex)
            {
                logger.LogWarning(ex,
                    "Skipping channel partner link {Link} when counting indirect customers: {Status}",
                    linkName, ex.HttpStatusCode);
                return 0;
            }
            finally
            {
                throttle.Release();
            }
        }));

        return counts.Sum();
    }

    /// <summary>Lists the resource names of every ACTIVE channel partner link (BASIC view, quota-light).</summary>
    private async Task<List<string>> ListActiveChannelPartnerLinkNamesAsync(CloudchannelService service, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        string? pageToken = null;
        do
        {
            var request = service.Accounts.ChannelPartnerLinks.List(_options.AccountName);
            request.View = AccountsResource.ChannelPartnerLinksResource.ListRequest.ViewEnum.BASIC;
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);

            foreach (var link in response.ChannelPartnerLinks ?? [])
            {
                if (string.Equals(link.LinkState, "ACTIVE", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(link.Name))
                {
                    names.Add(link.Name);
                }
            }

            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return names;
    }

    /// <summary>Paginates a single channel partner link's customer list and returns the count.</summary>
    private static async Task<int> CountLinkCustomersAsync(
        CloudchannelService service, string linkName, RequestPacer? pacer, CancellationToken cancellationToken)
    {
        var count = 0;
        string? pageToken = null;
        do
        {
            if (pacer is not null)
            {
                await pacer.WaitAsync(cancellationToken);
            }

            var request = service.Accounts.ChannelPartnerLinks.Customers.List(linkName);
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);
            count += response.Customers?.Count ?? 0;
            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return count;
    }

    public async Task<ChannelPartnerLinksResult> ListChannelPartnerLinksAsync(CancellationToken cancellationToken)
    {
        EnsureAccountConfigured();
        using var service = CreateService();

        var links = new List<ChannelPartnerLink>();
        string? pageToken = null;
        do
        {
            var request = service.Accounts.ChannelPartnerLinks.List(_options.AccountName);
            // FULL includes the partner's Cloud Identity info so the UI can show who's linked.
            request.View = AccountsResource.ChannelPartnerLinksResource.ListRequest.ViewEnum.FULL;
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);

            foreach (var link in response.ChannelPartnerLinks ?? [])
            {
                links.Add(MapChannelPartnerLink(link));
            }

            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return new ChannelPartnerLinksResult { Links = links };
    }

    public async Task<ChannelPartnerLink> GetChannelPartnerLinkAsync(string linkId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkId);
        EnsureAccountConfigured();
        using var service = CreateService();

        var request = service.Accounts.ChannelPartnerLinks.Get(ChannelPartnerLinkName(linkId));
        request.View = AccountsResource.ChannelPartnerLinksResource.GetRequest.ViewEnum.FULL;
        var response = await request.ExecuteAsync(cancellationToken);
        return MapChannelPartnerLink(response);
    }

    public async Task<ChannelPartnerLink> CreateChannelPartnerLinkAsync(CreateChannelPartnerLinkRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ResellerCloudIdentityId);
        EnsureAccountConfigured();
        using var service = CreateService();

        // A new link always starts in the INVITED state; the partner accepts it via InviteLinkUri.
        var body = new GoogleCloudChannelV1ChannelPartnerLink
        {
            ResellerCloudIdentityId = request.ResellerCloudIdentityId,
            LinkState = InvitedLinkState
        };

        logger.LogInformation("Creating channel partner link for reseller {Reseller}", request.ResellerCloudIdentityId);

        var response = await service.Accounts.ChannelPartnerLinks
            .Create(body, _options.AccountName)
            .ExecuteAsync(cancellationToken);

        return MapChannelPartnerLink(response);
    }

    public async Task<ChannelPartnerLink> UpdateChannelPartnerLinkStateAsync(string linkId, UpdateChannelPartnerLinkRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LinkState);
        EnsureAccountConfigured();
        using var service = CreateService();

        // link_state is the only mutable field; the update mask scopes the patch to it.
        var body = new GoogleCloudChannelV1UpdateChannelPartnerLinkRequest
        {
            ChannelPartnerLink = new GoogleCloudChannelV1ChannelPartnerLink
            {
                LinkState = request.LinkState
            },
            UpdateMask = "channel_partner_link.link_state"
        };

        logger.LogInformation("Updating channel partner link {Link} to state {State}", linkId, request.LinkState);

        var response = await service.Accounts.ChannelPartnerLinks
            .Patch(body, ChannelPartnerLinkName(linkId))
            .ExecuteAsync(cancellationToken);

        return MapChannelPartnerLink(response);
    }

    public async Task<CustomersResult> ListChannelPartnerCustomersAsync(string linkId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkId);
        EnsureAccountConfigured();
        using var service = CreateService();

        var customers = new List<Customer>();
        string? pageToken = null;
        do
        {
            var request = service.Accounts.ChannelPartnerLinks.Customers.List(ChannelPartnerLinkName(linkId));
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);

            foreach (var customer in response.Customers ?? [])
            {
                customers.Add(MapCustomer(customer));
            }

            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return new CustomersResult { Customers = customers };
    }
}
