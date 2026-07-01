using GChannel.ApiService.Data;
using GChannel.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace GChannel.ApiService.Endpoints;

/// <summary>
/// §10 read-model estate views. Customers and channel-partner-links page/sort/filter server-side
/// against SQL (so the list pages stay fast at distributor scale), expose an "as-of" freshness
/// timestamp, and offer a "refresh now" action that prioritises a link/customer to the front of the
/// sync queue (the background <c>ReadModelSyncService</c> picks the stalest rows first, so zeroing a
/// row's <c>LastSyncedUtc</c> moves it to the head of the next cycle).
/// </summary>
public static class EstateEndpoints
{
    private const int MaxPageSize = 200;

    public static IEndpointRouteBuilder MapEstateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/estate").WithTags("Estate");

        group.MapGet("/customers", async (
                GChannelDbContext db,
                CancellationToken ct,
                int page = 0,
                int pageSize = 25,
                string? sort = null,
                bool desc = false,
                string? search = null,
                string? linkId = null) =>
            {
                pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
                page = Math.Max(0, page);

                var q = db.CustomerRecords.AsNoTracking().Where(c => !c.IsDeleted);

                if (!string.IsNullOrWhiteSpace(linkId))
                {
                    q = linkId switch
                    {
                        "direct" => q.Where(c => c.OwningLinkId == null),
                        "indirect" => q.Where(c => c.OwningLinkId != null),
                        _ => q.Where(c => c.OwningLinkId == linkId),
                    };
                }

                if (!string.IsNullOrWhiteSpace(search))
                {
                    var s = search.Trim();
                    q = q.Where(c => (c.OrgName != null && c.OrgName.Contains(s)) ||
                                     (c.Domain != null && c.Domain.Contains(s)) ||
                                     c.CustomerId.Contains(s));
                }

                q = (sort, desc) switch
                {
                    ("org", false) => q.OrderBy(c => c.OrgName),
                    ("org", true) => q.OrderByDescending(c => c.OrgName),
                    ("domain", false) => q.OrderBy(c => c.Domain),
                    ("domain", true) => q.OrderByDescending(c => c.Domain),
                    ("seats", false) => q.OrderBy(c => c.SeatCount),
                    ("seats", true) => q.OrderByDescending(c => c.SeatCount),
                    ("created", false) => q.OrderBy(c => c.CreateTime),
                    ("created", true) => q.OrderByDescending(c => c.CreateTime),
                    (_, true) => q.OrderByDescending(c => c.OrgName),
                    _ => q.OrderBy(c => c.OrgName),
                };

                var total = await q.CountAsync(ct);
                var items = await q.Skip(page * pageSize).Take(pageSize)
                    .Select(c => new EstateCustomer
                    {
                        CustomerId = c.CustomerId,
                        OrgName = c.OrgName,
                        Domain = c.Domain,
                        CloudIdentityId = c.CloudIdentityId,
                        OwningLinkId = c.OwningLinkId,
                        SeatCount = c.SeatCount,
                        CreateTime = c.CreateTime,
                        LastSyncedUtc = c.LastSyncedUtc,
                    })
                    .ToListAsync(ct);

                // Per-customer rollups for the page's customers (kept to the page so the list stays fast
                // at distributor scale): estimated monthly value, entitlement state counts and the next
                // renewal date — all from the synced read-model, no live Channel API calls.
                if (items.Count > 0)
                {
                    var ids = items.Select(i => i.CustomerId).ToList();
                    var now = DateTimeOffset.UtcNow;
                    var rows = await db.EntitlementRecords.AsNoTracking()
                        .Where(e => ids.Contains(e.CustomerId) && !e.IsDeleted)
                        .Select(e => new
                        {
                            e.CustomerId, e.State, e.Currency, e.UnitPrice, e.Seats,
                            e.RepricingPercent, e.CommitmentEndTime, e.OfferName, e.RenewalEnabled,
                        })
                        .ToListAsync(ct);

                    var byCustomer = rows
                        .GroupBy(e => e.CustomerId)
                        .ToDictionary(g => g.Key, g =>
                        {
                            var active = g.Count(e => e.State == "ACTIVE");
                            var suspended = g.Count(e => e.State == "SUSPENDED");

                            // Estimated monthly value: Σ over active priced entitlements of
                            // unit price × seats × (1 + markup%), in the customer's dominant currency.
                            var priced = g.Where(e => e.State == "ACTIVE" && e.UnitPrice > 0 && e.Seats > 0).ToList();
                            decimal? monthly = null;
                            string? currency = null;
                            if (priced.Count > 0)
                            {
                                var dominant = priced.GroupBy(e => e.Currency ?? "")
                                    .OrderByDescending(cg => cg.Sum(e => e.UnitPrice * e.Seats))
                                    .First();
                                monthly = dominant.Sum(e => e.UnitPrice * e.Seats * (1 + (e.RepricingPercent / 100m)));
                                currency = string.IsNullOrEmpty(dominant.Key) ? null : dominant.Key;
                            }

                            // Next renewal: earliest upcoming commitment end across active entitlements.
                            var nextRenewal = g
                                .Where(e => e.State == "ACTIVE" && e.CommitmentEndTime != null && e.CommitmentEndTime > now)
                                .OrderBy(e => e.CommitmentEndTime)
                                .FirstOrDefault();

                            return (active, suspended, Total: monthly, Currency: currency,
                                    Renewal: nextRenewal?.CommitmentEndTime, RenewalOffer: nextRenewal?.OfferName,
                                    RenewalAutoRenew: nextRenewal?.RenewalEnabled);
                        });

                    // Resolve friendly reseller names for the page's indirect customers (primary domain,
                    // else reseller cloud id, else the raw link id) from the read-model links table.
                    var linkIds = items.Where(i => i.OwningLinkId != null)
                        .Select(i => i.OwningLinkId!).Distinct().ToList();
                    var resellerNames = linkIds.Count == 0
                        ? new Dictionary<string, string>()
                        : await db.ResellerLinks.AsNoTracking()
                            .Where(l => linkIds.Contains(l.LinkId))
                            .ToDictionaryAsync(
                                l => l.LinkId,
                                l => l.PrimaryDomain ?? l.ResellerCloudId ?? l.LinkId,
                                ct);

                    items = items.Select(i =>
                        byCustomer.TryGetValue(i.CustomerId, out var v)
                            ? i with
                            {
                                EstimatedMonthlyTotal = v.Total,
                                Currency = v.Currency,
                                ActiveSubscriptions = v.active,
                                SuspendedSubscriptions = v.suspended,
                                NextRenewalUtc = v.Renewal,
                                NextRenewalOfferName = v.RenewalOffer,
                                NextRenewalAutoRenew = v.RenewalAutoRenew,
                                ResellerName = i.OwningLinkId != null && resellerNames.TryGetValue(i.OwningLinkId, out var rn) ? rn : null,
                            }
                            : i.OwningLinkId != null && resellerNames.TryGetValue(i.OwningLinkId, out var rn2)
                                ? i with { ResellerName = rn2 }
                                : i).ToList();
                }

                return Results.Ok(new PagedEstateResult<EstateCustomer>
                {
                    Items = items,
                    Total = total,
                    // Estate-wide freshness (the most recent customer sync), not just this page's rows,
                    // so the badge doesn't read "—" when the current page happens to hold not-yet-synced
                    // customers. Never-synced rows (LastSyncedUtc == MinValue) are excluded.
                    AsOf = await db.CustomerRecords.AsNoTracking()
                        .Where(c => !c.IsDeleted && c.LastSyncedUtc > DateTimeOffset.MinValue)
                        .Select(c => (DateTimeOffset?)c.LastSyncedUtc)
                        .MaxAsync(ct),
                });
            });

