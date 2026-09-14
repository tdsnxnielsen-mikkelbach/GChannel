using System.Globalization;
using System.Text.RegularExpressions;
using GChannel.ApiService.Configuration;
using GChannel.ApiService.Data;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.BigQuery.V2;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace GChannel.Worker.Services;

/// <summary>
/// §15 billing-export sync. Once per configured interval it runs two aggregated BigQuery queries over
/// the reseller's Cloud Billing → BigQuery detailed cost export (monthly by sub-account, and daily for
/// the trend window), resolves each billing sub-account to a Channel customer + owning reseller link via
/// the entitlement billing account the §10 read-model now stores, and upserts the results into the
/// CustomerSpend / SpendDaily read-model. The UI then reads durable, indexed BILLED figures without ever
/// touching BigQuery on the request path. A no-op unless <see cref="GoogleBillingOptions.BigQueryConfigured"/>
/// and a service-account credential are set. Queries are aggregated server-side and capped with
/// <see cref="GoogleBillingOptions.MaximumBytesBilled"/> to keep BigQuery cost low.
/// </summary>
public sealed class BillingSyncService(
    IOptions<GoogleChannelOptions> channelOptions,
    IOptions<GoogleBillingOptions> billingOptions,
    IServiceScopeFactory scopeFactory,
    IConnectionMultiplexer redis,
    ILogger<BillingSyncService> logger) : BackgroundService
{
    private const string SyncLockKey = "billing:sync:lock";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = billingOptions.Value;
        var channel = channelOptions.Value;
        if (!opts.BigQueryConfigured)
        {
            logger.LogInformation("Billing-export sync is disabled (GoogleBilling:BigQueryEnabled off or ProjectId/Dataset/Table unset).");
            return;
        }
        if (!channel.HasServiceAccountCredential)
        {
            logger.LogWarning("Billing-export sync is configured but no service-account credential is available; skipping.");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, opts.SyncIntervalHours));
        var db = redis.GetDatabase();
        BigQueryClient? bq = null;

        try { await db.KeyDeleteAsync(SyncLockKey); } catch (Exception ex) { logger.LogDebug(ex, "Could not clear billing-sync lock on startup."); }

        logger.LogInformation(
            "Billing-export sync enabled; cycle every {Interval}h against {Project}.{Dataset}.{Table}.",
            interval.TotalHours, opts.BigQueryProjectId, opts.BillingExportDataset, opts.BillingExportTable);

        do
        {
            try
            {
                if (!await db.StringSetAsync(SyncLockKey, Environment.MachineName, interval, when: When.NotExists))
                {
                    logger.LogDebug("Another replica already synced billing this interval; skipping.");
                    continue;
                }

                bq ??= await BigQueryClient.CreateAsync(opts.BigQueryProjectId, BuildCredential(channel));
                await RunCycleAsync(bq, opts, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Billing-export sync cycle failed; will retry next interval.");
            }
        }
        while (await WaitAsync(interval, stoppingToken));
    }

    private static GoogleCredential BuildCredential(GoogleChannelOptions channel)
    {
        // BigQuery access uses the reseller service account's OWN IAM on the export project — no
        // domain-wide delegation/impersonation (that's only for the Channel API's user-scoped calls).
        var credential = string.IsNullOrWhiteSpace(channel.ServiceAccountKeyJson)
            ? CredentialFactory.FromFile(channel.ServiceAccountKeyPath, "service_account")
            : CredentialFactory.FromJson(channel.ServiceAccountKeyJson, "service_account");
        return credential.CreateScoped("https://www.googleapis.com/auth/bigquery");
    }

    private async Task RunCycleAsync(BigQueryClient bq, GoogleBillingOptions opts, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var table = $"`{opts.BigQueryProjectId}.{opts.BillingExportDataset}.{opts.BillingExportTable}`";
        var queryOptions = new QueryOptions { MaximumBytesBilled = opts.MaximumBytesBilled };
        var excluded = opts.ExcludeBillingAccountIdList.Select(Normalize).ToHashSet(StringComparer.Ordinal);

        // Resolve billing sub-account -> (customer, owning reseller link) from the §10 read-model. A
        // sub-account can back several entitlements of one customer; keep the first mapping seen.
        var accountMap = await BuildAccountMapAsync(ct);

        // --- Monthly spend by sub-account (last 13 invoice months). ---
        var minMonth = now.UtcDateTime.AddMonths(-13).ToString("yyyyMM", CultureInfo.InvariantCulture);

        // Fallback correlation: GCP entitlements don't carry a billing sub-account, so the entitlement
        // join above finds nothing. The export instead ties each sub-account to a customer via the GCP
        // organization's domain (project.ancestors org display_name == the org's primary domain), which
        // matches our CustomerRecord.Domain. Build sub-account -> org-domain and domain -> customer maps.
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
        // Manual overrides win: pin sub-accounts the export can't auto-resolve (no org, or a TLD-mismatched
        // org domain) to a customer domain.
        foreach (var kv in opts.SubAccountDomainMapParsed) { subToDomain[Normalize(kv.Key)] = kv.Value; }

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
        int monthlyUnmapped = 0;
        await WithDbAsync(async dbc =>
        {
            var stored = await dbc.CustomerSpend.ToDictionaryAsync(
                r => (r.BillingSubAccountId, r.InvoiceMonth, r.Currency), ct);
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

                if (!stored.TryGetValue((sub, month, currency), out var rec))
                {
                    rec = new CustomerSpendRecord { BillingSubAccountId = sub, InvoiceMonth = month, Currency = currency };
                    dbc.CustomerSpend.Add(rec);
                    stored[(sub, month, currency)] = rec;
                }
                rec.CustomerId = owner.CustomerId;
                rec.OwningLinkId = owner.OwningLinkId;
                rec.GrossCost = gross;
                rec.Credits = credits;
                rec.Cost = gross + credits;
                rec.LastSyncedUtc = now;
                monthlyUpserts++;
            }
            await dbc.SaveChangesAsync(ct);
        });

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
        await WithDbAsync(async dbc =>
        {
            await dbc.SpendDaily.Where(d => d.Day < minDayOnly).ExecuteDeleteAsync(ct);
            var stored = await dbc.SpendDaily
                .Where(d => d.Day >= minDayOnly)
                .ToDictionaryAsync(r => (r.BillingSubAccountId, r.Day, r.Currency), ct);
            foreach (var row in dailyRows)
            {
                var sub = Normalize(row["sub_account"] as string);
                if (sub.Length == 0 || excluded.Contains(sub)) { continue; }
                if (!DateOnly.TryParseExact(row["day"] as string, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) { continue; }
                var currency = row["currency"] as string ?? "";
                if (currency.Length == 0) { continue; }
                var owner = Resolve(sub, accountMap, subToDomain, domainMap, sldMap);

                if (!stored.TryGetValue((sub, day, currency), out var rec))
                {
                    rec = new SpendDailyRecord { BillingSubAccountId = sub, Day = day, Currency = currency };
                    dbc.SpendDaily.Add(rec);
                    stored[(sub, day, currency)] = rec;
                }
                rec.CustomerId = owner.CustomerId;
                rec.OwningLinkId = owner.OwningLinkId;
                rec.Cost = Dec(row["cost"]);
                rec.LastSyncedUtc = now;
                dailyUpserts++;
            }
            await dbc.SaveChangesAsync(ct);
        });

        logger.LogInformation(
            "Billing-export cycle: {Monthly} monthly + {Daily} daily spend rows upserted ({Unmapped} monthly rows unattributed to a customer).",
            monthlyUpserts, dailyUpserts, monthlyUnmapped);
    }

    // Resolves a sub-account to a customer: the entitlement billing-account map first (precise), else the
    // export org-domain -> CustomerRecord.Domain, else the unambiguous registrable-domain (SLD) match
    // (handles TLD mismatches, e.g. formuepleje.dev -> formuepleje.dk).
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

    /// <summary>Builds customer-domain and unambiguous registrable-domain (SLD) maps from the read-model.</summary>
    private async Task<(Dictionary<string, (string? CustomerId, string? OwningLinkId)> ByDomain,
                        Dictionary<string, (string? CustomerId, string? OwningLinkId)> BySld)> BuildDomainMapsAsync(CancellationToken ct)
    {
        var byDomain = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
        var sldOwner = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
        var sldCustomers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        await WithDbAsync(async dbc =>
        {
            var rows = await dbc.CustomerRecords.AsNoTracking()
                .Where(c => !c.IsDeleted && (c.Domain != null || c.CloudIdentityDomain != null))
                .Select(c => new { c.Domain, c.CloudIdentityDomain, c.CustomerId, c.OwningLinkId })
                .ToListAsync(ct);
            foreach (var r in rows)
            {
                // Map BOTH the customer domain and its Cloud Identity primary domain (they can differ).
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
        });
        // Keep only SLDs owned by exactly one customer (avoids mis-attributing e.g. acme.dk vs acme.com).
        var bySld = sldOwner
            .Where(kv => sldCustomers.TryGetValue(kv.Key, out var s) && s.Count == 1)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        return (byDomain, bySld);
    }

    // Registrable (second-level) label of a domain: formuepleje.dev -> "formuepleje". Sufficient for this
    // book's TLDs; ambiguous SLDs are excluded from matching above.
    private static string Sld(string domain)
    {
        var parts = domain.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[^2] : (parts.Length == 1 ? parts[0] : string.Empty);
    }

    /// <summary>Builds a billing sub-account -> (customer, owning reseller link) map from the entitlement read-model.</summary>
    private async Task<Dictionary<string, (string? CustomerId, string? OwningLinkId)>> BuildAccountMapAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, (string?, string?)>(StringComparer.Ordinal);
        await WithDbAsync(async dbc =>
        {
            var rows = await dbc.EntitlementRecords.AsNoTracking()
                .Where(e => !e.IsDeleted && (
                    (e.BillingAccount != null && e.BillingAccount != "") ||
                    (e.ProvisioningId != null && e.ProvisioningId != "")))
                .Select(e => new { e.BillingAccount, e.ProvisioningId, e.CustomerId, e.OwningLinkId })
                .ToListAsync(ct);
            foreach (var r in rows)
            {
                // Channel leaves billingAccount empty for GCP entitlements; there the provisioned-service id
                // IS the Cloud Billing sub-account, so it's the precise join key to the export. Only accept
                // candidates shaped like a sub-account (guards against non-GCP provisioning ids).
                foreach (var candidate in new[] { r.BillingAccount, r.ProvisioningId })
                {
                    var key = Normalize(candidate);
                    if (key.Length == 0 || !BillingAccountPattern.IsMatch(key)) { continue; }
                    map.TryAdd(key, (r.CustomerId, r.OwningLinkId));
                }
            }
        });
        return map;
    }

    private static readonly Regex BillingAccountPattern =
        new("^[0-9A-Fa-f]{6}-[0-9A-Fa-f]{6}-[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    private async Task WithDbAsync(Func<GChannelDbContext, Task> work)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GChannelDbContext>();
        await work(db);
    }

    private async Task<bool> WaitAsync(TimeSpan interval, CancellationToken ct)
    {
        try { await Task.Delay(interval, ct); return !ct.IsCancellationRequested; }
        catch (OperationCanceledException) { return false; }
    }

    // Billing account ids compare case-insensitively; the export is bare (010528-357BBF-249088) while an
    // entitlement's billingAccount may be a resource name (billingAccounts/010528-...). Take the last
    // path segment and upper-case so both sides match.
    private static string Normalize(string? account)
    {
        if (string.IsNullOrWhiteSpace(account)) { return string.Empty; }
        var slash = account.LastIndexOf('/');
        var id = slash >= 0 ? account[(slash + 1)..] : account;
        return id.Trim().ToUpperInvariant();
    }

    private static decimal Dec(object? value) =>
        value is null ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
}
