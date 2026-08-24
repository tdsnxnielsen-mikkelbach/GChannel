using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using GChannel.ApiService.Configuration;
using Microsoft.Extensions.Options;

namespace GChannel.ApiService.Services;

/// <summary>
/// Client for the Ion Gateway (TD SYNNEX StreamOne). GChannel is a distributor selling through indirect
/// resellers, and Ion cost/margin is <b>reseller + SKU level</b>, sourced from each reseller's orders — the
/// <c>/customers/*</c> endpoints only address Ion's small set of direct customers, not our resellers. So we
/// match a reseller to an Ion account by <b>email domain</b> (== our channel-partner link's primary domain),
/// then read that account's order lines. Only Google <b>Workspace</b> lines are priced; GCP is
/// consumption-billed and null here (its real cost is the §15 BigQuery export). Singleton over
/// <see cref="IHttpClientFactory"/> so it is safe to inject into the singleton <see cref="ReadModelProjector"/>;
/// every call is best-effort and returns empty on failure.
/// </summary>
public interface IIonGatewayClient
{
    bool IsConfigured { get; }

    /// <summary>
    /// Returns priced Google cost/margin entries for a reseller (matched to an Ion account by email domain),
    /// from that reseller's Ion orders. Empty when unconfigured, unmatched, or the reseller has no priced Google lines.
    /// </summary>
    Task<IReadOnlyList<IonGoogleCost>> GetResellerGoogleCostsAsync(string resellerDomain, CancellationToken cancellationToken);
}

