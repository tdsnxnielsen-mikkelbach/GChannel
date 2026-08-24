using System.Globalization;
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
                accountMap.TryGetValue(sub, out var owner);
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
                accountMap.TryGetValue(sub, out var owner);

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
            "Billing-export cycle: {Monthly} monthly + {Daily} daily spend rows upserted ({Unmapped} monthly rows had no matching entitlement billing account).",
            monthlyUpserts, dailyUpserts, monthlyUnmapped);
    }

    /// <summary>Builds a billing sub-account -> (customer, owning reseller link) map from the entitlement read-model.</summary>
    private async Task<Dictionary<string, (string? CustomerId, string? OwningLinkId)>> BuildAccountMapAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, (string?, string?)>(StringComparer.Ordinal);
        await WithDbAsync(async dbc =>
        {
            var rows = await dbc.EntitlementRecords.AsNoTracking()
                .Where(e => !e.IsDeleted && e.BillingAccount != null && e.BillingAccount != "")
                .Select(e => new { e.BillingAccount, e.CustomerId, e.OwningLinkId })
                .ToListAsync(ct);
            foreach (var r in rows)
            {
                var key = Normalize(r.BillingAccount);
                if (key.Length == 0) { continue; }
                map.TryAdd(key, (r.CustomerId, r.OwningLinkId));
            }
        });
        return map;
    }

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