        group.MapGet("/resellers", async (
                GChannelDbContext db,
                CancellationToken ct,
                int page = 0,
                int pageSize = 25,
                string? sort = null,
                bool desc = false,
                string? search = null,
                string? state = null) =>
            {
                pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
                page = Math.Max(0, page);

                var q = db.ResellerLinks.AsNoTracking().AsQueryable();

                if (!string.IsNullOrWhiteSpace(state))
                {
                    q = q.Where(r => r.LinkState == state);
                }

                if (!string.IsNullOrWhiteSpace(search))
                {
                    var s = search.Trim();
                    q = q.Where(r => (r.PrimaryDomain != null && r.PrimaryDomain.Contains(s)) ||
                                     (r.ResellerCloudId != null && r.ResellerCloudId.Contains(s)) ||
                                     r.LinkId.Contains(s));
                }

                q = (sort, desc) switch
                {
                    ("domain", false) => q.OrderBy(r => r.PrimaryDomain),
                    ("domain", true) => q.OrderByDescending(r => r.PrimaryDomain),
                    ("state", false) => q.OrderBy(r => r.LinkState),
                    ("state", true) => q.OrderByDescending(r => r.LinkState),
                    ("customers", false) => q.OrderBy(r => r.CustomerCount),
                    ("customers", true) => q.OrderByDescending(r => r.CustomerCount),
                    ("created", false) => q.OrderBy(r => r.CreateTime),
                    ("created", true) => q.OrderByDescending(r => r.CreateTime),
                    (_, true) => q.OrderByDescending(r => r.PrimaryDomain),
                    _ => q.OrderBy(r => r.PrimaryDomain),
                };

                var total = await q.CountAsync(ct);
                var items = await q.Skip(page * pageSize).Take(pageSize)
                    .Select(r => new EstateReseller
                    {
                        LinkId = r.LinkId,
                        PrimaryDomain = r.PrimaryDomain,
                        ResellerCloudId = r.ResellerCloudId,
                        LinkState = r.LinkState,
                        CustomerCount = r.CustomerCount,
                        CreateTime = r.CreateTime,
                        LastSyncedUtc = r.LastSyncedUtc,
                        SyncError = r.SyncError,
                    })
                    .ToListAsync(ct);

                return Results.Ok(new PagedEstateResult<EstateReseller>
                {
                    Items = items,
                    Total = total,
                    AsOf = items.Count == 0 ? null : items.Min(i => i.LastSyncedUtc),
                });
            });

