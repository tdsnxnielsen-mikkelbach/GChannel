using GChannel.Core.Configuration;
using GChannel.Core.Persistence.Documents;
using GChannel.Shared.Contracts;
using Marten;
using Microsoft.Extensions.Options;
using Wolverine.Http;

namespace GChannel.ApiService.Endpoints;

/// <summary>
/// Native Google vs third-party (Marketplace / ISV / partner) views: spend split from the export's
/// transaction_type / seller_name (CustomerSpendBySeller documents) and Marketplace orders from the Consumer
/// Procurement API (MarketplaceOrder documents). Read-only; populated by the worker.
/// </summary>
public static class SellerSpendEndpoints
{
    public const string OrdersCursorScope = "marketplace-orders";

    [WolverineGet("/api/billing/spend/sellers")]
    public static Task<SellerSpendSummary> SellerSpend(int? months, IQuerySession session, IOptions<GoogleBillingOptions> options, CancellationToken ct)
        => SummaryAsync(session, options.Value, customerId: null, months, ct);

    [WolverineGet("/api/billing/customers/{customerId}/spend/sellers")]
    public static Task<SellerSpendSummary> CustomerSellerSpend(
        string customerId, int? months, IQuerySession session, IOptions<GoogleBillingOptions> options, CancellationToken ct)
        => SummaryAsync(session, options.Value, customerId, months, ct);

    private static async Task<SellerSpendSummary> SummaryAsync(IQuerySession session, GoogleBillingOptions opts, string? customerId, int? months, CancellationToken ct)
    {
        if (!opts.BigQueryConfigured) { return new SellerSpendSummary { CustomerId = customerId, Unconfigured = true }; }

        var window = BillingEndpoints.MonthKeys(Math.Clamp(months ?? (customerId is null ? 1 : 3), 1, 36));
        var query = session.Query<CustomerSpendBySellerRecord>().Where(r => r.InvoiceMonth.IsOneOf(window));
        if (customerId is not null) { query = query.Where(r => r.CustomerId == customerId); }
        var rows = await query.ToListAsync(ct);

        static SellerSpendLine Line(IGrouping<(SellerCategory Category, string? Seller, string? Service, string Currency), CustomerSpendBySellerRecord> g) => new()
        {
            Category = g.Key.Category,
            SellerName = g.Key.Seller,
            ServiceDescription = g.Key.Service,
            Currency = g.Key.Currency,
            Cost = g.Sum(x => x.Cost),
            GrossCost = g.Sum(x => x.GrossCost),
            Credits = g.Sum(x => x.Credits)
        };

        var byCategory = rows
            .GroupBy(r => (SellerCategories.Parse(r.TransactionType), (string?)null, (string?)null, r.Currency))
            .Select(Line)
            .OrderBy(l => l.Category).ThenBy(l => l.Currency)
            .ToList();

        var thirdParty = rows.Where(r => SellerCategories.IsThirdParty(SellerCategories.Parse(r.TransactionType))).ToList();

        var sellers = thirdParty
            .GroupBy(r => (SellerCategories.Parse(r.TransactionType), (string?)(r.SellerName ?? "(unnamed seller)"), (string?)null, r.Currency))
            .Select(Line)
            .OrderByDescending(l => l.Cost)
            .ToList();

        var products = thirdParty
            .GroupBy(r => (SellerCategories.Parse(r.TransactionType), (string?)r.SellerName, (string?)(r.ServiceDescription ?? "(unnamed product)"), r.Currency))
            .Select(Line)
            .OrderByDescending(l => l.Cost)
            .ToList();

        return new SellerSpendSummary
        {
            CustomerId = customerId,
            InvoiceMonths = rows.Select(r => r.InvoiceMonth).Distinct().OrderBy(m => m).ToList(),
            ByCategory = byCategory,
            ThirdPartySellers = sellers,
            ThirdPartyProducts = products,
            AsOf = rows.Count > 0 ? rows.Max(r => r.LastSyncedUtc) : null
        };
    }

