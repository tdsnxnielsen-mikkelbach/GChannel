using GChannel.Core.Configuration;
using GChannel.Core.Estate.ReadModels;
using GChannel.Core.Messaging;
using GChannel.Core.Persistence.Documents;
using GChannel.Worker.Infrastructure;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.BigQuery.V2;
using Marten;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.RegularExpressions;
using Wolverine;
using Wolverine.Attributes;

namespace GChannel.Worker.Handlers;

/// <summary>
/// Billing-export sync (the <c>RunBillingSyncCycle</c> recurring tick). Runs aggregated BigQuery queries over
/// the reseller's Cloud Billing → BigQuery detailed cost export (monthly by sub-account, per seller/service,
/// and daily for the trend window), resolves each billing sub-account to a Channel customer + owning reseller
/// link via the entitlement billing account the read model stores, and upserts the results into the
/// CustomerSpend / CustomerSpendBySeller / SpendDaily documents. Queries are aggregated server-side and
/// capped with <see cref="GoogleBillingOptions.MaximumBytesBilled"/> to keep BigQuery cost low.
/// </summary>
public sealed class BillingSyncHandler(
    IOptions<GoogleChannelOptions> channelOptions,
    IOptions<GoogleBillingOptions> billingOptions,
    IDocumentStore store,
    RecurringJobCatalog catalog,
    ILogger<BillingSyncHandler> logger)
{
    private BigQueryClient? _bigQuery;

    // Several aggregated BigQuery queries over the export table plus the Marketplace order scan.
    [MessageTimeout(RecurringJobs.BillingSyncBudgetSeconds)]
    public async Task<OutgoingMessages> Handle(RunBillingSyncCycle tick, IDocumentSession session, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        await RecurringJobRunner.RunAsync(tick, catalog[RecurringJobs.BillingSync], session, outgoing, logger, RunCycleAsync, ct);
        return outgoing;
    }

    private static GoogleCredential BuildCredential(GoogleChannelOptions channel)
    {
        // BigQuery access uses the reseller service account's OWN IAM on the export project — no domain-wide
        // delegation/impersonation (that's only for the Channel API's user-scoped calls).
        var credential = string.IsNullOrWhiteSpace(channel.ServiceAccountKeyJson)
            ? CredentialFactory.FromFile(channel.ServiceAccountKeyPath, "service_account")
            : CredentialFactory.FromJson(channel.ServiceAccountKeyJson, "service_account");
        return credential.CreateScoped("https://www.googleapis.com/auth/bigquery");
    }

    private async Task<string?> RunCycleAsync(CancellationToken ct)
    {
        var opts = billingOptions.Value;
        var channel = channelOptions.Value;
        _bigQuery ??= await BigQueryClient.CreateAsync(opts.BigQueryProjectId, BuildCredential(channel));
        var bq = _bigQuery;

        var now = DateTimeOffset.UtcNow;
        var table = $"`{opts.BigQueryProjectId}.{opts.BillingExportDataset}.{opts.BillingExportTable}`";
        var queryOptions = new QueryOptions { MaximumBytesBilled = opts.MaximumBytesBilled };
        var excluded = opts.ExcludeBillingAccountIdList.Select(Normalize).ToHashSet(StringComparer.Ordinal);

        var accountMap = await BuildAccountMapAsync(ct);
        var minMonth = now.UtcDateTime.AddMonths(-13).ToString("yyyyMM", CultureInfo.InvariantCulture);
        var monthWindow = Enumerable.Range(0, 14).Select(i => now.UtcDateTime.AddMonths(-i).ToString("yyyyMM", CultureInfo.InvariantCulture)).ToArray();

        // Fallback correlation: GCP entitlements don't carry a billing sub-account; the export ties each sub-account
        // to a customer via the GCP organization's domain, which matches CustomerRecord.Domain.
        var (domainMap, sldMap) = await BuildDomainMapsAsync(ct);
        var subToDomain = new Dictionary<string, string>(StringComparer.Ordinal);
        var domainSql = $"""
            SELECT billing_account_id AS sub_account, ANY_VALUE(a.display_name) AS org_domain
            FROM {table}, UNNEST(project.ancestors) AS a
            WHERE a.resource_name LIKE 'organizations/%' AND invoice.month >= @minMonth
            GROUP BY sub_account
            """;
        foreach (var row in await bq.ExecuteQueryAsync(domainSql,
            [new BigQueryParameter("minMonth", BigQueryDbType.String, minMonth)], queryOptions, cancellationToken: ct))
        {
            var s = Normalize(row["sub_account"] as string);
            var d = (row["org_domain"] as string)?.Trim().ToLowerInvariant();
            if (s.Length > 0 && !string.IsNullOrEmpty(d)) { subToDomain[s] = d; }
        }
        foreach (var kv in opts.SubAccountDomainMapParsed) { subToDomain[Normalize(kv.Key)] = kv.Value; }

        // --- Monthly spend by sub-account (last 13 invoice months). ---
        var monthlySql = $"""
            SELECT
              billing_account_id AS sub_account,
              invoice.month AS invoice_month,
              currency,
              SUM(cost) AS gross_cost,
              SUM(IFNULL((SELECT SUM(c.amount) FROM UNNEST(credits) AS c), 0)) AS credits
            FROM {table}
            WHERE invoice.month >= @minMonth
            GROUP BY sub_account, invoice_month, currency
            """;
        var monthlyRows = await bq.ExecuteQueryAsync(monthlySql,
            [new BigQueryParameter("minMonth", BigQueryDbType.String, minMonth)], queryOptions, cancellationToken: ct);

        var monthlyUpserts = 0;
        var monthlyUnmapped = 0;
        await WithSessionAsync(async session =>
        {
            var stored = (await session.Query<CustomerSpendRecord>().Where(r => r.InvoiceMonth.IsOneOf(monthWindow)).ToListAsync(ct))
                .ToDictionary(r => r.Id, StringComparer.Ordinal);
            foreach (var row in monthlyRows)
            {
                var sub = Normalize(row["sub_account"] as string);
                if (sub.Length == 0 || excluded.Contains(sub)) { continue; }
                var month = row["invoice_month"] as string ?? "";
                var currency = row["currency"] as string ?? "";
                if (month.Length == 0 || currency.Length == 0) { continue; }
                var gross = Dec(row["gross_cost"]);
                var credits = Dec(row["credits"]);
                var owner = Resolve(sub, accountMap, subToDomain, domainMap, sldMap);
                if (owner.CustomerId is null) { monthlyUnmapped++; }

                var id = CustomerSpendRecord.MakeId(sub, month, currency);
                if (!stored.TryGetValue(id, out var rec))
                {
                    rec = new CustomerSpendRecord { Id = id, BillingSubAccountId = sub, InvoiceMonth = month, Currency = currency };
                    stored[id] = rec;
                }
                rec.CustomerId = owner.CustomerId;
                rec.OwningLinkId = owner.OwningLinkId;
                rec.GrossCost = gross;
                rec.Credits = credits;
                rec.Cost = gross + credits;
                rec.LastSyncedUtc = now;
                session.Store(rec);
                monthlyUpserts++;
            }
        }, ct);

        // --- Native Google vs third-party split: monthly spend by seller + service. Rows for the window are replaced wholesale. ---
        var sellerSql = $"""
            SELECT
              billing_account_id AS sub_account,
              invoice.month AS invoice_month,
              currency,
              IFNULL(transaction_type, '') AS transaction_type,
              IFNULL(seller_name, '') AS seller_name,
              IFNULL(service.description, '') AS service_description,
              SUM(cost) AS gross_cost,
              SUM(IFNULL((SELECT SUM(c.amount) FROM UNNEST(credits) AS c), 0)) AS credits
            FROM {table}
            WHERE invoice.month >= @minMonth
            GROUP BY sub_account, invoice_month, currency, transaction_type, seller_name, service_description
            """;
        var sellerRows = await bq.ExecuteQueryAsync(sellerSql,
            [new BigQueryParameter("minMonth", BigQueryDbType.String, minMonth)], queryOptions, cancellationToken: ct);

        var sellerUpserts = 0;
        await WithSessionAsync(session =>
        {
            session.DeleteWhere<CustomerSpendBySellerRecord>(r => r.InvoiceMonth.IsOneOf(monthWindow));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in sellerRows)
            {
                var sub = Normalize(row["sub_account"] as string);
                if (sub.Length == 0 || excluded.Contains(sub)) { continue; }
                var month = row["invoice_month"] as string ?? "";
                var currency = row["currency"] as string ?? "";
                if (month.Length == 0 || currency.Length == 0) { continue; }
                var txType = Truncate((row["transaction_type"] as string ?? "").Trim().ToUpperInvariant(), 32);
                var seller = (row["seller_name"] as string ?? "").Trim();
                var service = (row["service_description"] as string ?? "").Trim();
                var sellerKey = Truncate(seller.ToLowerInvariant(), 256);
                var serviceKey = Truncate(service.ToLowerInvariant(), 256);
                var id = CustomerSpendBySellerRecord.MakeId(sub, month, currency, txType, sellerKey, serviceKey);
                if (!seen.Add(id)) { continue; }

                var gross = Dec(row["gross_cost"]);
                var credits = Dec(row["credits"]);
                var owner = Resolve(sub, accountMap, subToDomain, domainMap, sldMap);
                session.Store(new CustomerSpendBySellerRecord
                {
                    Id = id,
                    BillingSubAccountId = sub,
                    InvoiceMonth = month,
                    Currency = currency,
                    TransactionType = txType,
                    SellerKey = sellerKey,
                    ServiceKey = serviceKey,
                    SellerName = seller.Length == 0 ? null : Truncate(seller, 256),
                    ServiceDescription = service.Length == 0 ? null : Truncate(service, 256),
                    CustomerId = owner.CustomerId,
                    OwningLinkId = owner.OwningLinkId,
                    GrossCost = gross,
                    Credits = credits,
                    Cost = gross + credits,
                    LastSyncedUtc = now
                });
                sellerUpserts++;
            }
            return Task.CompletedTask;
        }, ct);

        // --- Daily spend by sub-account (trailing trend window). Prune older rows first. ---
        var minDay = now.UtcDateTime.Date.AddDays(-Math.Max(1, opts.TrendDays));
        var minDayOnly = DateOnly.FromDateTime(minDay);
        var dailySql = $"""
            SELECT
              billing_account_id AS sub_account,
              FORMAT_DATE('%Y-%m-%d', DATE(usage_start_time)) AS day,
              currency,
              SUM(cost + IFNULL((SELECT SUM(c.amount) FROM UNNEST(credits) AS c), 0)) AS cost
            FROM {table}
            WHERE usage_start_time >= @minDay
            GROUP BY sub_account, day, currency
            """;
        var dailyRows = await bq.ExecuteQueryAsync(dailySql,
            [new BigQueryParameter("minDay", BigQueryDbType.Timestamp, minDay)], queryOptions, cancellationToken: ct);

        var dailyUpserts = 0;
        await WithSessionAsync(async session =>
        {
            session.DeleteWhere<SpendDailyRecord>(d => d.Day < minDayOnly);
            var stored = (await session.Query<SpendDailyRecord>().Where(d => d.Day >= minDayOnly).ToListAsync(ct))
                .ToDictionary(r => r.Id, StringComparer.Ordinal);
            foreach (var row in dailyRows)
            {
                var sub = Normalize(row["sub_account"] as string);
                if (sub.Length == 0 || excluded.Contains(sub)) { continue; }
                if (!DateOnly.TryParseExact(row["day"] as string, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) { continue; }
                var currency = row["currency"] as string ?? "";
                if (currency.Length == 0) { continue; }
                var owner = Resolve(sub, accountMap, subToDomain, domainMap, sldMap);

                var id = SpendDailyRecord.MakeId(sub, day, currency);
                if (!stored.TryGetValue(id, out var rec))
                {
                    rec = new SpendDailyRecord { Id = id, BillingSubAccountId = sub, Day = day, Currency = currency };
                    stored[id] = rec;
                }
                rec.CustomerId = owner.CustomerId;
                rec.OwningLinkId = owner.OwningLinkId;
                rec.Cost = Dec(row["cost"]);
                rec.LastSyncedUtc = now;
                session.Store(rec);
                dailyUpserts++;
            }
        }, ct);

        logger.LogInformation(
            "Billing-export cycle: {Monthly} monthly + {Daily} daily + {Seller} seller/service spend rows upserted ({Unmapped} monthly rows unattributed to a customer).",
            monthlyUpserts, dailyUpserts, sellerUpserts, monthlyUnmapped);
        return null;
    }

    // Resolves a sub-account to a customer: the entitlement billing-account map first (precise), else the export
    // org-domain -> CustomerRecord.Domain, else the unambiguous registrable-domain (SLD) match.
    private static (string? CustomerId, string? OwningLinkId) Resolve(
        string sub,
        Dictionary<string, (string? CustomerId, string? OwningLinkId)> accountMap,
        Dictionary<string, string> subToDomain,
        Dictionary<string, (string? CustomerId, string? OwningLinkId)> domainMap,
        Dictionary<string, (string? CustomerId, string? OwningLinkId)> sldMap)
    {
        if (accountMap.TryGetValue(sub, out var byBilling) && byBilling.CustomerId is not null) { return byBilling; }
        if (subToDomain.TryGetValue(sub, out var domain))
        {
            if (domainMap.TryGetValue(domain, out var byDomain)) { return byDomain; }
            if (sldMap.TryGetValue(Sld(domain), out var bySld)) { return bySld; }
        }
        return (null, null);
    }

    private async Task<(Dictionary<string, (string? CustomerId, string? OwningLinkId)> ByDomain,
                        Dictionary<string, (string? CustomerId, string? OwningLinkId)> BySld)> BuildDomainMapsAsync(CancellationToken ct)
    {
        var byDomain = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
        var sldOwner = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
        var sldCustomers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        await using var session = store.QuerySession();
        var rows = await session.Query<CustomerRecord>()
            .Where(c => !c.IsDeleted && (c.Domain != null || c.CloudIdentityDomain != null))
            .ToListAsync(ct);
        foreach (var r in rows)
        {
            foreach (var d in new[] { r.Domain, r.CloudIdentityDomain })
            {
                var key = d?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(key)) { continue; }
                byDomain.TryAdd(key, (r.CustomerId, r.OwningLinkId));
                var sld = Sld(key);
                if (sld.Length > 0)
                {
                    if (!sldCustomers.TryGetValue(sld, out var set)) { set = sldCustomers[sld] = new(StringComparer.OrdinalIgnoreCase); }
                    set.Add(r.CustomerId);
                    sldOwner[sld] = (r.CustomerId, r.OwningLinkId);
                }
            }
        }
        var bySld = sldOwner
            .Where(kv => sldCustomers.TryGetValue(kv.Key, out var s) && s.Count == 1)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        return (byDomain, bySld);
    }

    private static string Sld(string domain)
    {
        var parts = domain.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[^2] : (parts.Length == 1 ? parts[0] : string.Empty);
    }

    /// <summary>billing sub-account -> (customer, owning reseller link) from the entitlement read model.</summary>
    private async Task<Dictionary<string, (string? CustomerId, string? OwningLinkId)>> BuildAccountMapAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, (string?, string?)>(StringComparer.Ordinal);
        await using var session = store.QuerySession();
        var rows = await session.Query<EntitlementRecord>()
            .Where(e => !e.IsDeleted && (e.BillingAccount != null || e.ProvisioningId != null))
            .ToListAsync(ct);
        foreach (var r in rows)
        {
            // Channel leaves billingAccount empty for GCP entitlements; there the provisioned-service id IS the Cloud
            // Billing sub-account. Only accept candidates shaped like a sub-account.
            foreach (var candidate in new[] { r.BillingAccount, r.ProvisioningId })
            {
                var key = Normalize(candidate);
                if (key.Length == 0 || !BillingAccountPattern.IsMatch(key)) { continue; }
                map.TryAdd(key, (r.CustomerId, r.OwningLinkId));
            }
        }
        return map;
    }

    private static readonly Regex BillingAccountPattern =
        new("^[0-9A-Fa-f]{6}-[0-9A-Fa-f]{6}-[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    private async Task WithSessionAsync(Func<IDocumentSession, Task> work, CancellationToken ct)
    {
        await using var session = store.LightweightSession();
        await work(session);
        await session.SaveChangesAsync(ct);
    }

    // Billing account ids compare case-insensitively; take the last path segment and upper-case so both sides match.
    private static string Normalize(string? account)
    {
        if (string.IsNullOrWhiteSpace(account)) { return string.Empty; }
        var slash = account.LastIndexOf('/');
        var id = slash >= 0 ? account[(slash + 1)..] : account;
        return id.Trim().ToUpperInvariant();
    }

    private static decimal Dec(object? value) =>
        value is null ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
