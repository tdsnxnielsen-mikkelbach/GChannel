using GChannel.Core.Configuration;
using GChannel.Core.Estate;
using GChannel.Core.Estate.ReadModels;
using GChannel.Core.GoogleApis;
using GChannel.Core.Persistence.Documents;
using GChannel.Shared.Contracts;
using Marten;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Globalization;
using Wolverine.Http;

namespace GChannel.ApiService.Endpoints;

/// <summary>
/// Cloud Billing budget endpoints (billingbudgets.googleapis.com) plus the actual-spend views served from the
/// CustomerSpend / SpendDaily read-model documents the worker's billing-export sync populates (BILLED figures,
/// distinct from the estimated estate value).
/// </summary>
public static class BillingEndpoints
{
    [WolverineGet("/api/billing/accounts")]
    public static async Task<BillingAccountsResult> ListBillingAccounts(IBillingBudgetsService billing, CancellationToken cancellationToken)
    {
        if (!billing.IsConfigured)
        {
            return new BillingAccountsResult
            {
                DiscoveryAvailable = false,
                DiscoveryError = "No service-account credential configured (GoogleChannel:ServiceAccountKeyJson)."
            };
        }
        return await billing.ListBillingAccountsAsync(cancellationToken);
    }

    [WolverineGet("/api/billing/accounts/{billingAccountId}/budgets")]
    public static async Task<IResult> ListBudgets(string billingAccountId, IBillingBudgetsService billing, CancellationToken cancellationToken)
    {
        if (!billing.IsConfigured)
        {
            return Results.Problem("Billing budgets are not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        return Results.Ok(await billing.ListBudgetsAsync(billingAccountId, cancellationToken));
    }

    [WolverineGet("/api/billing/accounts/{billingAccountId}/members")]
    public static async Task<BillingAccountMembersResult> ListBillingAccountMembers(
        string billingAccountId, IBillingBudgetsService billing, CancellationToken cancellationToken)
    {
        if (!billing.IsConfigured)
        {
            return new BillingAccountMembersResult
            {
                BillingAccountId = billingAccountId,
                Available = false,
                Error = "No service-account credential configured (GoogleChannel:ServiceAccountKeyJson)."
            };
        }
        return await billing.GetBillingAccountMembersAsync(billingAccountId, cancellationToken);
    }

    private const int MaxIdentityBatch = 100;

    /// <summary>On-demand identity + security posture for a batch of member emails; see <see cref="MemberIdentityResolver"/>.</summary>
    [WolverinePost("/api/billing/members/identity")]
    public static Task<MemberIdentityResult> ResolveMemberIdentities(
        MemberIdentityRequest request, [FromServices] MemberIdentityResolver resolver, IGoogleChannelClient channel,
        IQuerySession session, CancellationToken cancellationToken)
        => resolver.ResolveAsync(request.Emails.Take(MaxIdentityBatch), channel, session, request.Refresh, cancellationToken);

    [WolverinePost("/api/billing/budgets")]
    public static async Task<IResult> SaveBudget(SaveBudgetRequest request, IBillingBudgetsService billing, CancellationToken cancellationToken)
    {
        if (!billing.IsConfigured)
        {
            return Results.Problem("Billing budgets are not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        return Results.Ok(await billing.SaveBudgetAsync(request, cancellationToken));
    }

    [WolverineDelete("/api/billing/accounts/{billingAccountId}/budgets/{budgetId}")]
    public static async Task<IResult> DeleteBudget(
        string billingAccountId, string budgetId, IBillingBudgetsService billing, CancellationToken cancellationToken)
    {
        if (!billing.IsConfigured)
        {
            return Results.Problem("Billing budgets are not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        await billing.DeleteBudgetAsync(billingAccountId, budgetId, cancellationToken);
        return Results.NoContent();
    }

    // ---- Actual spend (BigQuery detailed billing export) --------------------------------------------

    // Bulk last-window billed totals per customer (for the customer-list "Billed" column).
    [WolverineGet("/api/billing/customers/spend")]
    public static async Task<CustomerBilledSummaryResult> CustomerBilledSummaries(
        int? months, IQuerySession session, IOptions<GoogleBillingOptions> options, CancellationToken cancellationToken)
    {
        if (!options.Value.BigQueryConfigured)
        {
            return new CustomerBilledSummaryResult { Unconfigured = true };
        }

        var window = MonthKeys(Math.Clamp(months ?? 1, 1, 12));
        var rows = await session.Query<CustomerSpendRecord>()
            .Where(s => s.CustomerId != null && s.InvoiceMonth.IsOneOf(window))
            .ToListAsync(cancellationToken);

        return new CustomerBilledSummaryResult
        {
            Customers = rows
                .GroupBy(r => new { r.CustomerId, r.Currency })
                .Select(g => new CustomerBilledSummary { CustomerId = g.Key.CustomerId!, Currency = g.Key.Currency, Cost = g.Sum(x => x.Cost) })
                .ToList(),
            AsOf = rows.Count > 0 ? rows.Max(r => r.LastSyncedUtc) : null
        };
    }

    [WolverineGet("/api/billing/customers/{customerId}/spend")]
    public static async Task<CustomerSpendResult> CustomerSpend(
        string customerId, int? months, IQuerySession session, IOptions<GoogleBillingOptions> options, CancellationToken cancellationToken)
    {
        if (!options.Value.BigQueryConfigured)
        {
            return new CustomerSpendResult { CustomerId = customerId, Unconfigured = true };
        }

        var window = MonthKeys(Math.Clamp(months ?? 13, 1, 36));
        var rows = await session.Query<CustomerSpendRecord>()
            .Where(s => s.CustomerId == customerId && s.InvoiceMonth.IsOneOf(window))
            .ToListAsync(cancellationToken);

        return new CustomerSpendResult
        {
            CustomerId = customerId,
            Months = rows
                .OrderBy(r => r.InvoiceMonth).ThenBy(r => r.Currency)
                .Select(r => new CustomerSpendPoint
                {
                    InvoiceMonth = r.InvoiceMonth,
                    Currency = r.Currency,
                    Cost = r.Cost,
                    GrossCost = r.GrossCost,
                    Credits = r.Credits
                })
                .ToList(),
            AsOf = rows.Count > 0 ? rows.Max(r => r.LastSyncedUtc) : null
        };
    }

    [WolverineGet("/api/billing/customers/{customerId}/trend")]
    public static async Task<SpendTrendResult> CustomerSpendTrend(
        string customerId, int? days, IQuerySession session, IOptions<GoogleBillingOptions> options, CancellationToken cancellationToken)
    {
        var opts = options.Value;
        if (!opts.BigQueryConfigured)
        {
            return new SpendTrendResult { CustomerId = customerId, Unconfigured = true };
        }

        var n = Math.Clamp(days ?? opts.TrendDays, 1, 365);
        var minDay = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-(n - 1)));
        var rows = await session.Query<SpendDailyRecord>()
            .Where(d => d.CustomerId == customerId && d.Day >= minDay)
            .ToListAsync(cancellationToken);

        var points = new List<SpendTrendPoint>();
        foreach (var byCurrency in rows.GroupBy(r => r.Currency))
        {
            var ordered = byCurrency.OrderBy(r => r.Day).ToList();
            var flags = DetectSpikes([.. ordered.Select(r => (double)r.Cost)], opts.SpikeWindowDays, opts.SpikeStdDevK);
            for (var i = 0; i < ordered.Count; i++)
            {
                points.Add(new SpendTrendPoint { Day = ordered[i].Day, Currency = byCurrency.Key, Cost = ordered[i].Cost, IsSpike = flags[i] });
            }
        }

        return new SpendTrendResult
        {
            CustomerId = customerId,
            Days = [.. points.OrderBy(p => p.Day).ThenBy(p => p.Currency)],
            AsOf = rows.Count > 0 ? rows.Max(r => r.LastSyncedUtc) : null
        };
    }

    [WolverineGet("/api/billing/spend/top")]
    public static async Task<TopSpendersResult> TopSpenders(
        int? months, int? limit, IQuerySession session, IOptions<GoogleBillingOptions> options, CancellationToken cancellationToken)
    {
        if (!options.Value.BigQueryConfigured)
        {
            return new TopSpendersResult { Unconfigured = true };
        }

        var window = MonthKeys(Math.Clamp(months ?? 1, 1, 12));
        var lim = Math.Clamp(limit ?? 10, 1, 100);
        var rows = await session.Query<CustomerSpendRecord>()
            .Where(s => s.CustomerId != null && s.InvoiceMonth.IsOneOf(window))
            .ToListAsync(cancellationToken);

        var top = rows
            .GroupBy(r => new { r.CustomerId, r.OwningLinkId, r.Currency })
            .Select(g => new TopSpender { CustomerId = g.Key.CustomerId!, OwningLinkId = g.Key.OwningLinkId, Currency = g.Key.Currency, Cost = g.Sum(x => x.Cost) })
            .OrderByDescending(t => t.Cost)
            .Take(lim)
            .ToList();

        var names = await ResolveResellerNamesAsync(session, top.Select(t => t.OwningLinkId), cancellationToken);
        top = top.Select(t => t.OwningLinkId != null && names.TryGetValue(t.OwningLinkId, out var rn) ? t with { ResellerName = rn } : t).ToList();

        return new TopSpendersResult { Spenders = top, AsOf = rows.Count > 0 ? rows.Max(r => r.LastSyncedUtc) : null };
    }

    [WolverineGet("/api/billing/resellers/spend")]
    public static async Task<ResellerSpendResult> ResellerSpend(
        int? months, IQuerySession session, IOptions<GoogleBillingOptions> options, CancellationToken cancellationToken)
    {
        if (!options.Value.BigQueryConfigured)
        {
            return new ResellerSpendResult { Unconfigured = true };
        }

        var window = MonthKeys(Math.Clamp(months ?? 1, 1, 12));
        var rows = await session.Query<CustomerSpendRecord>()
            .Where(s => s.InvoiceMonth.IsOneOf(window))
            .ToListAsync(cancellationToken);

        var resellers = rows
            .GroupBy(r => new { r.OwningLinkId, r.Currency })
            .Select(g => new ResellerSpend
            {
                OwningLinkId = g.Key.OwningLinkId,
                Currency = g.Key.Currency,
                Cost = g.Sum(x => x.Cost),
                CustomerCount = g.Select(x => x.CustomerId).Where(c => c != null).Distinct().Count()
            })
            .OrderByDescending(r => r.Cost)
            .ToList();

        var names = await ResolveResellerNamesAsync(session, resellers.Select(r => r.OwningLinkId), cancellationToken);
        resellers = resellers.Select(r => r.OwningLinkId != null && names.TryGetValue(r.OwningLinkId, out var rn) ? r with { ResellerName = rn } : r).ToList();

        return new ResellerSpendResult { Resellers = resellers, AsOf = rows.Count > 0 ? rows.Max(r => r.LastSyncedUtc) : null };
    }

    [WolverineGet("/api/billing/resellers/{linkId}/spend")]
    public static async Task<ResellerSpendDetailResult> ResellerSpendDetail(
        string linkId, int? months, IQuerySession session, IOptions<GoogleBillingOptions> options, CancellationToken cancellationToken)
    {
        if (!options.Value.BigQueryConfigured)
        {
            return new ResellerSpendDetailResult { LinkId = linkId, Unconfigured = true };
        }

        var window = MonthKeys(Math.Clamp(months ?? 13, 1, 36));
        var rows = await session.Query<CustomerSpendRecord>()
            .Where(s => s.OwningLinkId == linkId && s.InvoiceMonth.IsOneOf(window))
            .ToListAsync(cancellationToken);

        return new ResellerSpendDetailResult
        {
            LinkId = linkId,
            Months = rows
                .GroupBy(r => new { r.InvoiceMonth, r.Currency })
                .Select(g => new CustomerSpendPoint
                {
                    InvoiceMonth = g.Key.InvoiceMonth,
                    Currency = g.Key.Currency,
                    Cost = g.Sum(x => x.Cost),
                    GrossCost = g.Sum(x => x.GrossCost),
                    Credits = g.Sum(x => x.Credits)
                })
                .OrderBy(p => p.InvoiceMonth).ThenBy(p => p.Currency)
                .ToList(),
            AsOf = rows.Count > 0 ? rows.Max(r => r.LastSyncedUtc) : null
        };
    }

    /// <summary>The <c>YYYYMM</c> keys of the last <paramref name="months"/> invoice months including the current one.</summary>
    internal static string[] MonthKeys(int months)
    {
        var now = DateTime.UtcNow;
        return Enumerable.Range(0, months)
            .Select(i => now.AddMonths(-i).ToString("yyyyMM", CultureInfo.InvariantCulture))
            .ToArray();
    }

    // Friendly reseller name per link id (primary domain, else reseller cloud id, else the raw link id).
    private static async Task<Dictionary<string, string>> ResolveResellerNamesAsync(IQuerySession session, IEnumerable<string?> linkIds, CancellationToken ct)
    {
        var ids = linkIds.Where(id => id != null).Select(id => id!).Distinct().ToList();
        if (ids.Count == 0) { return []; }
        var links = await session.Query<ResellerLinkRecord>().Where(l => l.LinkId.IsOneOf(ids)).ToListAsync(ct);
        return links.ToDictionary(l => l.LinkId, EstateRollups.LinkLabel, StringComparer.OrdinalIgnoreCase);
    }

    // Flags a day whose cost exceeds mean + k·stddev over the preceding rolling window (prior days only, so a
    // spike doesn't mask itself).
    private static bool[] DetectSpikes(IReadOnlyList<double> costs, int window, double k)
    {
        var flags = new bool[costs.Count];
        if (window < 2)
        {
            return flags;
        }

        for (var i = 0; i < costs.Count; i++)
        {
            var start = Math.Max(0, i - window);
            var count = i - start;
            if (count < 2)
            {
                continue;
            }

            double mean = 0;
            for (var j = start; j < i; j++) { mean += costs[j]; }
            mean /= count;

            double variance = 0;
            for (var j = start; j < i; j++) { var d = costs[j] - mean; variance += d * d; }
            variance /= count;

            var stdDev = Math.Sqrt(variance);
            if (stdDev > 0 && costs[i] > mean + (k * stdDev))
            {
                flags[i] = true;
            }
        }

        return flags;
    }
}
