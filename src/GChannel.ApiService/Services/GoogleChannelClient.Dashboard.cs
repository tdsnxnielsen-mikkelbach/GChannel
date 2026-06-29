using GChannel.ApiService.Configuration;
using GChannel.Shared.Contracts;
using Google.Apis.Cloudchannel.v1;
using Google.Apis.Cloudchannel.v1.Data;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Util;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net;

namespace GChannel.ApiService.Services;

// Dashboard aggregation — see IGoogleChannelClient for the contract documentation.
public sealed partial class GoogleChannelClient
{
    public async Task<DashboardOverview> GetDashboardOverviewAsync(CancellationToken cancellationToken)
    {
        EnsureAccountConfigured();
        using var service = CreateService();

        // Pace the (single, paginated) account customer-list call through the shared ListCustomers
        // quota bucket. This one list now also yields the indirect estate (customers carry a
        // ChannelPartnerId), so no per-reseller customer-list calls are made.
        var customerListPacer = CreateCustomerListPacer();

        // Cheap, quota-light first phase of the dashboard: just the customer list (one paginated
        // call set) drives the headline (direct) customer count and the onboarded-over-time chart, so
        // the UI can render these immediately while the slower aggregation loads.
        var customers = await ListAllCustomersAsync(service, customerListPacer, cancellationToken);

        // Channel partner links (§5) are an account-level list (no per-customer fan-out), so counting
        // them here keeps the overview cheap while restoring the "Channel links" headline figure. The
        // BASIC view carries link_state, so the per-state breakdown costs no extra quota. The
        // downstream (indirect) customer estate is heavier — one list call per reseller — so it is
        // computed in the (background-only) summary phase, not here.
        var (channelLinkCount, channelLinkStates) = await SummarizeChannelPartnerLinksAsync(service, cancellationToken);

        return new DashboardOverview
        {
            CustomerCount = customers.Count,
            ChannelLinkCount = channelLinkCount,
            ChannelLinkStates = channelLinkStates,
            CustomersOnboarded = BuildMonthlyOnboarded(customers)
        };
    }