        // Estimated estate value for a single reseller (channel partner link): wholesale cost, repriced
        // revenue and margin across all of that reseller's customers' active priced entitlements — the
        // read-model view of "what this reseller is doing". Per-currency; headline is the dominant one.
        group.MapGet("/resellers/{linkId}/value", async (string linkId, GChannelDbContext db, CancellationToken ct) =>
            {
                var active = db.EntitlementRecords.AsNoTracking()
                    .Where(e => !e.IsDeleted && e.State == "ACTIVE" && e.OwningLinkId == linkId);

                var byCurrency = await active
                    .Where(e => e.UnitPrice > 0 && e.Currency != null)
                    .GroupBy(e => e.Currency!)
                    .Select(g => new
                    {
                        Currency = g.Key,
                        Wholesale = g.Sum(e => e.UnitPrice * e.Seats),
                        Revenue = g.Sum(e => e.UnitPrice * e.Seats * (1 + (e.RepricingPercent / 100m))),
                        Seats = g.Sum(e => e.Seats),
                        Count = g.Count(),
                    })
                    .ToListAsync(ct);

                var customerCount = await active.Select(e => e.CustomerId).Distinct().CountAsync(ct);
                var unpriced = await active.CountAsync(e => e.UnitPrice <= 0, ct);

                var currencies = byCurrency
                    .Select(x => new ResellerEstateValueCurrency
                    {
                        Currency = x.Currency,
                        WholesaleMonthly = decimal.Round(x.Wholesale, 2),
                        RevenueMonthly = decimal.Round(x.Revenue, 2),
                        MarginMonthly = decimal.Round(x.Revenue - x.Wholesale, 2),
                        PricedEntitlementCount = x.Count,
                        ActiveSeats = x.Seats,
                    })
                    .OrderByDescending(x => x.WholesaleMonthly)
                    .ToList();

                var dominant = currencies.Count > 0 ? currencies[0] : null;
                return Results.Ok(new ResellerEstateValue
                {
                    Currency = dominant?.Currency,
                    WholesaleMonthly = dominant?.WholesaleMonthly ?? 0m,
                    RevenueMonthly = dominant?.RevenueMonthly ?? 0m,
                    MarginMonthly = dominant?.MarginMonthly ?? 0m,
                    MixedCurrencies = currencies.Count > 1,
                    PricedEntitlementCount = currencies.Sum(c => c.PricedEntitlementCount),
                    UnpricedEntitlementCount = unpriced,
                    ActiveSeats = currencies.Sum(c => c.ActiveSeats),
                    CustomerCount = customerCount,
                    Currencies = currencies,
                });
            });

