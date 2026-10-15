using GChannel.ApiService.Configuration;
using GChannel.ApiService.Data;
using GChannel.ApiService.Services;
using GChannel.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;

namespace GChannel.ApiService.Endpoints;

/// <summary>
/// Cloud Billing budget endpoints (billingbudgets.googleapis.com). Separate from the Channel API:
/// these read/write budgets on the reseller's billing accounts and per-customer sub-accounts using the
/// reseller service-account credential. See <see cref="IBillingBudgetsService"/>.
/// </summary>
public static class BillingEndpoints
{
    public static IEndpointRouteBuilder MapBillingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/billing").WithTags("Billing");

        group.MapGet("/accounts", async (
                IBillingBudgetsService billing,
                CancellationToken cancellationToken) =>
            {
                if (!billing.IsConfigured)
                {
                    return Results.Ok(new BillingAccountsResult
                    {
                        DiscoveryAvailable = false,
                        DiscoveryError = "No service-account credential configured (GoogleChannel:ServiceAccountKeyJson)."
                    });
                }

                return Results.Ok(await billing.ListBillingAccountsAsync(cancellationToken));
            })
            .WithName("ListBillingAccounts")
            .WithSummary("Lists the reseller's billing accounts and sub-accounts (discovered or configured).");

        group.MapGet("/accounts/{billingAccountId}/budgets", async (
                string billingAccountId,
                IBillingBudgetsService billing,
                CancellationToken cancellationToken) =>
            {
                if (!billing.IsConfigured)
                {
                    return Results.Problem("Billing budgets are not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                return Results.Ok(await billing.ListBudgetsAsync(billingAccountId, cancellationToken));
            })
            .WithName("ListBudgets")
            .WithSummary("Lists budgets for a billing account.");

        group.MapGet("/accounts/{billingAccountId}/members", async (
                string billingAccountId,
                IBillingBudgetsService billing,
                CancellationToken cancellationToken) =>
            {
                if (!billing.IsConfigured)
                {
                    return Results.Ok(new BillingAccountMembersResult
                    {
                        BillingAccountId = billingAccountId,
                        Available = false,
                        Error = "No service-account credential configured (GoogleChannel:ServiceAccountKeyJson)."
                    });
                }

                return Results.Ok(await billing.GetBillingAccountMembersAsync(billingAccountId, cancellationToken));
            })
            .WithName("ListBillingAccountMembers")
            .WithSummary("Lists a billing (sub-)account's IAM members, split into direct and inherited grants.");

        group.MapPost("/members/identity", ResolveMemberIdentitiesAsync)
            .WithName("ResolveBillingMemberIdentities")
            .WithSummary("Classifies member emails as managed/consumer Google Accounts and reads 2SV + Advanced Protection where visible.");

        group.MapPost("/budgets", async (
                SaveBudgetRequest request,
                IBillingBudgetsService billing,
                CancellationToken cancellationToken) =>
            {
                if (!billing.IsConfigured)
                {
                    return Results.Problem("Billing budgets are not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                var saved = await billing.SaveBudgetAsync(request, cancellationToken);
                return Results.Ok(saved);
            })
            .WithName("SaveBudget")
            .WithSummary("Creates a budget (no budget id) or updates an existing one (budget id set).");

        group.MapDelete("/accounts/{billingAccountId}/budgets/{budgetId}", async (
                string billingAccountId,
                string budgetId,
                IBillingBudgetsService billing,
                CancellationToken cancellationToken) =>
            {
                if (!billing.IsConfigured)
                {
                    return Results.Problem("Billing budgets are not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                await billing.DeleteBudgetAsync(billingAccountId, budgetId, cancellationToken);
                return Results.NoContent();
            })
            .WithName("DeleteBudget")
            .WithSummary("Deletes a budget.");

        // §15 actual spend (BigQuery detailed billing export) — served from the CustomerSpend/SpendDaily
        // read-model the worker's BillingSyncService populates. BILLED figures, distinct from §11 estimates.

        // Bulk last-window billed totals per customer (for the customer-list "Billed" column). Literal
        // "/customers/spend" outranks the "/customers/{customerId}/spend" param route below.
        group.MapGet("/customers/spend", async (
                int? months,
                GChannelDbContext db,
                IOptions<GoogleBillingOptions> options,
                CancellationToken cancellationToken) =>
            {
                if (!options.Value.BigQueryConfigured)
                {
                    return Results.Ok(new CustomerBilledSummaryResult { Unconfigured = true });
                }

                var n = Math.Clamp(months ?? 1, 1, 12);
                var minMonth = DateTime.UtcNow.AddMonths(-(n - 1)).ToString("yyyyMM", CultureInfo.InvariantCulture);
                var rows = await db.CustomerSpend.AsNoTracking()
                    .Where(s => s.CustomerId != null && string.Compare(s.InvoiceMonth, minMonth) >= 0)
                    .ToListAsync(cancellationToken);

                var summaries = rows
                    .GroupBy(r => new { r.CustomerId, r.Currency })
                    .Select(g => new CustomerBilledSummary
                    {
                        CustomerId = g.Key.CustomerId!,
                        Currency = g.Key.Currency,
                        Cost = g.Sum(x => x.Cost)
                    })
                    .ToList();

                return Results.Ok(new CustomerBilledSummaryResult
                {
                    Customers = summaries,
                    AsOf = rows.Count > 0 ? rows.Max(r => r.LastSyncedUtc) : null
                });
            })
            .WithName("CustomerBilledSummaries")
            .WithSummary("Bulk last-window billed totals per customer for the customer list.");

        group.MapGet("/customers/{customerId}/spend", async (
                string customerId,
                int? months,
                GChannelDbContext db,
                IOptions<GoogleBillingOptions> options,
                CancellationToken cancellationToken) =>
            {
                if (!options.Value.BigQueryConfigured)
                {
                    return Results.Ok(new CustomerSpendResult { CustomerId = customerId, Unconfigured = true });
                }

                var n = Math.Clamp(months ?? 13, 1, 36);
                var minMonth = DateTime.UtcNow.AddMonths(-(n - 1)).ToString("yyyyMM", CultureInfo.InvariantCulture);
                var rows = await db.CustomerSpend.AsNoTracking()
                    .Where(s => s.CustomerId == customerId && string.Compare(s.InvoiceMonth, minMonth) >= 0)
                    .ToListAsync(cancellationToken);

                var points = rows
                    .OrderBy(r => r.InvoiceMonth).ThenBy(r => r.Currency)
                    .Select(r => new CustomerSpendPoint
                    {
                        InvoiceMonth = r.InvoiceMonth,
                        Currency = r.Currency,
                        Cost = r.Cost,
                        GrossCost = r.GrossCost,
                        Credits = r.Credits
                    })
                    .ToList();

                return Results.Ok(new CustomerSpendResult
                {
                    CustomerId = customerId,
                    Months = points,
                    AsOf = rows.Count > 0 ? rows.Max(r => r.LastSyncedUtc) : null
                });
            })
            .WithName("CustomerSpend")
            .WithSummary("Monthly actual (billed) spend for a customer from the BigQuery cost export.");

        group.MapGet("/customers/{customerId}/trend", async (
                string customerId,
                int? days,
                GChannelDbContext db,
                IOptions<GoogleBillingOptions> options,
                CancellationToken cancellationToken) =>
            {
                var opts = options.Value;
                if (!opts.BigQueryConfigured)
                {
                    return Results.Ok(new SpendTrendResult { CustomerId = customerId, Unconfigured = true });
                }

                var n = Math.Clamp(days ?? opts.TrendDays, 1, 365);
                var minDay = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-(n - 1)));
                var rows = await db.SpendDaily.AsNoTracking()
                    .Where(d => d.CustomerId == customerId && d.Day >= minDay)
                    .ToListAsync(cancellationToken);

                var points = new List<SpendTrendPoint>();
                foreach (var byCurrency in rows.GroupBy(r => r.Currency))
                {
                    var ordered = byCurrency.OrderBy(r => r.Day).ToList();
                    var flags = DetectSpikes([.. ordered.Select(r => (double)r.Cost)], opts.SpikeWindowDays, opts.SpikeStdDevK);
                    for (var i = 0; i < ordered.Count; i++)
                    {
                        points.Add(new SpendTrendPoint
                        {
                            Day = ordered[i].Day,
                            Currency = byCurrency.Key,
                            Cost = ordered[i].Cost,
                            IsSpike = flags[i]
                        });
                    }
                }

                return Results.Ok(new SpendTrendResult
                {
                    CustomerId = customerId,
                    Days = [.. points.OrderBy(p => p.Day).ThenBy(p => p.Currency)],
                    AsOf = rows.Count > 0 ? rows.Max(r => r.LastSyncedUtc) : null
                });
            })
            .WithName("CustomerSpendTrend")
            .WithSummary("Daily actual spend series with flagged spikes for a customer.");

        group.MapGet("/spend/top", async (
                int? months,
                int? limit,
                GChannelDbContext db,
                IOptions<GoogleBillingOptions> options,
                CancellationToken cancellationToken) =>
            {
                if (!options.Value.BigQueryConfigured)
                {
                    return Results.Ok(new TopSpendersResult { Unconfigured = true });
                }

                var n = Math.Clamp(months ?? 1, 1, 12);
                var lim = Math.Clamp(limit ?? 10, 1, 100);
                var minMonth = DateTime.UtcNow.AddMonths(-(n - 1)).ToString("yyyyMM", CultureInfo.InvariantCulture);
                var rows = await db.CustomerSpend.AsNoTracking()
                    .Where(s => s.CustomerId != null && string.Compare(s.InvoiceMonth, minMonth) >= 0)
                    .ToListAsync(cancellationToken);

                var top = rows
                    .GroupBy(r => new { r.CustomerId, r.OwningLinkId, r.Currency })
                    .Select(g => new TopSpender
                    {
                        CustomerId = g.Key.CustomerId!,
                        OwningLinkId = g.Key.OwningLinkId,
                        Currency = g.Key.Currency,
                        Cost = g.Sum(x => x.Cost)
                    })
                    .OrderByDescending(t => t.Cost)
                    .Take(lim)
                    .ToList();

                var topNames = await ResolveResellerNamesAsync(db, top.Select(t => t.OwningLinkId), cancellationToken);
                top = top.Select(t => t.OwningLinkId != null && topNames.TryGetValue(t.OwningLinkId, out var rn)
                    ? t with { ResellerName = rn } : t).ToList();

                return Results.Ok(new TopSpendersResult
                {
                    Spenders = top,
                    AsOf = rows.Count > 0 ? rows.Max(r => r.LastSyncedUtc) : null
                });
            })
            .WithName("TopSpenders")
            .WithSummary("Estate-wide top spenders by billed cost over the recent window.");

        group.MapGet("/resellers/spend", async (
                int? months,
                GChannelDbContext db,
                IOptions<GoogleBillingOptions> options,
                CancellationToken cancellationToken) =>
            {
                if (!options.Value.BigQueryConfigured)
                {
                    return Results.Ok(new ResellerSpendResult { Unconfigured = true });
                }

                var n = Math.Clamp(months ?? 1, 1, 12);
                var minMonth = DateTime.UtcNow.AddMonths(-(n - 1)).ToString("yyyyMM", CultureInfo.InvariantCulture);
                var rows = await db.CustomerSpend.AsNoTracking()
                    .Where(s => string.Compare(s.InvoiceMonth, minMonth) >= 0)
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

                var resellerNames = await ResolveResellerNamesAsync(db, resellers.Select(r => r.OwningLinkId), cancellationToken);
                resellers = resellers.Select(r => r.OwningLinkId != null && resellerNames.TryGetValue(r.OwningLinkId, out var rn)
                    ? r with { ResellerName = rn } : r).ToList();

                return Results.Ok(new ResellerSpendResult
                {
                    Resellers = resellers,
                    AsOf = rows.Count > 0 ? rows.Max(r => r.LastSyncedUtc) : null
                });
            })
            .WithName("ResellerSpend")
            .WithSummary("Billed spend rolled up per owning reseller (channel partner).");

        group.MapGet("/resellers/{linkId}/spend", async (
                string linkId,
                int? months,
                GChannelDbContext db,
                IOptions<GoogleBillingOptions> options,
                CancellationToken cancellationToken) =>
            {
                if (!options.Value.BigQueryConfigured)
                {
                    return Results.Ok(new ResellerSpendDetailResult { LinkId = linkId, Unconfigured = true });
                }

                var n = Math.Clamp(months ?? 13, 1, 36);
                var minMonth = DateTime.UtcNow.AddMonths(-(n - 1)).ToString("yyyyMM", CultureInfo.InvariantCulture);
                var rows = await db.CustomerSpend.AsNoTracking()
                    .Where(s => s.OwningLinkId == linkId && string.Compare(s.InvoiceMonth, minMonth) >= 0)
                    .ToListAsync(cancellationToken);

                var points = rows
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
                    .ToList();

                return Results.Ok(new ResellerSpendDetailResult
                {
                    LinkId = linkId,
                    Months = points,
                    AsOf = rows.Count > 0 ? rows.Max(r => r.LastSyncedUtc) : null
                });
            })
            .WithName("ResellerSpendDetail")
            .WithSummary("Monthly billed spend for one owning reseller (channel partner) link.");

        return app;
    }

    private static readonly HashSet<string> ConsumerDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com"
    };

    private const int MaxIdentityBatch = 100;

    /// <summary>
    /// Per member email: (1) gmail.com → consumer; (2) found in our own Workspace directory (Admin SDK via
    /// DWD) → managed, plus 2SV + Advanced Protection; (3) otherwise ask the Channel API whether the email's
    /// domain has a Cloud Identity / Workspace tenant (<c>checkCloudIdentityAccountsExist</c>) — a
    /// domain-verified tenant means managed, a team account means partially managed, none means consumer.
    /// (4) For a managed tenant that is a resold customer, retry the Admin SDK lookup scoped to that
    /// customer's id: the reseller admin's delegated Admin-console access then also yields 2SV / Advanced
    /// Protection for the customer's users. Results are cached per email; unreadable signals come back
    /// null/Unknown rather than failing.
    /// </summary>
    private static async Task<IResult> ResolveMemberIdentitiesAsync(
        MemberIdentityRequest request,
        IWorkspaceUserSecurityService workspace,
        IGoogleChannelClient channel,
        IDistributedCache cache,
        GChannelDbContext db,
        IOptions<GoogleChannelOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("BillingEndpoints");
        var emails = request.Emails
            .Where(e => !string.IsNullOrWhiteSpace(e) && e.Contains('@'))
            .Select(e => e.Trim().ToLowerInvariant())
            .Distinct()
            .Take(MaxIdentityBatch)
            .ToList();

        var ttl = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(Math.Max(60, options.Value.CacheSeconds))
        };

        // Domain → Cloud Identity check, memoised across this batch (and in Redis via the same key the
        // Cloud-identity-check page uses, so both features share one Channel API call per domain).
        var domainCache = new Dictionary<string, CheckCloudIdentityResult?>(StringComparer.OrdinalIgnoreCase);
        async Task<CheckCloudIdentityResult?> CheckDomainAsync(string domain)
        {
            if (domainCache.TryGetValue(domain, out var hit)) { return hit; }

            var key = $"cid:{domain}:";
            CheckCloudIdentityResult? result = null;
            try
            {
                var cached = await cache.GetStringAsync(key, cancellationToken);
                if (cached is not null)
                {
                    result = JsonSerializer.Deserialize<CheckCloudIdentityResult>(cached);
                }
                else
                {
                    result = await channel.CheckCloudIdentityAsync(new CheckCloudIdentityRequest { Domain = domain }, cancellationToken);
                    await cache.SetStringAsync(key, JsonSerializer.Serialize(result), ttl, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Cloud Identity check failed for member domain {Domain}", domain);
            }

            domainCache[domain] = result;
            return result;
        }

        // Workspace customer id ("C01aihlmh") for a managed domain: prefer the id the Channel API just
        // returned for the DOMAIN-type account; fall back to our read-model customer row for the domain.
        var customerIdCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        async Task<string?> ResolveCustomerIdAsync(string domain, CheckCloudIdentityResult? cid)
        {
            if (customerIdCache.TryGetValue(domain, out var hit)) { return hit; }

            var id = cid?.Accounts.FirstOrDefault(a => a.IsDomain && a.Existing)?.CustomerCloudIdentityId;
            if (string.IsNullOrWhiteSpace(id))
            {
                id = await db.CustomerRecords.AsNoTracking()
                    .Where(c => c.CloudIdentityId != null && (c.Domain == domain || c.CloudIdentityDomain == domain))
                    .Select(c => c.CloudIdentityId)
                    .FirstOrDefaultAsync(cancellationToken);
            }

            customerIdCache[domain] = id;
            return id;
        }

        var members = new List<MemberIdentityInfo>(emails.Count);
        string? directoryError = null;

        foreach (var email in emails)
        {
            var cacheKey = $"member-identity:{email}";
            var cachedJson = await cache.GetStringAsync(cacheKey, cancellationToken);
            if (cachedJson is not null)
            {
                var cachedInfo = JsonSerializer.Deserialize<MemberIdentityInfo>(cachedJson);
                if (cachedInfo is not null) { members.Add(cachedInfo); continue; }
            }

            var domain = email[(email.IndexOf('@') + 1)..];
            var info = new MemberIdentityInfo { Email = email };

            if (ConsumerDomains.Contains(domain))
            {
                info = info with
                {
                    AccountKind = GoogleAccountKind.Consumer,
                    AccountKindReason = $"{domain} is a consumer Google Account domain."
                };
            }
            else
            {
                WorkspaceUserSecurity? sec = null;
                if (workspace.IsConfigured)
                {
                    try
                    {
                        sec = await workspace.GetUserSecurityAsync(email, customerId: null, cancellationToken);
                        if (sec.Error is not null) { directoryError ??= sec.Error; }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Workspace security lookup failed for {Email}", email);
                        directoryError ??= ex.Message;
                    }
                }

                if (sec is { Found: true })
                {
                    info = info with
                    {
                        AccountKind = GoogleAccountKind.Managed,
                        AccountKindReason = "User exists in the reseller's Google Workspace directory.",
                        InResellerDirectory = true,
                        TwoStepVerificationEnrolled = sec.Enrolled2Sv,
                        TwoStepVerificationEnforced = sec.Enforced2Sv,
                        Suspended = sec.Suspended,
                        IsAdmin = sec.IsAdmin,
                        AdvancedProtection = sec.AdvancedProtection,
                        AdvancedProtectionAsOf = sec.AdvancedProtectionAsOf,
                        Error = sec.Error
                    };
                }
                else
                {
                    var cid = await CheckDomainAsync(domain);

                    // Resold customer tenant: retry the Admin SDK lookup scoped to its customer id. Works only
                    // for customers who granted us reseller Admin-console access; otherwise Found=false.
                    WorkspaceUserSecurity? resold = null;
                    if (cid is { Exists: true } && workspace.IsConfigured)
                    {
                        var customerId = await ResolveCustomerIdAsync(domain, cid);
                        if (customerId is not null)
                        {
                            try
                            {
                                resold = await workspace.GetUserSecurityAsync(email, customerId, cancellationToken);
                            }
                            catch (Exception ex)
                            {
                                logger.LogDebug(ex, "Resold-customer security lookup failed for {Email} in {CustomerId}", email, customerId);
                            }
                        }
                    }

                    info = (cid, resold) switch
                    {
                        (_, { Found: true }) => info with
                        {
                            AccountKind = GoogleAccountKind.Managed,
                            AccountKindReason = $"{domain} is a resold Workspace customer ({resold.CustomerId}) we have reseller admin access to.",
                            WorkspaceCustomerId = resold.CustomerId,
                            TwoStepVerificationEnrolled = resold.Enrolled2Sv,
                            TwoStepVerificationEnforced = resold.Enforced2Sv,
                            Suspended = resold.Suspended,
                            IsAdmin = resold.IsAdmin,
                            AdvancedProtection = resold.AdvancedProtection,
                            AdvancedProtectionAsOf = resold.AdvancedProtectionAsOf,
                            Error = resold.Error
                        },
                        ({ Exists: true }, _) => info with
                        {
                            AccountKind = GoogleAccountKind.Managed,
                            AccountKindReason = $"{domain} has a domain-verified Cloud Identity / Workspace tenant we have no reseller admin access to — 2SV not visible."
                        },
                        ({ HasNonDomainAccounts: true }, _) => info with
                        {
                            AccountKind = GoogleAccountKind.ManagedTeam,
                            AccountKindReason = $"{domain} only has a Cloud Identity team (email-verified) account."
                        },
                        (not null, _) => info with
                        {
                            AccountKind = GoogleAccountKind.Consumer,
                            AccountKindReason = $"No Cloud Identity / Workspace tenant exists for {domain} — unmanaged Google Account."
                        },
                        (null, _) => info with
                        {
                            AccountKind = GoogleAccountKind.Unknown,
                            AccountKindReason = "Cloud Identity check for the domain was unavailable."
                        }
                    };
                }
            }

            await cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(info), ttl, cancellationToken);
            members.Add(info);
        }

        return Results.Ok(new MemberIdentityResult
        {
            Members = members,
            DirectoryAvailable = workspace.IsConfigured && directoryError is null,
            DirectoryError = workspace.IsConfigured
                ? directoryError
                : "Admin SDK lookups need GoogleChannel:ServiceAccountKeyJson and GoogleChannel:ImpersonateUser."
        });
    }

    // Friendly reseller name per link id (primary domain, else reseller cloud id, else the raw link id),
    // resolved from the §10 read-model links table. Nulls in the input (direct customers) are ignored.
    private static async Task<Dictionary<string, string>> ResolveResellerNamesAsync(
        GChannelDbContext db, IEnumerable<string?> linkIds, CancellationToken ct)
    {
        var ids = linkIds.Where(id => id != null).Select(id => id!).Distinct().ToList();
        if (ids.Count == 0) { return []; }
        return await db.ResellerLinks.AsNoTracking()
            .Where(l => ids.Contains(l.LinkId))
            .ToDictionaryAsync(l => l.LinkId, l => l.PrimaryDomain ?? l.ResellerCloudId ?? l.LinkId, ct);
    }

    // Flags a day whose cost exceeds mean + k·stddev over the preceding rolling window (a pure,
    // testable heuristic — no ML). The window uses only PRIOR days so a spike doesn't mask itself.
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