    public async Task<DashboardSummary> GetDashboardSummaryAsync(
        CancellationToken cancellationToken,
        bool applyTimeBudget = true,
        Func<DashboardSummary, Task>? onPartial = null,
        int partialEvery = 0)
    {
        EnsureAccountConfigured();
        using var service = CreateService();

        // §3 entitlements — one List call per customer is unavoidable (there is no cross-customer
        // list). To guarantee the endpoint responds well within the caller's HTTP attempt timeout
        // (so it never gets cut off mid-flight and the cached result can warm up), the whole
        // aggregation runs under a single time budget with bounded parallelism. Customers not reached
        // within the budget, or that error out, are reported as skipped together with the reason why.
        // The background refresher (applyTimeBudget: false) runs unbounded so its cached result is
        // complete even for large estates where the on-demand budget would otherwise truncate it.
        var budgetSeconds = Math.Max(5, _options.DashboardBudgetSeconds);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (applyTimeBudget)
        {
            budget.CancelAfter(TimeSpan.FromSeconds(budgetSeconds));
        }
        var budgetToken = budget.Token;

        // Pace the (paginated) account customers.list call plus the per-reseller fan-out below through
        // one shared ListCustomers quota bucket so they stay under the Channel API's "ListCustomers
        // requests per minute" quota. Disabled when set to 0.
        var customerListPacer = CreateCustomerListPacer();

        // Pace the per-customer entitlements.list calls to stay under the Channel API's per-minute
        // "ListEntitlements" quota. Without this, bounded concurrency alone still bursts past the
        // quota on large estates and the project gets a wave of 429s. Disabled when set to 0. Shared
        // by the direct aggregation below and the indirect per-reseller seat sum.
        var pacer = _options.DashboardRequestsPerMinute > 0
            ? new RequestPacer(TimeSpan.FromSeconds(60.0 / _options.DashboardRequestsPerMinute))
            : null;

        List<Customer> customers;
        CatalogLookups lookups;
        int indirectCustomers;
        IReadOnlyList<DashboardResellerCustomers> topResellers;
        try
        {
            // §2 customers — also drives the onboarded-over-time chart (bucket by create time).
            customers = await ListAllCustomersAsync(service, customerListPacer, budgetToken);

            // §1 catalog — products.list + offers.list + products.skus.list give the full
            // offer→SKU→product fallback chain so the Product mix labels resolve even when an
            // entitlement's specific offer is no longer listed.
            lookups = await BuildCatalogLookupsAsync(service, budgetToken, includeSkus: true);

            // §5 indirect estate — the downstream end customers owned by each linked indirect
            // reseller. A distributor's accounts.customers.list returns only its own direct customers,
            // so the reseller estate must be enumerated with one channelPartnerLinks.customers.list per
            // ACTIVE link — 40+ calls that cannot fit the on-demand time budget under the tight shared
            // ListCustomers quota. So only the (unbudgeted) background refresher computes it; the
            // on-demand path leaves it empty and the UI shows the last value warmed into the cache.
            (indirectCustomers, topResellers) = applyTimeBudget
                ? (0, [])
                : await GetIndirectEstateAsync(service, customerListPacer, pacer, budgetToken);
        }
        catch (OperationCanceledException) when (applyTimeBudget && !cancellationToken.IsCancellationRequested)
        {
            // The budget tripped while loading the customer list / catalog — almost always because the
            // per-minute quota was exhausted and the request was stuck in retry back-off. Fail fast
            // with a non-cancellation exception so the endpoint serves the last-known-good cached
            // result instead of the caller blowing its HTTP timeout waiting for a doomed aggregation.
            throw new TimeoutException(
                $"Dashboard aggregation did not load the customer list within the {budgetSeconds}s time budget.");
        }

        using var throttle = new SemaphoreSlim(Math.Max(1, _options.DashboardMaxConcurrency));

        // The onboarding chart is constant across the run; compute it once for every snapshot.
        var onboarded = BuildMonthlyOnboarded(customers);

        // Accumulators merged as each customer completes (rather than after the whole Task.WhenAll) so
        // the background refresher can publish a growing partial summary while the quota-paced
        // aggregation is still in flight. All mutation happens under the gate.
        var gate = new object();
        var active = 0;
        var trials = 0;
        var suspended = 0;
        long activeSeats = 0;
        var notReached = 0;
        var failed = 0;
        var completed = 0;
        var failureReasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var productMix = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Builds a summary from the accumulators as they currently stand. Caller must hold the gate.
        DashboardSummary BuildSnapshotLocked() => new()
        {
            CustomerCount = customers.Count,
            IndirectCustomerCount = indirectCustomers,
            TopIndirectResellers = topResellers,
            ActiveEntitlementCount = active,
            TrialEntitlementCount = trials,
            SuspendedEntitlementCount = suspended,
            ActiveSeats = activeSeats,
            SkippedCustomerCount = notReached + failed,
            IncompleteReason = BuildIncompleteReason(notReached, failed, failureReasons, budgetSeconds),
            CustomersOnboarded = onboarded,
            ProductMix = productMix
                .OrderByDescending(kv => kv.Value)
                .Take(8)
                .Select(kv => new DashboardProductSlice { Product = kv.Key, Count = kv.Value })
                .ToList()
        };

        await Task.WhenAll(customers.Select(async customer =>
        {
            CustomerLoadResult result;
            var acquired = false;
            try
            {
                await throttle.WaitAsync(budgetToken);
                acquired = true;
                var aggregate = await AggregateCustomerEntitlementsAsync(
                    service, customer.Id, lookups, pacer, budgetToken);
                result = CustomerLoadResult.Loaded(aggregate);
            }
            catch (OperationCanceledException)
            {
                // Either the caller went away (request token) or the time budget elapsed. Swallow
                // per task so the debugger doesn't flag each in-flight parallel call as
                // user-unhandled; the genuine-cancellation check below decides whether to abort.
                result = CustomerLoadResult.NotReached;
            }
            catch (Google.GoogleApiException ex)
            {
                // One customer failing to list entitlements (e.g. permission/transient) must not
                // sink the whole dashboard. Record the reason and skip it.
                logger.LogWarning(ex, "Skipping customer {Customer} in dashboard summary: {Status}", customer.Id, ex.HttpStatusCode);
                result = CustomerLoadResult.Failed(DescribeApiError(ex));
            }
            finally
            {
                if (acquired)
                {
                    throttle.Release();
                }
            }

            DashboardSummary? snapshot = null;
            lock (gate)
            {
                switch (result.Outcome)
                {
                    case CustomerLoadOutcome.NotReachedInTime:
                        notReached++;
                        break;
                    case CustomerLoadOutcome.Failed:
                        failed++;
                        var reason = result.FailureReason ?? "unknown error";
                        failureReasons[reason] = failureReasons.GetValueOrDefault(reason) + 1;
                        break;
                    default:
                        var aggregate = result.Aggregate;
                        active += aggregate.Active;
                        trials += aggregate.Trials;
                        suspended += aggregate.Suspended;
                        activeSeats += aggregate.ActiveSeats;
                        foreach (var (label, count) in aggregate.ProductMix)
                        {
                            productMix[label] = productMix.GetValueOrDefault(label) + count;
                        }
                        break;
                }

                completed++;
                if (onPartial is not null && partialEvery > 0 && completed % partialEvery == 0)
                {
                    snapshot = BuildSnapshotLocked();
                }
            }

            if (snapshot is not null)
            {
                try
                {
                    await onPartial!(snapshot);
                }
                catch
                {
                    // Publishing progress is best-effort; never let it disturb the aggregation.
                }
            }
        }));

        // Only abort if the caller actually went away; a tripped time budget is expected and yields
        // a partial (but timely) summary rather than a failure.
        cancellationToken.ThrowIfCancellationRequested();

        DashboardSummary summary;
        lock (gate)
        {
            summary = BuildSnapshotLocked();
        }

        if (summary.IncompleteReason is not null)
        {
            logger.LogWarning("Dashboard summary incomplete: {Reason}", summary.IncompleteReason);
        }

        return summary;
    }