        // Estimated monthly value for a single customer: wholesale cost, repriced revenue and margin
        // across that customer's active priced entitlements — the read-model equivalent of the reseller
        // value above, so the customer detail page shows the same figure whether or not the live pricing
        // path has run. Per-currency; headline is the dominant one. Covers direct and indirect customers.
        group.MapGet("/customers/{customerId}/value", async (string customerId, GChannelDbContext db, CancellationToken ct) =>
            {
                var active = db.EntitlementRecords.AsNoTracking()
                    .Where(e => !e.IsDeleted && e.State == "ACTIVE" && e.CustomerId == customerId);

                var byCurrency = await active
                    .Where(e => e.UnitPrice > 0 && e.Currency != null)
                    .GroupBy(e => e.Currency!)
                    .Select(g => new
                    {
                        Currency = g.Key,
                        Wholesale = g.Sum(e => e.UnitPrice * e.Seats),
                        Revenue = g.Sum(e => e.UnitPrice * e.Seats * (1 + (e.RepricingPercent / 100m))),
                        Seats = g.Sum(e => e.Seats),
                        Count = g.Count(),
                    })
                    .ToListAsync(ct);

                var unpriced = await active.CountAsync(e => e.UnitPrice <= 0, ct);

                var currencies = byCurrency
                    .Select(x => new ResellerEstateValueCurrency
                    {
                        Currency = x.Currency,
                        WholesaleMonthly = decimal.Round(x.Wholesale, 2),
                        RevenueMonthly = decimal.Round(x.Revenue, 2),
                        MarginMonthly = decimal.Round(x.Revenue - x.Wholesale, 2),
                        PricedEntitlementCount = x.Count,
                        ActiveSeats = x.Seats,
                    })
                    .OrderByDescending(x => x.WholesaleMonthly)
                    .ToList();

                var dominant = currencies.Count > 0 ? currencies[0] : null;
                return Results.Ok(new CustomerEstateValue
                {
                    Currency = dominant?.Currency,
                    WholesaleMonthly = dominant?.WholesaleMonthly ?? 0m,
                    RevenueMonthly = dominant?.RevenueMonthly ?? 0m,
                    MarginMonthly = dominant?.MarginMonthly ?? 0m,
                    MixedCurrencies = currencies.Count > 1,
                    PricedEntitlementCount = currencies.Sum(c => c.PricedEntitlementCount),
                    UnpricedEntitlementCount = unpriced,
                    ActiveSeats = currencies.Sum(c => c.ActiveSeats),
                    Currencies = currencies,
                });
            });

