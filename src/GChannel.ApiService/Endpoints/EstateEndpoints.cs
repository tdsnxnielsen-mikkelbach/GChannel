using GChannel.Core.Estate;
using GChannel.Core.Estate.ReadModels;
using GChannel.Core.Messaging;
using GChannel.Core.Persistence.Documents;
using GChannel.Shared.Contracts;
using Marten;
using Wolverine;
using Wolverine.Http;

namespace GChannel.ApiService.Endpoints;

/// <summary>
/// Read-model estate views: customers, resellers and entitlements paged / sorted / filtered server-side
/// from the projected Marten documents (no Channel API calls), with an "as-of" freshness timestamp and a
/// "refresh now" action that moves a link/customer to the head of the sync rotation (and, for customers,
/// triggers an immediate targeted re-projection through the worker).
/// </summary>
public static class EstateEndpoints
{
    private const int MaxPageSize = 200;

    [WolverineGet("/api/estate/customers")]
    public static async Task<PagedEstateResult<EstateCustomer>> ListCustomers(
        IQuerySession session, int page, int pageSize, string? sort, bool desc, string? search, string? linkId, CancellationToken ct)
    {
        pageSize = pageSize <= 0 ? 25 : Math.Clamp(pageSize, 1, MaxPageSize);
        page = Math.Max(0, page);
        var now = DateTimeOffset.UtcNow;

        var customers = await session.Query<CustomerRecord>().Where(c => !c.IsDeleted).ToListAsync(ct);
        IEnumerable<CustomerRecord> q = customers;

        if (!string.IsNullOrWhiteSpace(linkId))
        {
            q = linkId switch
            {
                "direct" => q.Where(c => c.IsResellerSelf),
                "indirect" => q.Where(c => !c.IsResellerSelf),
                _ => q.Where(c => c.OwningLinkId == linkId),
            };
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(c => (c.OrgName != null && c.OrgName.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                             (c.Domain != null && c.Domain.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                             c.CustomerId.Contains(s, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = q.ToList();
        var ids = filtered.Select(c => c.CustomerId).ToList();

        // Per-customer rollups from the projected entitlements (seats, value, state counts, next renewal).
        var entitlements = ids.Count == 0
            ? []
            : await session.Query<EntitlementRecord>().Where(e => !e.IsDeleted && e.CustomerId.IsOneOf(ids)).ToListAsync(ct);
        var rollups = EstateRollups.ByCustomer(entitlements, now);

        var syncStates = (await session.Query<CustomerSyncState>().ToListAsync(ct))
            .ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

        var links = (await session.Query<ResellerLinkRecord>().ToListAsync(ct))
            .ToDictionary(l => l.LinkId, EstateRollups.LinkLabel, StringComparer.OrdinalIgnoreCase);

        long SeatsOf(CustomerRecord c) => rollups.TryGetValue(c.CustomerId, out var r) ? r.SeatCount : 0;

        var ordered = (sort, desc) switch
        {
            ("org", false) => filtered.OrderBy(c => c.OrgName, StringComparer.OrdinalIgnoreCase),
            ("org", true) => filtered.OrderByDescending(c => c.OrgName, StringComparer.OrdinalIgnoreCase),
            ("domain", false) => filtered.OrderBy(c => c.Domain, StringComparer.OrdinalIgnoreCase),
            ("domain", true) => filtered.OrderByDescending(c => c.Domain, StringComparer.OrdinalIgnoreCase),
            ("seats", false) => filtered.OrderBy(SeatsOf),
            ("seats", true) => filtered.OrderByDescending(SeatsOf),
            ("created", false) => filtered.OrderBy(c => c.CreateTime),
            ("created", true) => filtered.OrderByDescending(c => c.CreateTime),
            (_, true) => filtered.OrderByDescending(c => c.OrgName, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderBy(c => c.OrgName, StringComparer.OrdinalIgnoreCase),
        };

        var items = ordered.Skip(page * pageSize).Take(pageSize)
            .Select(c =>
            {
                rollups.TryGetValue(c.CustomerId, out var r);
                syncStates.TryGetValue(c.CustomerId, out var state);
                return new EstateCustomer
                {
                    CustomerId = c.CustomerId,
                    OrgName = c.OrgName,
                    Domain = c.Domain,
                    CloudIdentityId = c.CloudIdentityId,
                    OwningLinkId = c.OwningLinkId,
                    IsResellerSelf = c.IsResellerSelf,
                    SeatCount = r?.SeatCount ?? 0,
                    CreateTime = c.CreateTime,
                    LastSyncedUtc = state?.LastSyncedUtc ?? DateTimeOffset.MinValue,
                    EstimatedMonthlyTotal = r?.MonthlyTotal,
                    Currency = r?.Currency,
                    ActiveSubscriptions = r?.Active ?? 0,
                    SuspendedSubscriptions = r?.Suspended ?? 0,
                    NextRenewalUtc = r?.NextRenewal,
                    NextRenewalOfferName = r?.NextRenewalOffer,
                    NextRenewalAutoRenew = r?.NextRenewalAutoRenew,
                    ResellerName = c.OwningLinkId != null && links.TryGetValue(c.OwningLinkId, out var rn) ? rn : null,
                };
            })
            .ToList();

        // Estate-wide freshness (the most recent customer sync); never-synced rows are excluded.
        var asOf = syncStates.Values
            .Where(s => !s.IsDeleted && s.LastSyncedUtc > DateTimeOffset.MinValue)
            .Select(s => (DateTimeOffset?)s.LastSyncedUtc)
            .DefaultIfEmpty(null)
            .Max();

        return new PagedEstateResult<EstateCustomer> { Items = items, Total = filtered.Count, AsOf = asOf };
    }

    [WolverineGet("/api/estate/resellers")]
    public static async Task<PagedEstateResult<EstateReseller>> ListResellers(
        IQuerySession session, int page, int pageSize, string? sort, bool desc, string? search, string? state, CancellationToken ct)
    {
        pageSize = pageSize <= 0 ? 25 : Math.Clamp(pageSize, 1, MaxPageSize);
        page = Math.Max(0, page);

        var links = await session.Query<ResellerLinkRecord>().Where(l => !l.IsDeleted).ToListAsync(ct);
        var states = (await session.Query<LinkSyncState>().ToListAsync(ct)).ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

        IEnumerable<ResellerLinkRecord> q = links;
        if (!string.IsNullOrWhiteSpace(state))
        {
            q = q.Where(r => r.LinkState == state);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(r => (r.PrimaryDomain != null && r.PrimaryDomain.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                             (r.ResellerCloudId != null && r.ResellerCloudId.Contains(s, StringComparison.OrdinalIgnoreCase)) ||
                             r.LinkId.Contains(s, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = q.ToList();
        int CustomersOf(ResellerLinkRecord r) => states.TryGetValue(r.LinkId, out var st) ? st.CustomerCount : 0;

        var ordered = (sort, desc) switch
        {
            ("domain", false) => filtered.OrderBy(r => r.PrimaryDomain, StringComparer.OrdinalIgnoreCase),
            ("domain", true) => filtered.OrderByDescending(r => r.PrimaryDomain, StringComparer.OrdinalIgnoreCase),
            ("state", false) => filtered.OrderBy(r => r.LinkState),
            ("state", true) => filtered.OrderByDescending(r => r.LinkState),
            ("customers", false) => filtered.OrderBy(CustomersOf),
            ("customers", true) => filtered.OrderByDescending(CustomersOf),
            ("created", false) => filtered.OrderBy(r => r.CreateTime),
            ("created", true) => filtered.OrderByDescending(r => r.CreateTime),
            (_, true) => filtered.OrderByDescending(r => r.PrimaryDomain, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderBy(r => r.PrimaryDomain, StringComparer.OrdinalIgnoreCase),
        };

        var items = ordered.Skip(page * pageSize).Take(pageSize)
            .Select(r =>
            {
                states.TryGetValue(r.LinkId, out var st);
                return new EstateReseller
                {
                    LinkId = r.LinkId,
                    PrimaryDomain = r.PrimaryDomain,
                    ResellerCloudId = r.ResellerCloudId,
                    LinkState = r.LinkState,
                    CustomerCount = st?.CustomerCount ?? 0,
                    CreateTime = r.CreateTime,
                    LastSyncedUtc = st?.LastSyncedUtc ?? DateTimeOffset.MinValue,
                    SyncError = st?.SyncError,
                };
            })
            .ToList();

        var asOf = states.Values
            .Where(s => s.LastSyncedUtc > DateTimeOffset.MinValue)
            .Select(s => (DateTimeOffset?)s.LastSyncedUtc)
            .DefaultIfEmpty(null)
            .Max();

        return new PagedEstateResult<EstateReseller> { Items = items, Total = filtered.Count, AsOf = asOf };
    }

    // Estimated estate value for a single reseller (channel partner link): wholesale cost, repriced revenue
    // and margin across all of that reseller's customers' active priced entitlements. Per-currency; the
    // headline is the dominant one.
    [WolverineGet("/api/estate/resellers/{linkId}/value")]
    public static async Task<ResellerEstateValue> GetResellerValue(string linkId, IQuerySession session, CancellationToken ct)
    {
        var active = await session.Query<EntitlementRecord>()
            .Where(e => !e.IsDeleted && e.State == "ACTIVE" && e.OwningLinkId == linkId)
            .ToListAsync(ct);
        var currencies = EstateRollups.ValueByCurrency(active);
        var customerCount = await session.Query<CustomerRecord>().CountAsync(c => !c.IsDeleted && c.OwningLinkId == linkId, ct);
        var dominant = currencies.Count > 0 ? currencies[0] : null;
        return new ResellerEstateValue
        {
            Currency = dominant?.Currency,
            WholesaleMonthly = dominant?.WholesaleMonthly ?? 0m,
            RevenueMonthly = dominant?.RevenueMonthly ?? 0m,
            MarginMonthly = dominant?.MarginMonthly ?? 0m,
            MixedCurrencies = currencies.Count > 1,
            PricedEntitlementCount = currencies.Sum(c => c.PricedEntitlementCount),
            UnpricedEntitlementCount = active.Count(e => e.UnitPrice <= 0),
            ActiveSeats = currencies.Sum(c => c.ActiveSeats),
            CustomerCount = customerCount,
            Currencies = currencies,
        };
    }

    // Estimated monthly value for a single customer (direct or indirect).
    [WolverineGet("/api/estate/customers/{customerId}/value")]
    public static async Task<CustomerEstateValue> GetCustomerValue(string customerId, IQuerySession session, CancellationToken ct)
    {
        var active = await session.Query<EntitlementRecord>()
            .Where(e => !e.IsDeleted && e.State == "ACTIVE" && e.CustomerId == customerId)
            .ToListAsync(ct);
        var currencies = EstateRollups.ValueByCurrency(active);
        var dominant = currencies.Count > 0 ? currencies[0] : null;
        return new CustomerEstateValue
        {
            Currency = dominant?.Currency,
            WholesaleMonthly = dominant?.WholesaleMonthly ?? 0m,
            RevenueMonthly = dominant?.RevenueMonthly ?? 0m,
            MarginMonthly = dominant?.MarginMonthly ?? 0m,
            MixedCurrencies = currencies.Count > 1,
            PricedEntitlementCount = currencies.Sum(c => c.PricedEntitlementCount),
            UnpricedEntitlementCount = active.Count(e => e.UnitPrice <= 0),
            ActiveSeats = currencies.Sum(c => c.ActiveSeats),
            Currencies = currencies,
        };
    }

    // Estate-wide entitlements list the dashboard lifecycle KPIs link into. "direct" = reseller
    // self-purchases, "indirect" = reseller end customers, else the whole estate.
    [WolverineGet("/api/estate/entitlements")]
    public static async Task<PagedEstateResult<EstateEntitlement>> ListEntitlements(
        IQuerySession session, int page, int pageSize, string? sort, bool desc, string? search, string? state, string? scope, CancellationToken ct)
    {
        pageSize = pageSize <= 0 ? 25 : Math.Clamp(pageSize, 1, MaxPageSize);
        page = Math.Max(0, page);

        var query = session.Query<EntitlementRecord>().Where(e => !e.IsDeleted);
        query = (scope ?? "").ToLowerInvariant() switch
        {
            "direct" => query.Where(e => e.IsResellerSelf),
            "indirect" => query.Where(e => !e.IsResellerSelf),
            _ => query,
        };
        query = (state ?? "").ToLowerInvariant() switch
        {
            "active" => query.Where(e => e.State == "ACTIVE" && !e.IsTrial),
            "trial" => query.Where(e => e.IsTrial),
            "suspended" => query.Where(e => e.State == "SUSPENDED"),
            _ => query,
        };
        var rows = await query.ToListAsync(ct);

        var customers = (await session.Query<CustomerRecord>().ToListAsync(ct)).ToDictionary(c => c.CustomerId, StringComparer.OrdinalIgnoreCase);
        var syncStates = (await session.Query<CustomerSyncState>().ToListAsync(ct)).ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        string? NameOf(EntitlementRecord e) => customers.TryGetValue(e.CustomerId, out var c) ? c.OrgName : null;

        IEnumerable<EntitlementRecord> q = rows;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(e =>
                (NameOf(e)?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false) ||
                e.CustomerId.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                (e.OfferName?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.SkuName?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.ProductName?.Contains(s, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        var filtered = q.ToList();

        var ordered = (sort, desc) switch
        {
            ("customer", false) => filtered.OrderBy(NameOf, StringComparer.OrdinalIgnoreCase),
            ("customer", true) => filtered.OrderByDescending(NameOf, StringComparer.OrdinalIgnoreCase),
            ("product", false) => filtered.OrderBy(e => e.ProductName, StringComparer.OrdinalIgnoreCase),
            ("product", true) => filtered.OrderByDescending(e => e.ProductName, StringComparer.OrdinalIgnoreCase),
            ("seats", false) => filtered.OrderBy(e => e.Seats),
            ("seats", true) => filtered.OrderByDescending(e => e.Seats),
            ("state", false) => filtered.OrderBy(e => e.State),
            ("state", true) => filtered.OrderByDescending(e => e.State),
            ("renewal", false) => filtered.OrderBy(e => e.CommitmentEndTime),
            ("renewal", true) => filtered.OrderByDescending(e => e.CommitmentEndTime),
            ("created", false) => filtered.OrderBy(e => e.CreateTime),
            ("created", true) => filtered.OrderByDescending(e => e.CreateTime),
            (_, true) => filtered.OrderByDescending(NameOf, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderBy(NameOf, StringComparer.OrdinalIgnoreCase),
        };

        var items = ordered.Skip(page * pageSize).Take(pageSize)
            .Select(e => new EstateEntitlement
            {
                EntitlementId = e.EntitlementId,
                CustomerId = e.CustomerId,
                CustomerName = NameOf(e),
                OwningLinkId = e.OwningLinkId,
                ProductName = e.ProductName,
                SkuName = e.SkuName,
                OfferName = e.OfferName,
                State = e.State,
                IsTrial = e.IsTrial,
                Seats = e.Seats,
                BillableSeats = e.BillableSeats,
                UnitPrice = e.UnitPrice,
                Currency = e.Currency,
                RepricingPercent = e.RepricingPercent,
                CommitmentEndTime = e.CommitmentEndTime,
                CreateTime = e.CreateTime,
                LastSyncedUtc = syncStates.TryGetValue(e.CustomerId, out var st) ? st.LastSyncedUtc : e.LastChangedUtc,
            })
            .ToList();

        return new PagedEstateResult<EstateEntitlement>
        {
            Items = items,
            Total = filtered.Count,
            AsOf = items.Count == 0 ? null : items.Min(i => i.LastSyncedUtc),
        };
    }

    // Refresh now: zero the link's sync stamp so the next cycle fans it out first.
    [WolverinePost("/api/estate/resellers/{linkId}/resync")]
    public static async Task<IResult> ResyncReseller(string linkId, IDocumentSession session, CancellationToken ct)
    {
        var link = await session.LoadAsync<ResellerLinkRecord>(EstateStreams.Link(linkId), ct);
        if (link is null)
        {
            return Results.NotFound();
        }
        var state = await session.LoadAsync<LinkSyncState>(linkId, ct) ?? new LinkSyncState { Id = linkId };
        state.LastSyncedUtc = DateTimeOffset.MinValue;
        session.Store(state);
        await session.SaveChangesAsync(ct);
        return Results.Accepted();
    }

    // Refresh now: zero the customer's sync stamp (and its owning link's) AND ask the worker for an immediate
    // targeted re-projection of the customer.
    [WolverinePost("/api/estate/customers/{customerId}/resync")]
    public static async Task<IResult> ResyncCustomer(string customerId, IDocumentSession session, IMessageBus bus, CancellationToken ct)
    {
        var customer = await session.LoadAsync<CustomerRecord>(EstateStreams.Customer(customerId), ct);
        if (customer is null)
        {
            return Results.NotFound();
        }
        var state = await session.LoadAsync<CustomerSyncState>(customerId, ct) ?? new CustomerSyncState { Id = customerId, OwningLinkId = customer.OwningLinkId };
        state.LastSyncedUtc = DateTimeOffset.MinValue;
        session.Store(state);
        if (!string.IsNullOrEmpty(customer.OwningLinkId))
        {
            var link = await session.LoadAsync<LinkSyncState>(customer.OwningLinkId, ct) ?? new LinkSyncState { Id = customer.OwningLinkId };
            link.LastSyncedUtc = DateTimeOffset.MinValue;
            session.Store(link);
        }
        await bus.PublishAsync(new ProjectCustomer(customerId, "manual resync"));
        await session.SaveChangesAsync(ct);
        return Results.Accepted();
    }
}