public sealed class IonGatewayClient(
    IHttpClientFactory httpClientFactory,
    IOptions<IonGatewayOptions> options,
    ILogger<IonGatewayClient> logger) : IIonGatewayClient
{
    public const string HttpClientName = "IonGateway";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan DirectoryTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan OrdersTtl = TimeSpan.FromMinutes(15);

    // Reseller directory (email domain -> Ion accountId), refreshed hourly; and per-account order costs,
    // cached so a read-model cycle fetches each reseller's orders once instead of once per end customer.
    private readonly SemaphoreSlim _directoryLock = new(1, 1);
    private Dictionary<string, long>? _domainToAccount;
    private DateTimeOffset _directoryExpires;
    private readonly ConcurrentDictionary<long, (DateTimeOffset Expires, IReadOnlyList<IonGoogleCost> Costs)> _ordersCache = new();

    public bool IsConfigured => options.Value.IsConfigured;

    public async Task<IReadOnlyList<IonGoogleCost>> GetResellerGoogleCostsAsync(string resellerDomain, CancellationToken cancellationToken)
    {
        var opts = options.Value;
        if (!opts.IsConfigured || string.IsNullOrWhiteSpace(resellerDomain))
        {
            return [];
        }

        try
        {
            var directory = await GetDirectoryAsync(opts, cancellationToken);
            if (directory is null || !directory.TryGetValue(resellerDomain.Trim().ToLowerInvariant(), out var accountId))
            {
                return [];
            }
            return await GetOrderCostsAsync(opts, accountId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ion reseller cost lookup failed for domain {Domain}; leaving Ion fields unset.", resellerDomain);
            return [];
        }
    }

    // Email domain -> Ion accountId, from the reseller directory (cached hourly; ~195 rows).
    private async Task<Dictionary<string, long>?> GetDirectoryAsync(IonGatewayOptions opts, CancellationToken ct)
    {
        if (_domainToAccount is not null && _directoryExpires > DateTimeOffset.UtcNow)
        {
            return _domainToAccount;
        }

        await _directoryLock.WaitAsync(ct);
        try
        {
            if (_domainToAccount is not null && _directoryExpires > DateTimeOffset.UtcNow)
            {
                return _domainToAccount;
            }

            using var client = CreateClient(opts);
            var resellers = await client.GetFromJsonAsync<List<IonReseller>>("/api/v1/resellers/", JsonOptions, ct) ?? [];
            var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in resellers)
            {
                var domain = EmailDomain(r.PrimaryContactEmail);
                if (domain is not null) { map[domain] = r.AccountId; }
            }
            _domainToAccount = map;
            _directoryExpires = DateTimeOffset.UtcNow.Add(DirectoryTtl);
            return map;
        }
        finally
        {
            _directoryLock.Release();
        }
    }

    // Priced Google order lines for one Ion account, indexed later by normalised SKU name.
    private async Task<IReadOnlyList<IonGoogleCost>> GetOrderCostsAsync(IonGatewayOptions opts, long accountId, CancellationToken ct)
    {
        if (_ordersCache.TryGetValue(accountId, out var hit) && hit.Expires > DateTimeOffset.UtcNow)
        {
            return hit.Costs;
        }

        using var client = CreateClient(opts);
        var groups = await client.GetFromJsonAsync<List<IonResellerOrdersGroup>>($"/api/v1/resellers/{accountId}/orders", JsonOptions, ct) ?? [];
        var costs = new List<IonGoogleCost>();
        foreach (var group in groups)
        {
            foreach (var order in group.Orders ?? [])
            {
                foreach (var line in order.Lines ?? [])
                {
                    if (line.Cost is not { } cost) { continue; } // GCP lines are null (consumption-billed → §15)
                    var isGoogle = Contains(line.ProductName, "google")
                        || Contains(line.SkuName, "google") || Contains(line.SkuName, "workspace")
                        || (line.MfgPartNumber?.StartsWith("GCP", StringComparison.OrdinalIgnoreCase) ?? false);
                    if (!isGoogle) { continue; }

                    var nameKey = NormalizeSkuName(line.SkuName ?? line.SkuId);
                    if (nameKey.Length == 0) { continue; }

                    decimal? marginPct = line.Price is { } p && p > 0m ? Math.Round((p - cost) / p * 100m, 2) : null;
                    costs.Add(new IonGoogleCost(nameKey, line.SkuName ?? line.SkuId, line.MfgPartNumber, cost, marginPct, order.CurrencyCode));
                }
            }
        }

        IReadOnlyList<IonGoogleCost> result = costs;
        _ordersCache[accountId] = (DateTimeOffset.UtcNow.Add(OrdersTtl), result);
        return result;
    }

    private HttpClient CreateClient(IonGatewayOptions opts)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        client.BaseAddress = new Uri(opts.BaseUrl);
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        client.DefaultRequestHeaders.Add("X-Api-Key", opts.ApiKey);
        return client;
    }

    private static string? EmailDomain(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) { return null; }
        var at = email.LastIndexOf('@');
        return at >= 0 && at < email.Length - 1 ? email[(at + 1)..].Trim().ToLowerInvariant() : null;
    }

    private static bool Contains(string? value, string term) =>
        value is not null && value.Contains(term, StringComparison.OrdinalIgnoreCase);

    /// <summary>Normalises a SKU name/id to a comparable key (lower-case, alphanumeric only) so friendly names match across sources.</summary>
    public static string NormalizeSkuName(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? string.Empty
            : new string([.. name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}

/// <summary>A priced Google order line from Ion, reduced to what GChannel consumes. Money is per unit (guide §3).</summary>
public sealed record IonGoogleCost(string SkuNameKey, string? SkuName, string? MfgPartNumber, decimal Cost, decimal? MarginPercent, string? Currency);

// Ion reseller + orders DTOs — a trimmed subset. JSON is camelCase (JsonSerializerDefaults.Web).

internal sealed record IonReseller
{
    public long AccountId { get; init; }
    public string? Name { get; init; }
    public string? PrimaryContactEmail { get; init; }
    public bool DelegatedAdminEnabled { get; init; }
}

internal sealed record IonResellerOrdersGroup
{
    public IReadOnlyList<IonResellerOrder>? Orders { get; init; }
}

internal sealed record IonResellerOrder
{
    public string? CurrencyCode { get; init; }
    public IReadOnlyList<IonOrderLine>? Lines { get; init; }
}

internal sealed record IonOrderLine
{
    public string? ProductId { get; init; }
    public string? SkuId { get; init; }
    public string? MfgPartNumber { get; init; }
    public string? ProductName { get; init; }
    public string? SkuName { get; init; }
    public int Quantity { get; init; }
    public decimal? Price { get; init; }
    public decimal? Cost { get; init; }
    public decimal? Msrp { get; init; }
    public decimal? Margin { get; init; }
}
