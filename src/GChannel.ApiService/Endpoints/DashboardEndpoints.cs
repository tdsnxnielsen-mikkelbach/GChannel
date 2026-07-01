using System.Globalization;
using System.Text.Json;
using GChannel.ApiService.Configuration;
using GChannel.ApiService.Data;
using GChannel.ApiService.Services;
using GChannel.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace GChannel.ApiService.Endpoints;

/// <summary>
/// Maps the derived home-dashboard endpoint. There is no single Channel API reporting endpoint
/// (the legacy <c>accounts.reports.*</c> API is deprecated in v1), so the figures are aggregated
/// from the customer and entitlement read paths and cached in Redis like the other reads.
/// </summary>
public static class DashboardEndpoints
{
    /// <summary>Redis key the dashboard summary is cached under (shared with the background refresher).</summary>
    public const string CacheKey = "dashboard:summary";

    /// <summary>Redis key the cheap dashboard overview (count + onboarding) is cached under.</summary>
    public const string OverviewCacheKey = "dashboard:overview";

    /// <summary>Redis key the background refresher's run status (last run + in-progress flag) is stored under.</summary>
    public const string StatusKey = "dashboard:refresh:status";

    /// <summary>
    /// How long the "last known good" fallback copy is kept. Far longer than the live TTL so a
    /// sustained quota outage still has a result to serve.
    /// </summary>
    public static readonly TimeSpan StaleTtl = TimeSpan.FromHours(24);

    /// <summary>Derives the "last known good" key for a given live cache key.</summary>
    public static string StaleKey(string cacheKey) => cacheKey + ":last";

    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/dashboard").WithTags("Dashboard");

        group.MapGet("/summary", async (
                IGoogleChannelClient channel,
                IDistributedCache cache,
                IOptions<GoogleChannelOptions> options,
                GChannelDbContext db,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    // §10 read-model: when enabled, the dashboard is built entirely from the durably
                    // synced SQL tables (zero live Channel API fan-out), so the heavy entitlement work
                    // only happens once in the read-model sync rather than competing for quota here.
                    Func<Task<DashboardSummary>> factory = options.Value.UseReadModel
                        ? () => BuildReadModelSummaryAsync(db, cancellationToken)
                        : () => channel.GetDashboardSummaryAsync(cancellationToken);

                    return await CachedAsync(cache, CacheKey, options.Value.CacheSeconds,
                        factory, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Benign: the caller (Blazor circuit) went away mid-aggregation. Caught here in
                    // user code so the debugger doesn't flag it as user-unhandled; nothing to return.
                    return Results.StatusCode(499);
                }
            })
            .WithName("GetDashboardSummary")
            .WithSummary("Aggregated reseller figures derived from customers and entitlements.");

        group.MapGet("/overview", async (
                IGoogleChannelClient channel,
                IDistributedCache cache,
                IOptions<GoogleChannelOptions> options,
                GChannelDbContext db,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    Func<Task<DashboardOverview>> factory = options.Value.UseReadModel
                        ? () => BuildReadModelOverviewAsync(db, cancellationToken)
                        : () => channel.GetDashboardOverviewAsync(cancellationToken);

                    return await CachedAsync(cache, OverviewCacheKey, options.Value.CacheSeconds,
                        factory, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return Results.StatusCode(499);
                }
            })
            .WithName("GetDashboardOverview")
            .WithSummary("Cheap first-phase dashboard figures (customer count + onboarded-over-time).");

        group.MapGet("/status", async (
                IDistributedCache cache,
                IOptions<GoogleChannelOptions> options,
                CancellationToken cancellationToken) =>
            {
                var stored = await cache.GetStringAsync(StatusKey, cancellationToken);
                var status = stored is not null
                    ? JsonSerializer.Deserialize<DashboardRefreshStatus>(stored)
                    : null;

                // No status yet (refresher hasn't ticked, or it's disabled): report configuration only.
                return Results.Ok(status ?? new DashboardRefreshStatus
                {
                    Enabled = options.Value.BackgroundRefreshEnabled
                });
            })
            .WithName("GetDashboardStatus")
            .WithSummary("Freshness/health of the background dashboard refresher (last run + in-progress flag).");

