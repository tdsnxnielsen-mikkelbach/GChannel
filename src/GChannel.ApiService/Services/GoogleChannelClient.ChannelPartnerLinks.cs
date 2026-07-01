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
    /// Computes the indirect (reseller-owned) customer estate: the downstream end customers across
    /// every ACTIVE channel partner link, plus a per-reseller breakdown for the dashboard chart. This
    /// is a separate set from the account's own <c>accounts.customers.list</c> (a distributor's direct
    /// list does not include its resellers' customers), so it is the only way to surface the reseller
    /// estate. Lists the ACTIVE links (FULL view, for their display names), then fans out one
    /// <c>channelPartnerLinks.customers.list</c> per link with bounded parallelism, each call paced
    /// through the shared ListCustomers quota bucket; per reseller it also sums each customer's active
    /// seats (<c>num_units</c>) so the chart can rank by total seats rather than headcount. A single
    /// link failing (e.g. a permission error) is tolerated rather than sinking the whole figure.
    /// </summary>
    private async Task<(int Total, IReadOnlyList<DashboardResellerCustomers> ByReseller)> GetIndirectEstateAsync(
        CloudchannelService service, RequestPacer? customerListPacer, RequestPacer? entitlementPacer, CancellationToken cancellationToken)
    {
        var activeLinks = await ListActiveChannelPartnerLinksAsync(service, cancellationToken);
        if (activeLinks.Count == 0)
        {
            return (0, []);
        }

        using var throttle = new SemaphoreSlim(Math.Max(1, _options.DashboardMaxConcurrency));

        var perReseller = await Task.WhenAll(activeLinks.Select(async link =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                var estate = await CountLinkEstateAsync(service, link.Name, customerListPacer, entitlementPacer, cancellationToken);
                return new DashboardResellerCustomers { Reseller = link.Label, CustomerCount = estate.Customers, SeatCount = estate.Seats };
            }
            catch (Google.GoogleApiException ex)
            {
                logger.LogWarning(ex,
                    "Skipping channel partner link {Link} when counting indirect customers: {Status}",
                    link.Name, ex.HttpStatusCode);
                return new DashboardResellerCustomers { Reseller = link.Label, CustomerCount = 0, SeatCount = 0 };
            }
            finally
            {
                throttle.Release();
            }
        }));

        var byReseller = perReseller
            .Where(r => r.CustomerCount > 0)
            .OrderByDescending(r => r.SeatCount)
            .ThenByDescending(r => r.CustomerCount)
            .ThenBy(r => r.Reseller, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var total = byReseller.Sum(r => r.CustomerCount);
        var top = byReseller.Count > 15 ? byReseller.Take(15).ToList() : byReseller;
        return (total, top);
    }

    /// <summary>Lists ACTIVE channel partner links (FULL view) with a friendly display label each.</summary>
    private async Task<List<(string Name, string Label)>> ListActiveChannelPartnerLinksAsync(
        CloudchannelService service, CancellationToken cancellationToken)
    {
        var links = new List<(string Name, string Label)>();
        string? pageToken = null;
        do
        {
            var request = service.Accounts.ChannelPartnerLinks.List(_options.AccountName);
            request.View = AccountsResource.ChannelPartnerLinksResource.ListRequest.ViewEnum.FULL;
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);

            foreach (var link in response.ChannelPartnerLinks ?? [])
            {
                if (string.Equals(link.LinkState, "ACTIVE", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(link.Name))
                {
                    var label = link.ChannelPartnerCloudIdentityInfo?.PrimaryDomain
                        ?? link.ResellerCloudIdentityId
                        ?? LastSegment(link.Name);
                    links.Add((link.Name, label));
                }
            }

            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return links;
    }

    /// <summary>
    /// Counts a single channel partner link's downstream customers and sums the active seats
    /// (<c>num_units</c>) across each customer's entitlements. The customer count uses the shared
    /// ListCustomers quota bucket (<paramref name="customerPacer"/>); each per-customer
    /// entitlements.list page is paced through the ListEntitlements bucket (<paramref name="entitlementPacer"/>).
    /// A single customer's entitlement read failing is tolerated (it just contributes zero seats).
    /// </summary>
    private async Task<(int Customers, long Seats)> CountLinkEstateAsync(
        CloudchannelService service, string linkName, RequestPacer? customerPacer, RequestPacer? entitlementPacer, CancellationToken cancellationToken)
    {
        var customerNames = new List<string>();
        string? pageToken = null;
        do
        {
            if (customerPacer is not null)
            {
                await customerPacer.WaitAsync(cancellationToken);
            }

            var request = service.Accounts.ChannelPartnerLinks.Customers.List(linkName);
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);
            foreach (var customer in response.Customers ?? [])
            {
                if (!string.IsNullOrEmpty(customer.Name))
                {
                    customerNames.Add(customer.Name);
                }
            }
            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        long seats = 0;
        foreach (var customerName in customerNames)
        {
            try
            {
                seats += await SumActiveSeatsAsync(service, customerName, entitlementPacer, cancellationToken);
            }
            catch (Google.GoogleApiException ex)
            {
                logger.LogWarning(ex,
                    "Skipping seat count for customer {Customer}: {Status}", customerName, ex.HttpStatusCode);
            }
        }

        return (customerNames.Count, seats);
    }

    /// <summary>Sums <c>num_units</c> across the ACTIVE entitlements of one (full-resource-name) customer.</summary>
    private static async Task<long> SumActiveSeatsAsync(
        CloudchannelService service, string customerName, RequestPacer? pacer, CancellationToken cancellationToken)
    {
        long seats = 0;
        string? pageToken = null;
        do
        {
            if (pacer is not null)
            {
                await pacer.WaitAsync(cancellationToken);
            }

            var request = service.Accounts.Customers.Entitlements.List(customerName);
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);
            foreach (var entitlement in response.Entitlements ?? [])
            {
                if (!string.Equals(entitlement.ProvisioningState, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var units = entitlement.Parameters?
                    .FirstOrDefault(p => string.Equals(p.Name, "num_units", StringComparison.OrdinalIgnoreCase))?.Value;
                if (units?.Int64Value is { } n)
                {
                    seats += n;
                }
            }
            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return seats;
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

    public async Task<Customer> GetChannelPartnerCustomerAsync(string linkId, string customerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkId);
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        EnsureAccountConfigured();
        using var service = CreateService();

        var response = await service.Accounts.ChannelPartnerLinks.Customers
            .Get(ChannelPartnerCustomerName(linkId, customerId))
            .ExecuteAsync(cancellationToken);

        return MapCustomer(response);
    }

    public async Task<Customer> CreateChannelPartnerCustomerAsync(string linkId, SaveCustomerRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OrgDisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Domain);
        EnsureAccountConfigured();
        using var service = CreateService();

        var body = ToGoogleCustomer(request);
        body.Domain = request.Domain;

        logger.LogInformation(
            "Creating customer {Org} ({Domain}) under channel partner link {Link}",
            request.OrgDisplayName, request.Domain, linkId);

        var response = await service.Accounts.ChannelPartnerLinks.Customers
            .Create(body, ChannelPartnerLinkName(linkId))
            .ExecuteAsync(cancellationToken);

        return MapCustomer(response);
    }

    public async Task<Customer> UpdateChannelPartnerCustomerAsync(string linkId, SaveCustomerRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OrgDisplayName);
        EnsureAccountConfigured();
        using var service = CreateService();

        var body = ToGoogleCustomer(request);

        var patch = service.Accounts.ChannelPartnerLinks.Customers
            .Patch(body, ChannelPartnerCustomerName(linkId, request.Id!));
        // Restrict the update to editable fields so the immutable domain/cloud-identity are untouched
        // (mirrors the direct-customer update mask).
        patch.UpdateMask = "org_display_name,org_postal_address,primary_contact_info,language_code";

        var response = await patch.ExecuteAsync(cancellationToken);
        return MapCustomer(response);
    }

    public async Task DeleteChannelPartnerCustomerAsync(string linkId, string customerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkId);
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        EnsureAccountConfigured();
        using var service = CreateService();

        await service.Accounts.ChannelPartnerLinks.Customers
            .Delete(ChannelPartnerCustomerName(linkId, customerId))
            .ExecuteAsync(cancellationToken);
    }

    public async Task<Customer> ImportChannelPartnerCustomerAsync(string linkId, ImportCustomerRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkId);
        ArgumentNullException.ThrowIfNull(request);
        EnsureAccountConfigured();
        using var service = CreateService();

        var body = ToGoogleImportCustomerRequest(request);

        logger.LogInformation(
            "Importing customer ({Identifier}) under channel partner link {Link}",
            request.Domain ?? request.CloudIdentityId ?? request.PrimaryAdminEmail, linkId);

        var response = await service.Accounts.ChannelPartnerLinks.Customers
            .Import(body, ChannelPartnerLinkName(linkId))
            .ExecuteAsync(cancellationToken);

        return MapCustomer(response);
    }
}