        // Estate-wide entitlements list the dashboard lifecycle KPIs (Active/Trial/Suspended) link into.
        // Paged/sorted server-side against SQL and joined to the customer read-model for the org name.
        // The scope defaults to "direct" so the counts match the dashboard KPIs (which are direct-only).
        group.MapGet("/entitlements", async (
                GChannelDbContext db,
                CancellationToken ct,
                int page = 0,
                int pageSize = 25,
                string? sort = null,
                bool desc = false,
                string? search = null,
                string? state = null,
                string? scope = null) =>
            {
                pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
                page = Math.Max(0, page);

                var q = db.EntitlementRecords.AsNoTracking().Where(e => !e.IsDeleted);

                // Scope mirrors the dashboard KPIs (direct-only) by default; allow indirect/all too.
                q = (scope ?? "").ToLowerInvariant() switch
                {
                    "indirect" => q.Where(e => e.OwningLinkId != null),
                    "all" => q,
                    _ => q.Where(e => e.OwningLinkId == null),
                };

                // State filter mirrors the dashboard lifecycle buckets exactly.
                q = (state ?? "").ToLowerInvariant() switch
                {
                    "active" => q.Where(e => e.State == "ACTIVE" && !e.IsTrial),
                    "trial" => q.Where(e => e.IsTrial),
                    "suspended" => q.Where(e => e.State == "SUSPENDED"),
                    _ => q,
                };

                var joined = q.Join(
                    db.CustomerRecords.AsNoTracking(),
                    e => e.CustomerId,
                    c => c.CustomerId,
                    (e, c) => new { E = e, c.OrgName });

                if (!string.IsNullOrWhiteSpace(search))
                {
                    var s = search.Trim();
                    joined = joined.Where(x =>
                        (x.OrgName != null && x.OrgName.Contains(s)) ||
                        x.E.CustomerId.Contains(s) ||
                        (x.E.OfferName != null && x.E.OfferName.Contains(s)) ||
                        (x.E.SkuName != null && x.E.SkuName.Contains(s)) ||
                        (x.E.ProductName != null && x.E.ProductName.Contains(s)));
                }

                joined = (sort, desc) switch
                {
                    ("customer", false) => joined.OrderBy(x => x.OrgName),
                    ("customer", true) => joined.OrderByDescending(x => x.OrgName),
                    ("product", false) => joined.OrderBy(x => x.E.ProductName),
                    ("product", true) => joined.OrderByDescending(x => x.E.ProductName),
                    ("seats", false) => joined.OrderBy(x => x.E.Seats),
                    ("seats", true) => joined.OrderByDescending(x => x.E.Seats),
                    ("state", false) => joined.OrderBy(x => x.E.State),
                    ("state", true) => joined.OrderByDescending(x => x.E.State),
                    ("renewal", false) => joined.OrderBy(x => x.E.CommitmentEndTime),
                    ("renewal", true) => joined.OrderByDescending(x => x.E.CommitmentEndTime),
                    ("created", false) => joined.OrderBy(x => x.E.CreateTime),
                    ("created", true) => joined.OrderByDescending(x => x.E.CreateTime),
                    (_, true) => joined.OrderByDescending(x => x.OrgName),
                    _ => joined.OrderBy(x => x.OrgName),
                };

                var total = await joined.CountAsync(ct);
                var items = await joined.Skip(page * pageSize).Take(pageSize)
                    .Select(x => new EstateEntitlement
                    {
                        EntitlementId = x.E.EntitlementId,
                        CustomerId = x.E.CustomerId,
                        CustomerName = x.OrgName,
                        OwningLinkId = x.E.OwningLinkId,
                        ProductName = x.E.ProductName,
                        SkuName = x.E.SkuName,
                        OfferName = x.E.OfferName,
                        State = x.E.State,
                        IsTrial = x.E.IsTrial,
                        Seats = x.E.Seats,
                        UnitPrice = x.E.UnitPrice,
                        Currency = x.E.Currency,
                        RepricingPercent = x.E.RepricingPercent,
                        CommitmentEndTime = x.E.CommitmentEndTime,
                        CreateTime = x.E.CreateTime,
                        LastSyncedUtc = x.E.LastSyncedUtc,
                    })
                    .ToListAsync(ct);

                return Results.Ok(new PagedEstateResult<EstateEntitlement>
                {
                    Items = items,
                    Total = total,
                    AsOf = items.Count == 0 ? null : items.Min(i => i.LastSyncedUtc),
                });
            });

        // Refresh now: zero the row's LastSyncedUtc so the background sync (which orders by oldest
        // first) picks it at the head of the next cycle.
        group.MapPost("/resellers/{linkId}/resync", async (string linkId, GChannelDbContext db, CancellationToken ct) =>
            {
                var row = await db.ResellerLinks.FirstOrDefaultAsync(r => r.LinkId == linkId, ct);
                if (row is null) return Results.NotFound();
                row.LastSyncedUtc = DateTimeOffset.MinValue;
                await db.SaveChangesAsync(ct);
                return Results.Accepted();
            });

        group.MapPost("/customers/{customerId}/resync", async (string customerId, GChannelDbContext db, CancellationToken ct) =>
            {
                var row = await db.CustomerRecords.FirstOrDefaultAsync(c => c.CustomerId == customerId, ct);
                if (row is null) return Results.NotFound();
                row.LastSyncedUtc = DateTimeOffset.MinValue;
                // Owning link is the unit of sync; prioritise it too so the fan-out re-reads this customer next cycle.
                if (!string.IsNullOrEmpty(row.OwningLinkId))
                {
                    var link = await db.ResellerLinks.FirstOrDefaultAsync(r => r.LinkId == row.OwningLinkId, ct);
                    if (link is not null) link.LastSyncedUtc = DateTimeOffset.MinValue;
                }
                await db.SaveChangesAsync(ct);
                return Results.Accepted();
            });

        return app;
    }
}
