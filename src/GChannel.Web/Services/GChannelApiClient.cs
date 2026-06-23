using System.Net.Http.Headers;
using System.Net.Http.Json;
using GChannel.Shared.Contracts;
using Microsoft.AspNetCore.Components.Authorization;

namespace GChannel.Web.Services;

/// <summary>
/// Typed client the UI uses to talk to the API service. It transparently attaches the
/// signed-in user's Google access token so Razor components never touch tokens or REST paths.
/// </summary>
public sealed class GChannelApiClient(
    HttpClient http,
    AuthenticationStateProvider authState,
    GoogleTokenProvider tokenProvider)
{
    public async Task<CheckCloudIdentityResult?> CheckCloudIdentityAsync(
        CheckCloudIdentityRequest request,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var route = forceRefresh
            ? $"{ApiRoutes.CheckCloudIdentity}?refresh=true"
            : ApiRoutes.CheckCloudIdentity;

        using var message = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(request)
        };

        await AttachGoogleTokenAsync(message);

        using var response = await http.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CheckCloudIdentityResult>(cancellationToken);
    }

    /// <summary>Lists recently checked domains (latest result per domain).</summary>
    public Task<IdentityCheckHistoryResult?> GetIdentityCheckHistoryAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IdentityCheckHistoryResult>(ApiRoutes.IdentityCheckHistory, cancellationToken);

    /// <summary>Lists the products the reseller is authorized to sell.</summary>
    public Task<CatalogProductsResult?> ListProductsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<CatalogProductsResult>(ApiRoutes.Products, cancellationToken);

    /// <summary>Lists the SKUs for a product.</summary>
    public Task<CatalogSkusResult?> ListSkusAsync(string productId, CancellationToken cancellationToken = default) =>
        GetAsync<CatalogSkusResult>(ApiRoutes.ProductSkus(productId), cancellationToken);

    /// <summary>Lists the offers the reseller can sell.</summary>
    public Task<CatalogOffersResult?> ListOffersAsync(CancellationToken cancellationToken = default) =>
        GetAsync<CatalogOffersResult>(ApiRoutes.Offers, cancellationToken);

    /// <summary>Lists the rebilling-supported SKU groups.</summary>
    public Task<CatalogSkuGroupsResult?> ListSkuGroupsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<CatalogSkuGroupsResult>(ApiRoutes.SkuGroups, cancellationToken);

    /// <summary>Lists the billable SKUs in a SKU group.</summary>
    public Task<CatalogBillableSkusResult?> ListBillableSkusAsync(string skuGroupId, CancellationToken cancellationToken = default) =>
        GetAsync<CatalogBillableSkusResult>(ApiRoutes.BillableSkus(skuGroupId), cancellationToken);

    /// <summary>Lists the reseller's customers.</summary>
    public Task<CustomersResult?> ListCustomersAsync(CancellationToken cancellationToken = default) =>
        GetAsync<CustomersResult>(ApiRoutes.Customers, cancellationToken);

    /// <summary>Gets a single customer.</summary>
    public Task<Customer?> GetCustomerAsync(string customerId, CancellationToken cancellationToken = default) =>
        GetAsync<Customer>(ApiRoutes.Customer(customerId), cancellationToken);

    /// <summary>Creates a customer and returns the created resource.</summary>
    public Task<Customer?> CreateCustomerAsync(SaveCustomerRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<Customer>(HttpMethod.Post, ApiRoutes.Customers, request, cancellationToken);

    /// <summary>Updates a customer and returns the updated resource.</summary>
    public Task<Customer?> UpdateCustomerAsync(string customerId, SaveCustomerRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<Customer>(HttpMethod.Put, ApiRoutes.Customer(customerId), request, cancellationToken);

    /// <summary>Deletes a customer.</summary>
    public async Task DeleteCustomerAsync(string customerId, CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Delete, ApiRoutes.Customer(customerId));
        await AttachGoogleTokenAsync(message);

        using var response = await http.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Lists the SKUs a customer is eligible to purchase within a product.</summary>
    public Task<PurchasableSkusResult?> ListPurchasableSkusAsync(string customerId, string productId, CancellationToken cancellationToken = default) =>
        GetAsync<PurchasableSkusResult>(ApiRoutes.CustomerPurchasableSkus(customerId, productId), cancellationToken);

    /// <summary>Lists the offers a customer is eligible to purchase for a SKU.</summary>
    public Task<PurchasableOffersResult?> ListPurchasableOffersAsync(string customerId, string productId, string skuId, CancellationToken cancellationToken = default) =>
        GetAsync<PurchasableOffersResult>(ApiRoutes.CustomerPurchasableOffers(customerId, productId, skuId), cancellationToken);

    private async Task<T?> GetAsync<T>(string route, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, route);
        await AttachGoogleTokenAsync(message);

        using var response = await http.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string route, object body, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(method, route)
        {
            Content = JsonContent.Create(body)
        };
        await AttachGoogleTokenAsync(message);

        using var response = await http.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    private async Task AttachGoogleTokenAsync(HttpRequestMessage message)
    {
        var state = await authState.GetAuthenticationStateAsync();
        var token = await tokenProvider.GetAccessTokenAsync(state.User);
        if (!string.IsNullOrEmpty(token))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var email = state.User.Identity?.Name;
        if (!string.IsNullOrEmpty(email))
        {
            message.Headers.Add("X-User-Email", email);
        }
    }
}