    private enum CustomerLoadOutcome { Loaded, NotReachedInTime, Failed }

    /// <summary>Outcome of loading one customer's entitlements during the dashboard aggregation.</summary>
    private readonly record struct CustomerLoadResult(
        CustomerLoadOutcome Outcome, EntitlementAggregate Aggregate, string? FailureReason)
    {
        public static CustomerLoadResult Loaded(EntitlementAggregate aggregate) =>
            new(CustomerLoadOutcome.Loaded, aggregate, null);

        public static readonly CustomerLoadResult NotReached =
            new(CustomerLoadOutcome.NotReachedInTime, default, null);

        public static CustomerLoadResult Failed(string reason) =>
            new(CustomerLoadOutcome.Failed, default, reason);
    }

    /// <summary>Turns a Channel API error into a short reason string for the "incomplete" note.</summary>
    private static string DescribeApiError(Google.GoogleApiException ex)
    {
        var code = (int)ex.HttpStatusCode;
        return code > 0 ? $"{code} {ex.HttpStatusCode}" : "API error";
    }

    /// <summary>Builds the human-readable "why is this incomplete" note, or null when nothing was skipped.</summary>
    private static string? BuildIncompleteReason(
        int notReached, int failed, IReadOnlyDictionary<string, int> failureReasons, int budgetSeconds)
    {
        if (notReached == 0 && failed == 0)
        {
            return null;
        }

        var parts = new List<string>();
        if (notReached > 0)
        {
            parts.Add($"{notReached} not loaded within the {budgetSeconds}s time budget");
        }

        if (failed > 0)
        {
            var detail = string.Join(", ", failureReasons
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Value}\u00d7 {kv.Key}"));
            parts.Add($"{failed} failed ({detail})");
        }

        return string.Join("; ", parts) + ".";
    }

    private readonly record struct EntitlementAggregate(
        int Active, int Trials, int Suspended, long ActiveSeats, IReadOnlyDictionary<string, int> ProductMix);

