using GChannel.ApiService.Configuration;
using GChannel.ApiService.Data;
using GChannel.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Globalization;

namespace GChannel.ApiService.Endpoints;

/// <summary>
/// Native Google vs third-party (Marketplace / ISV / partner) views: spend split from the export's
/// transaction_type / seller_name (CustomerSpendBySeller read-model) and Marketplace orders from the
/// Consumer Procurement API (MarketplaceOrders read-model). Read-only; populated by the worker.
/// </summary>
public static class SellerSpendEndpoints
{
    public static IEndpointRouteBuilder MapSellerSpendEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/billing").WithTags("Billing sellers");

        group.MapGet("/spend/sellers", async (int? months, GChannelDbContext db, IOptions<GoogleBillingOptions> options, CancellationToken ct) =>
                Results.Ok(await SummaryAsync(db, options.Value, customerId: null, months, ct)))
            .WithName("SellerSpend")
            .WithSummary("Estate-wide native Google vs third-party spend with per-seller / per-product breakdown.");

        group.MapGet("/customers/{customerId}/spend/sellers", async (string customerId, int? months, GChannelDbContext db, IOptions<GoogleBillingOptions> options, CancellationToken ct) =>
                Results.Ok(await SummaryAsync(db, options.Value, customerId, months, ct)))
            .WithName("CustomerSellerSpend")
            .WithSummary("One customer's native Google vs third-party spend with per-seller / per-product breakdown.");

        group.MapGet("/spend/sellers/customers", GetCustomerSplitsAsync)
            .WithName("CustomerSellerSplits")
            .WithSummary("Per-customer Google vs third-party split over the window.");

        group.MapGet("/marketplace/orders", GetOrdersAsync)
            .WithName("MarketplaceOrders")
            .WithSummary("Marketplace orders (Consumer Procurement API) across billing accounts, optionally for one customer.");

        return app;
    }

    private static string MinMonth(int? months, int def, int max) =>
        DateTime.UtcNow.AddMonths(-(Math.Clamp(months ?? def, 1, max) - 1)).ToString("yyyyMM", CultureInfo.InvariantCulture);

    private static async Task<SellerSpendSummary> SummaryAsync(GChannelDbContext db, GoogleBillingOptions opts, string? customerId, int? months, CancellationToken ct)
    {
        if (!opts.BigQueryConfigured) { return new SellerSpendSummary { CustomerId = customerId, Unconfigured = true }; }

        var minMonth = MinMonth(months, customerId is null ? 1 : 3, 36);
        var query = db.CustomerSpendBySeller.AsNoTracking().Where(r => string.Compare(r.InvoiceMonth, minMonth) >= 0);
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

    private static async Task<IResult> GetCustomerSplitsAsync(int? months, GChannelDbContext db, IOptions<GoogleBillingOptions> options, CancellationToken ct)
    {
        if (!options.Value.BigQueryConfigured) { return Results.Ok(new CustomerSellerSplitResult { Unconfigured = true }); }

        var minMonth = MinMonth(months, 1, 36);
        var rows = await db.CustomerSpendBySeller.AsNoTracking()
            .Where(r => r.CustomerId != null && string.Compare(r.InvoiceMonth, minMonth) >= 0)
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

        return Results.Ok(new CustomerSellerSplitResult
        {
            Customers = customers,
            AsOf = rows.Count > 0 ? rows.Max(r => r.LastSyncedUtc) : null
        });
    }

    private static async Task<IResult> GetOrdersAsync(string? customerId, GChannelDbContext db, IOptions<GoogleChannelOptions> channel, IOptions<GoogleBillingOptions> billing, CancellationToken ct)
    {
        var unconfigured = !billing.Value.MarketplaceOrdersEnabled || !channel.Value.HasServiceAccountCredential;
        var query = db.MarketplaceOrders.AsNoTracking();
        if (customerId is not null) { query = query.Where(o => o.CustomerId == customerId); }
        var rows = await query.OrderByDescending(o => o.Active).ThenBy(o => o.Provider).ThenBy(o => o.DisplayName).ToListAsync(ct);

        var cursor = await db.SyncCursors.AsNoTracking().FirstOrDefaultAsync(c => c.Scope == "marketplace-orders", ct);
        var errors = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(cursor?.Notes))
        {
            foreach (var part in cursor.Notes.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = part.IndexOf('=');
                if (idx > 0) { errors[part[..idx]] = part[(idx + 1)..]; }
            }
        }

        return Results.Ok(new MarketplaceOrdersResult
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
        });
    }

    private static IReadOnlyList<string> Split(string? csv) =>
        string.IsNullOrEmpty(csv) ? [] : csv.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
