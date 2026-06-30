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
                    q = linkId == "direct" ? q.Where(c => c.OwningLinkId == null) : q.Where(c => c.OwningLinkId == linkId);
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
                            e.RepricingPercent, e.CommitmentEndTime, e.OfferName,
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
                                    Renewal: nextRenewal?.CommitmentEndTime, RenewalOffer: nextRenewal?.OfferName);
                        });

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
                            }
                            : i).ToList();
                }

                return Results.Ok(new PagedEstateResult<EstateCustomer>
                {
                    Items = items,
                    Total = total,
                    AsOf = items.Where(i => i.LastSyncedUtc > DateTimeOffset.MinValue)
                                .Select(i => (DateTimeOffset?)i.LastSyncedUtc)
                                .DefaultIfEmpty(null)
                                .Min(),
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