        return app;
    }

    /// <summary>
    /// Returns a cached JSON payload when present, otherwise invokes <paramref name="factory"/> and
    /// caches it. A separate long-lived "last known good" copy is also kept: if a live recompute fails
    /// (e.g. the Channel API per-minute quota is exhausted), the most recent successful result is served
    /// instead of failing the whole dashboard.
    /// </summary>
    private static async Task<IResult> CachedAsync<T>(
        IDistributedCache cache,
        string cacheKey,
        int cacheSeconds,
        Func<Task<T>> factory,
        CancellationToken cancellationToken,
        Func<T, Task<T>>? overlay = null)
    {
        var staleKey = StaleKey(cacheKey);

        var cached = await cache.GetStringAsync(cacheKey, cancellationToken);
        if (cached is not null)
        {
            var hit = JsonSerializer.Deserialize<T>(cached);
            if (hit is not null && overlay is not null) { hit = await overlay(hit); }
            return Results.Ok(hit);
        }

        T result;
        try
        {
            result = await factory();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Live recompute failed (commonly a 429 quota error). Serve the last known good result if
            // we have one so a transient quota blip doesn't break the page; otherwise surface the error.
            var stale = await cache.GetStringAsync(staleKey, cancellationToken);
            if (stale is not null)
            {
                var staleHit = JsonSerializer.Deserialize<T>(stale);
                if (staleHit is not null && overlay is not null) { staleHit = await overlay(staleHit); }
                return Results.Ok(staleHit);
            }

            throw;
        }

        var json = JsonSerializer.Serialize(result);
        await cache.SetStringAsync(
            cacheKey,
            json,
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(cacheSeconds)
            },
            cancellationToken);

        // Refresh the long-lived fallback copy (survives well past the live TTL).
        await cache.SetStringAsync(
            staleKey,
            json,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = StaleTtl },
            cancellationToken);

        if (overlay is not null) { result = await overlay(result); }
        return Results.Ok(result);
    }

    /// <summary>
    /// §10 read path: builds the entire dashboard summary from the durably synced SQL read-model
    /// (direct customer/entitlement aggregates + product mix + onboarding), then overlays the indirect
    /// estate and §11 estate value. Used when <see cref="GoogleChannelOptions.UseReadModel"/> is on so
    /// the dashboard never fans out live Channel API entitlement/customer calls — the read-model sync is
    /// the single quota consumer. Also called by the background refresher to warm the cache.
    /// </summary>
    public static async Task<DashboardSummary> BuildReadModelSummaryAsync(
        GChannelDbContext db, CancellationToken cancellationToken)
    {
        // §11 Phase 9: the entitlement KPIs (active/trial/suspended/seats/product mix) span the WHOLE
        // estate — direct customers plus reseller-owned (indirect) ones — so they line up with the
        // estate-value panel, which is also whole-estate. Customer count and onboarding stay direct-only
        // (a distinct concept surfaced separately as IndirectCustomerCount).
        var entitlements = db.EntitlementRecords.Where(e => !e.IsDeleted);

        var customerCount = await db.CustomerRecords
            .CountAsync(c => !c.IsDeleted && c.OwningLinkId == null, cancellationToken);
        var active = await entitlements.CountAsync(e => e.State == "ACTIVE" && !e.IsTrial, cancellationToken);
        var trials = await entitlements.CountAsync(e => e.IsTrial, cancellationToken);
        var suspended = await entitlements.CountAsync(e => e.State == "SUSPENDED", cancellationToken);
        var seats = await entitlements.Where(e => e.State == "ACTIVE").SumAsync(e => e.Seats, cancellationToken);

        // Product mix: pull the active entitlements' product id/name/source into memory (a small set —
        // one row per active entitlement) so we can (a) split the mix into direct vs indirect and
        // (b) back-fill friendly names. A product's name resolves only while one of its offers is
        // still listed, so a churned-offer entitlement can carry a null name while a sibling on the
        // same product resolved one; reuse any resolved name across all entitlements of that product.
        var activeMix = await entitlements.Where(e => e.State == "ACTIVE")
            .Select(e => new { e.ProductId, e.ProductName, e.OwningLinkId })
            .ToListAsync(cancellationToken);

        var nameByProductId = activeMix
            .Where(e => e.ProductId != null && !string.IsNullOrWhiteSpace(e.ProductName))
            .GroupBy(e => e.ProductId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().ProductName!, StringComparer.OrdinalIgnoreCase);

        string Label(string? productId, string? productName) =>
            !string.IsNullOrWhiteSpace(productName) ? productName!
            : productId != null && nameByProductId.TryGetValue(productId, out var resolved) ? resolved
            : productId ?? "Other";

        static List<DashboardProductSlice> BuildMix(IEnumerable<(string Label, int _)> labelled) =>
            labelled
                .GroupBy(x => x.Label)
                .Select(g => new DashboardProductSlice { Product = g.Key, Count = g.Count() })
                .OrderByDescending(s => s.Count)
                .Take(8)
                .ToList();

        var labelledAll = activeMix
            .Select(e => (Label: Label(e.ProductId, e.ProductName), IsDirect: e.OwningLinkId == null))
            .ToList();
        var mix = BuildMix(labelledAll.Select(x => (x.Label, 0)));
        var directMix = BuildMix(labelledAll.Where(x => x.IsDirect).Select(x => (x.Label, 0)));
        var indirectMix = BuildMix(labelledAll.Where(x => !x.IsDirect).Select(x => (x.Label, 0)));

        var onboardDates = await db.CustomerRecords
            .Where(c => !c.IsDeleted && c.OwningLinkId == null)
            .Select(c => c.CreateTime)
            .ToListAsync(cancellationToken);

        var summary = new DashboardSummary
        {
            CustomerCount = customerCount,
            ActiveEntitlementCount = active,
            TrialEntitlementCount = trials,
            SuspendedEntitlementCount = suspended,
            ActiveSeats = seats,
            SkippedCustomerCount = 0,
            IncompleteReason = null,
            CustomersOnboarded = BuildMonthlyOnboardedFromDates(onboardDates),
            ProductMix = mix,
            DirectProductMix = directMix,
            IndirectProductMix = indirectMix,
            // Set here so a direct-only estate (no indirect rows yet) still shows the value panel; the
            // overlay recomputes the identical figure when indirect rows exist.
            EstateValue = await ComputeEstateValueAsync(db, cancellationToken)
        };

        return await OverlayReadModelAsync(summary, db, cancellationToken);
    }

    /// <summary>
    /// §10 read path: builds the cheap dashboard overview (direct customer count, channel-link states
    /// and onboarding) from the SQL read-model instead of a live customers.list + links.list.
    /// </summary>
    public static async Task<DashboardOverview> BuildReadModelOverviewAsync(
        GChannelDbContext db, CancellationToken cancellationToken)
    {
        var customerCount = await db.CustomerRecords
            .CountAsync(c => !c.IsDeleted && c.OwningLinkId == null, cancellationToken);

        var linkStates = await db.ResellerLinks
            .GroupBy(l => l.LinkState)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var onboardDates = await db.CustomerRecords
            .Where(c => !c.IsDeleted && c.OwningLinkId == null)
            .Select(c => c.CreateTime)
            .ToListAsync(cancellationToken);

        return new DashboardOverview
        {
            CustomerCount = customerCount,
            ChannelLinkCount = linkStates.Sum(s => s.Count),
            ChannelLinkStates = linkStates
                .OrderByDescending(s => s.Count)
                .Select(s => new DashboardChannelLinkState { State = s.State, Count = s.Count })
                .ToList(),
            CustomersOnboarded = BuildMonthlyOnboardedFromDates(onboardDates)
        };
    }

    // Buckets customer create-times into the trailing 6 months (oldest first), matching the live path's
    // BuildMonthlyOnboarded so the chart looks identical regardless of which path produced it.
    private static List<DashboardMonthlyPoint> BuildMonthlyOnboardedFromDates(IReadOnlyList<DateTimeOffset?> createTimes)
    {
        var now = DateTimeOffset.UtcNow;
        var firstOfThisMonth = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

        var points = new List<DashboardMonthlyPoint>(6);
        for (var i = 5; i >= 0; i--)
        {
            var monthStart = firstOfThisMonth.AddMonths(-i);
            var monthEnd = monthStart.AddMonths(1);
            var count = createTimes.Count(t => t is { } v && v >= monthStart && v < monthEnd);

            points.Add(new DashboardMonthlyPoint
            {
                Month = monthStart.ToString("MMM", CultureInfo.InvariantCulture),
                Customers = count
            });
        }

        return points;
    }

    /// <summary>
    /// §10 read path: replaces the indirect estate (count + top resellers ranked by active seats) on a
    /// summary with values aggregated from the SQL read-model, so the dashboard reflects the durably
    /// synced estate instead of waiting for the live per-reseller fan-out. No-op if no rows synced yet.
    /// </summary>
    private static async Task<DashboardSummary> OverlayReadModelAsync(
        DashboardSummary summary, GChannelDbContext db, CancellationToken cancellationToken)
    {
        var indirectCount = await db.CustomerRecords
            .CountAsync(c => !c.IsDeleted && c.OwningLinkId != null, cancellationToken);

        // Direct-estate backfill: when the live aggregation skipped customers (typically 429s), serve the
        // last-synced direct figures from EntitlementRecords so the headline KPIs fill in from the DB
        // instead of showing partial numbers. Only kicks in when something was skipped and the read-model
        // has direct entitlement rows synced.
        if (summary.SkippedCustomerCount > 0)
        {
            summary = await BackfillDirectEstateAsync(summary, db, cancellationToken);
        }

        if (indirectCount == 0)
        {
            return summary; // Nothing synced yet — keep the live values.
        }

        // §11 per-reseller cost/margin: aggregate active, priced entitlements by owning link.
        var resellerPricing = await db.EntitlementRecords
            .Where(e => !e.IsDeleted && e.State == "ACTIVE" && e.OwningLinkId != null && e.UnitPrice > 0)
            .GroupBy(e => e.OwningLinkId!)
            .Select(g => new
            {
                LinkId = g.Key,
                Wholesale = g.Sum(e => e.UnitPrice * e.Seats),
                Revenue = g.Sum(e => e.UnitPrice * e.Seats * (1 + (e.RepricingPercent / 100m)))
            })
            .ToListAsync(cancellationToken);
        var pricingByLink = resellerPricing.ToDictionary(x => x.LinkId, StringComparer.OrdinalIgnoreCase);

        var resellers = await db.CustomerRecords
            .Where(c => !c.IsDeleted && c.OwningLinkId != null)
            .GroupBy(c => c.OwningLinkId!)
            .Select(g => new { LinkId = g.Key, Customers = g.Count(), Seats = g.Sum(c => c.SeatCount) })
            .Join(db.ResellerLinks, g => g.LinkId, l => l.LinkId,
                (g, l) => new { l.PrimaryDomain, l.ResellerCloudId, g.LinkId, g.Customers, g.Seats })
            .OrderByDescending(x => x.Seats)
            .ThenByDescending(x => x.Customers)
            .Take(15)
            .ToListAsync(cancellationToken);

        return summary with
        {
            IndirectCustomerCount = indirectCount,
            EstateValue = await ComputeEstateValueAsync(db, cancellationToken),
            TopIndirectResellers = resellers
                .Select(r =>
                {
                    pricingByLink.TryGetValue(r.LinkId, out var p);
                    return new DashboardResellerCustomers
                    {
                        Reseller = r.PrimaryDomain ?? r.ResellerCloudId ?? r.LinkId,
                        CustomerCount = r.Customers,
                        SeatCount = r.Seats,
                        WholesaleMonthly = p is null ? 0m : decimal.Round(p.Wholesale, 2),
                        MarginMonthly = p is null ? 0m : decimal.Round(p.Revenue - p.Wholesale, 2)
                    };
                })
                .ToList()
        };
    }

    // §11: estimated monthly estate value across all active, priced entitlements (direct + indirect),
    // reported in the estate's dominant currency. Returns null when nothing has a resolved price yet.
    private static async Task<DashboardEstateValue?> ComputeEstateValueAsync(
        GChannelDbContext db, CancellationToken cancellationToken)
    {
        var active = db.EntitlementRecords.Where(e => !e.IsDeleted && e.State == "ACTIVE");

        // Group by currency AND source (direct = no owning channel link, indirect = reseller-owned) so
        // the estate value can be split into what comes from your own customers vs downstream resellers.
        var byCurrencyScope = await active
            .Where(e => e.UnitPrice > 0 && e.Currency != null)
            .GroupBy(e => new { Currency = e.Currency!, IsDirect = e.OwningLinkId == null })
            .Select(g => new
            {
                g.Key.Currency,
                g.Key.IsDirect,
                Wholesale = g.Sum(e => e.UnitPrice * e.Seats),
                Revenue = g.Sum(e => e.UnitPrice * e.Seats * (1 + (e.RepricingPercent / 100m))),
                Count = g.Count()
            })
            .ToListAsync(cancellationToken);

        if (byCurrencyScope.Count == 0)
        {
            return null; // No entitlement has a resolved offer price yet.
        }

        static DashboardEstateValueScope Scope(IEnumerable<(decimal Wholesale, decimal Revenue, int Count)> rows)
        {
            var wholesale = rows.Sum(r => r.Wholesale);
            var revenue = rows.Sum(r => r.Revenue);
            return new DashboardEstateValueScope
            {
                WholesaleMonthly = decimal.Round(wholesale, 2),
                RevenueMonthly = decimal.Round(revenue, 2),
                MarginMonthly = decimal.Round(revenue - wholesale, 2),
                PricedEntitlementCount = rows.Sum(r => r.Count)
            };
        }

        var currencies = byCurrencyScope
            .GroupBy(x => x.Currency)
            .Select(g =>
            {
                var direct = g.Where(x => x.IsDirect).Select(x => (x.Wholesale, x.Revenue, x.Count));
                var indirect = g.Where(x => !x.IsDirect).Select(x => (x.Wholesale, x.Revenue, x.Count));
                var wholesale = g.Sum(x => x.Wholesale);
                var revenue = g.Sum(x => x.Revenue);
                return new DashboardEstateValueCurrency
                {
                    Currency = g.Key,
                    WholesaleMonthly = decimal.Round(wholesale, 2),
                    RevenueMonthly = decimal.Round(revenue, 2),
                    MarginMonthly = decimal.Round(revenue - wholesale, 2),
                    PricedEntitlementCount = g.Sum(x => x.Count),
                    Direct = Scope(direct),
                    Indirect = Scope(indirect)
                };
            })
            .OrderByDescending(x => x.WholesaleMonthly)
            .ToList();

        var dominant = currencies[0];
        var unpriced = await active.CountAsync(e => e.UnitPrice <= 0, cancellationToken);

        return new DashboardEstateValue
        {
            Currency = dominant.Currency,
            WholesaleMonthly = dominant.WholesaleMonthly,
            RevenueMonthly = dominant.RevenueMonthly,
            MarginMonthly = dominant.MarginMonthly,
            MixedCurrencies = currencies.Count > 1,
            PricedEntitlementCount = currencies.Sum(c => c.PricedEntitlementCount),
            UnpricedEntitlementCount = unpriced,
            Direct = dominant.Direct,
            Indirect = dominant.Indirect,
            Currencies = currencies
        };
    }

    // Replaces the partial live direct figures with read-model totals for direct customers (OwningLinkId
    // null) when the live run skipped some, so the dashboard fills from SQL rather than under-reporting.
    private static async Task<DashboardSummary> BackfillDirectEstateAsync(
        DashboardSummary summary, GChannelDbContext db, CancellationToken cancellationToken)
    {
        var rows = db.EntitlementRecords.Where(e => !e.IsDeleted && e.OwningLinkId == null);
        var hasData = await rows.AnyAsync(cancellationToken);
        if (!hasData)
        {
            return summary; // Read-model hasn't synced direct entitlements yet — keep live partials.
        }

        var active = await rows.CountAsync(e => e.State == "ACTIVE" && !e.IsTrial, cancellationToken);
        var trials = await rows.CountAsync(e => e.IsTrial, cancellationToken);
        var suspended = await rows.CountAsync(e => e.State == "SUSPENDED", cancellationToken);
        var seats = await rows.Where(e => e.State == "ACTIVE").SumAsync(e => e.Seats, cancellationToken);

        var mix = await rows.Where(e => e.State == "ACTIVE")
            .GroupBy(e => e.ProductName ?? e.ProductId ?? "Other")
            .Select(g => new { Product = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .Take(8)
            .ToListAsync(cancellationToken);

        return summary with
        {
            ActiveEntitlementCount = Math.Max(summary.ActiveEntitlementCount, active),
            TrialEntitlementCount = Math.Max(summary.TrialEntitlementCount, trials),
            SuspendedEntitlementCount = Math.Max(summary.SuspendedEntitlementCount, suspended),
            ActiveSeats = Math.Max(summary.ActiveSeats, seats),
            SkippedCustomerCount = 0,
            IncompleteReason = null,
            ProductMix = summary.ProductMix is { Count: > 0 }
                ? summary.ProductMix
                : mix.Select(m => new DashboardProductSlice { Product = m.Product, Count = m.Count }).ToList()
        };
    }
}

