using GChannel.ApiService.Configuration;
using GChannel.Shared.Contracts;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GChannel.ApiService.Services;

/// <summary>
/// Lists Marketplace orders per billing account via the Cloud Commerce Consumer Procurement API
/// (<c>cloudcommerceconsumerprocurement.googleapis.com</c>, <c>GET v1/billingAccounts/{id}/orders</c>).
/// Authenticated with the reseller service account's own IAM (no delegation); needs the
/// <c>consumerprocurement.orders.list</c> permission on the billing account (e.g. via a custom role or
/// <c>roles/consumerprocurement.orderViewer</c>) and the API enabled in the service account's project.
/// There is no official .NET client for this API, so it's called over REST.
/// </summary>
public interface IMarketplaceOrdersService
{
    bool IsConfigured { get; }

    /// <summary>Lists all orders on one billing account. Throws on permission / API errors so callers can record them per account.</summary>
    Task<IReadOnlyList<MarketplaceOrder>> ListOrdersAsync(string billingAccountId, CancellationToken cancellationToken);
}

public sealed class MarketplaceOrdersService(
    IOptions<GoogleChannelOptions> channelOptions,
    IHttpClientFactory httpClientFactory,
    ILogger<MarketplaceOrdersService> logger) : IMarketplaceOrdersService
{
    public const string HttpClientName = "consumer-procurement";
    private const string BaseUrl = "https://cloudcommerceconsumerprocurement.googleapis.com/v1/";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private GoogleCredential? _credential;

    public bool IsConfigured => channelOptions.Value.HasServiceAccountCredential;

    private GoogleCredential Credential
    {
        get
        {
            if (_credential is not null) { return _credential; }
            var o = channelOptions.Value;
            var baseCredential = string.IsNullOrWhiteSpace(o.ServiceAccountKeyJson)
                ? CredentialFactory.FromFile(o.ServiceAccountKeyPath, "service_account")
                : CredentialFactory.FromJson(o.ServiceAccountKeyJson, "service_account");
            return _credential = baseCredential.CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        }
    }

    public async Task<IReadOnlyList<MarketplaceOrder>> ListOrdersAsync(string billingAccountId, CancellationToken cancellationToken)
    {
        var token = await Credential.UnderlyingCredential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);
        var http = httpClientFactory.CreateClient(HttpClientName);
        var now = DateTimeOffset.UtcNow;
        var orders = new List<MarketplaceOrder>();
        string? pageToken = null;

        do
        {
            var url = $"{BaseUrl}billingAccounts/{Uri.EscapeDataString(billingAccountId)}/orders?pageSize=100"
                      + (pageToken is null ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var message = TryExtractError(body) ?? body;
                throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {message}", null, response.StatusCode);
            }

            var page = JsonSerializer.Deserialize<ListOrdersResponse>(body, Json) ?? new();
            foreach (var o in page.Orders ?? [])
            {
                orders.Add(ToContract(o, billingAccountId, now));
            }
            pageToken = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
        } while (pageToken is not null);

        logger.LogDebug("Marketplace orders for {Account}: {Count}", billingAccountId, orders.Count);
        return orders;
    }

    private static MarketplaceOrder ToContract(OrderDto o, string billingAccountId, DateTimeOffset now)
    {
        var items = o.LineItems ?? [];
        var products = items.Select(li => li.LineItemInfo?.ProductExternalName).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).Distinct().ToList();
        var offers = items.Select(li => li.LineItemInfo?.Offer).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).Distinct().ToList();
        // A cancelled order still lists line items; pending changes of type CANCEL mark it as going away.
        var active = items.Count > 0 && items.Any(li => !(li.PendingChange?.ChangeType?.Equals("CANCEL", StringComparison.OrdinalIgnoreCase) ?? false)
                                                         && !(li.ChangeHistory?.LastOrDefault()?.ChangeType?.Equals("CANCEL", StringComparison.OrdinalIgnoreCase) ?? false
                                                              && li.ChangeHistory?.LastOrDefault()?.ChangeState?.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase) == true));
        return new MarketplaceOrder
        {
            Name = o.Name ?? $"billingAccounts/{billingAccountId}/orders/?",
            BillingAccountId = billingAccountId,
            DisplayName = o.DisplayName,
            Products = products,
            Offers = offers,
            Provider = InferProvider(products.FirstOrDefault()),
            CreateTime = o.CreateTime,
            UpdateTime = o.UpdateTime,
            Active = active,
            LastSyncedUtc = now
        };
    }

    // Product external names look like "<product>.endpoints.<provider>.cloud.goog" or "<product>.<provider>.com".
    private static string? InferProvider(string? productExternalName)
    {
        if (string.IsNullOrEmpty(productExternalName)) { return null; }
        var parts = productExternalName.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var idx = Array.IndexOf(parts, "endpoints");
        if (idx >= 0 && idx + 1 < parts.Length) { return parts[idx + 1]; }
        return parts.Length >= 2 ? parts[^2] : parts.FirstOrDefault();
    }

    private static string? TryExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var msg) ? msg.GetString() : null;
        }
        catch { return null; }
    }

    private sealed class ListOrdersResponse
    {
        public List<OrderDto>? Orders { get; set; }
        public string? NextPageToken { get; set; }
    }

    private sealed class OrderDto
    {
        public string? Name { get; set; }
        public string? DisplayName { get; set; }
        public List<LineItemDto>? LineItems { get; set; }
        public DateTimeOffset? CreateTime { get; set; }
        public DateTimeOffset? UpdateTime { get; set; }
        public string? Etag { get; set; }
    }

    private sealed class LineItemDto
    {
        public string? LineItemId { get; set; }
        public LineItemInfoDto? LineItemInfo { get; set; }
        public ChangeDto? PendingChange { get; set; }
        public List<ChangeDto>? ChangeHistory { get; set; }
    }

    private sealed class LineItemInfoDto
    {
        public string? Offer { get; set; }
        public string? ProductExternalName { get; set; }
    }

    private sealed class ChangeDto
    {
        public string? ChangeId { get; set; }
        public string? ChangeType { get; set; }
        public string? ChangeState { get; set; }
    }
}