    /// <summary>Paginates a single customer's entitlements and returns its partial dashboard aggregate.</summary>
    private async Task<EntitlementAggregate> AggregateCustomerEntitlementsAsync(
        CloudchannelService service,
        string customerId,
        CatalogLookups lookups,
        RequestPacer? pacer,
        CancellationToken cancellationToken)
    {
        var active = 0;
        var trials = 0;
        var suspended = 0;
        long activeSeats = 0;
        var productMix = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        string? entitlementToken = null;
        do
        {
            // Each page is one ListEntitlements request against the per-minute quota, so pace it.
            if (pacer is not null)
            {
                await pacer.WaitAsync(cancellationToken);
            }

            var request = service.Accounts.Customers.Entitlements.List(CustomerName(customerId));
            request.PageToken = entitlementToken;
            var response = await request.ExecuteAsync(cancellationToken);

            foreach (var raw in response.Entitlements ?? [])
            {
                var entitlement = MapEntitlement(raw, lookups);
                var isActive = string.Equals(entitlement.ProvisioningState, "ACTIVE", StringComparison.OrdinalIgnoreCase);

                if (isActive)
                {
                    active++;

                    var seats = entitlement.Parameters
                        .FirstOrDefault(p => string.Equals(p.Name, "num_units", StringComparison.OrdinalIgnoreCase))?.Value;
                    if (long.TryParse(seats, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    {
                        activeSeats += n;
                    }

                    // MapEntitlement already resolved the product name from the offer/SKU/product
                    // catalogs; fall back to the raw product id only when nothing could be resolved.
                    var label = entitlement.ProductDisplayName ?? entitlement.ProductId ?? "Other";
                    productMix[label] = productMix.GetValueOrDefault(label) + 1;
                }

                if (entitlement.IsTrial)
                {
                    trials++;
                }

                if (string.Equals(entitlement.ProvisioningState, "SUSPENDED", StringComparison.OrdinalIgnoreCase))
                {
                    suspended++;
                }
            }

            entitlementToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(entitlementToken));

        return new EntitlementAggregate(active, trials, suspended, activeSeats, productMix);
    }

    /// <summary>
    /// Builds the pacer that throttles every ListCustomers call (the account list and the per-reseller
    /// fan-out) through the shared "ListCustomers requests per minute" quota, or <c>null</c> when
    /// pacing is disabled.
    /// </summary>
    private RequestPacer? CreateCustomerListPacer() =>
        _options.DashboardCustomerListRequestsPerMinute > 0
            ? new RequestPacer(TimeSpan.FromSeconds(60.0 / _options.DashboardCustomerListRequestsPerMinute))
            : null;

    /// <summary>Paginates the full reseller customer list (shared by the overview and summary).</summary>
    private async Task<List<Customer>> ListAllCustomersAsync(
        CloudchannelService service, RequestPacer? pacer, CancellationToken cancellationToken)
    {
        var customers = new List<Customer>();
        string? pageToken = null;
        do
        {
            if (pacer is not null)
            {
                await pacer.WaitAsync(cancellationToken);
            }

            var request = service.Accounts.Customers.List(_options.AccountName);
            request.PageToken = pageToken;
            var response = await request.ExecuteAsync(cancellationToken);

            foreach (var customer in response.Customers ?? [])
            {
                customers.Add(MapCustomer(customer));
            }

            pageToken = response.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return customers;
    }

    /// <summary>Buckets customers into the trailing six months by their create time (oldest first).</summary>
    private static List<DashboardMonthlyPoint> BuildMonthlyOnboarded(IReadOnlyList<Customer> customers)
    {
        var now = DateTimeOffset.UtcNow;
        var firstOfThisMonth = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

        var points = new List<DashboardMonthlyPoint>(6);
        for (var i = 5; i >= 0; i--)
        {
            var monthStart = firstOfThisMonth.AddMonths(-i);
            var monthEnd = monthStart.AddMonths(1);
            var count = customers.Count(c => c.CreateTime is { } t && t >= monthStart && t < monthEnd);

            points.Add(new DashboardMonthlyPoint
            {
                Month = monthStart.ToString("MMM", CultureInfo.InvariantCulture),
                Customers = count
            });
        }

        return points;
    }
}