    [WolverineGet("/api/billing/spend/sellers/customers")]
    public static async Task<CustomerSellerSplitResult> GetCustomerSplits(int? months, IQuerySession session, IOptions<GoogleBillingOptions> options, CancellationToken ct)
    {
        if (!options.Value.BigQueryConfigured) { return new CustomerSellerSplitResult { Unconfigured = true }; }

        var window = BillingEndpoints.MonthKeys(Math.Clamp(months ?? 1, 1, 36));
        var rows = await session.Query<CustomerSpendBySellerRecord>()
            .Where(r => r.CustomerId != null && r.InvoiceMonth.IsOneOf(window))
            .ToListAsync(ct);

        var customers = rows
            .GroupBy(r => (r.CustomerId!, r.Currency))
            .Select(g => new CustomerSellerSplit
            {
                CustomerId = g.Key.Item1,
                OwningLinkId = g.Select(x => x.OwningLinkId).FirstOrDefault(x => x != null),
                Currency = g.Key.Currency,
                GoogleCost = g.Where(x => SellerCategories.Parse(x.TransactionType) == SellerCategory.Google).Sum(x => x.Cost),
                ThirdPartyCost = g.Where(x => SellerCategories.IsThirdParty(SellerCategories.Parse(x.TransactionType))).Sum(x => x.Cost),
                Sellers = g.Where(x => SellerCategories.IsThirdParty(SellerCategories.Parse(x.TransactionType)) && x.SellerName != null)
                           .Select(x => x.SellerName!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList()
            })
            .OrderByDescending(c => c.ThirdPartyCost)
            .ThenByDescending(c => c.TotalCost)
            .ToList();

        return new CustomerSellerSplitResult { Customers = customers, AsOf = rows.Count > 0 ? rows.Max(r => r.LastSyncedUtc) : null };
    }

    [WolverineGet("/api/billing/marketplace/orders")]
    public static async Task<MarketplaceOrdersResult> GetOrders(
        string? customerId, IQuerySession session, IOptions<GoogleChannelOptions> channel, IOptions<GoogleBillingOptions> billing, CancellationToken ct)
    {
        var unconfigured = !billing.Value.MarketplaceOrdersEnabled || !channel.Value.HasServiceAccountCredential;
        var query = session.Query<MarketplaceOrderRecord>().AsQueryable();
        if (customerId is not null) { query = query.Where(o => o.CustomerId == customerId); }
        var rows = (await query.ToListAsync(ct))
            .OrderByDescending(o => o.Active).ThenBy(o => o.Provider).ThenBy(o => o.DisplayName)
            .ToList();

        var cursor = await session.LoadAsync<SyncCursor>(OrdersCursorScope, ct);
        var errors = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(cursor?.Notes))
        {
            foreach (var part in cursor.Notes.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = part.IndexOf('=');
                if (idx > 0) { errors[part[..idx]] = part[(idx + 1)..]; }
            }
        }

        return new MarketplaceOrdersResult
        {
            Orders = rows.Select(r => new MarketplaceOrder
            {
                Name = r.Name,
                BillingAccountId = r.BillingAccountId,
                CustomerId = r.CustomerId,
                DisplayName = r.DisplayName,
                Provider = r.Provider,
                Products = Split(r.ProductsCsv),
                Offers = Split(r.OffersCsv),
                CreateTime = r.CreateTime,
                UpdateTime = r.UpdateTime,
                Active = r.Active,
                LastSyncedUtc = r.LastSyncedUtc
            }).ToList(),
            Errors = errors,
            AsOf = cursor?.LastFullPassUtc,
            Unconfigured = unconfigured
        };
    }

    private static IReadOnlyList<string> Split(string? csv) =>
        string.IsNullOrEmpty(csv) ? [] : csv.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
