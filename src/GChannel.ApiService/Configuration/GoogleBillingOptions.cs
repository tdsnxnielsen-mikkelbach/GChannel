namespace GChannel.ApiService.Configuration;

/// <summary>
/// Configuration for the Cloud Billing Budget integration (billingbudgets.googleapis.com). This is a
/// separate Google Cloud data plane from the Channel API: it authenticates with the reseller's
/// service-account key (reused from <see cref="GoogleChannelOptions.ServiceAccountKeyJson"/>) and
/// operates on Cloud Billing accounts, not Channel accounts.
/// </summary>
public sealed class GoogleBillingOptions
{
    public const string SectionName = "GoogleBilling";

    /// <summary>
    /// Optional comma-separated list of billing account ids (e.g. <c>016E74-10DE75-21330A,01F210-8698CB-C16E8D</c>)
    /// to fall back to when live discovery via the Cloud Billing API is unavailable. Leave empty to rely
    /// on discovery only.
    /// </summary>
    public string BillingAccountIds { get; set; } = string.Empty;

    /// <summary>Parsed <see cref="BillingAccountIds"/>.</summary>
    public IReadOnlyList<string> BillingAccountIdList =>
        BillingAccountIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Client-side pacing (requests/minute) for read calls to the Cloud Billing + Budget APIs
    /// (account/sub-account discovery and budget listing). Kept under the Cloud Billing API's
    /// "all requests per minute" quota (typically 400) and the Budget API read quota. <c>0</c> disables pacing.
    /// </summary>
    public int ReadRequestsPerMinute { get; set; } = 240;

    /// <summary>
    /// Client-side pacing (requests/minute) for write calls to the Budget API (create/update/delete).
    /// Kept under the Budget API's write quota (typically 100/min per user). <c>0</c> disables pacing.
    /// </summary>
    public int WriteRequestsPerMinute { get; set; } = 60;

    // §15 — BigQuery detailed billing export (actual spend). Separate data plane from the Budget API
    // above: the reseller service-account reads the detailed usage-cost export table the reseller
    // configured on their Cloud Billing account. Everything below is a no-op unless BigQueryEnabled is
    // true AND ProjectId/Dataset/Table are set (mirroring how the Pub/Sub subscriber stays disabled).

    /// <summary>Feature flag: when false the §15 billing-export sync is disabled and its endpoints report unconfigured.</summary>
    public bool BigQueryEnabled { get; set; }

    /// <summary>GCP project that hosts the billing-export dataset (jobs are billed here). E.g. <c>927553582005</c>.</summary>
    public string BigQueryProjectId { get; set; } = string.Empty;

    /// <summary>BigQuery dataset holding the export table. E.g. <c>billing_export</c>.</summary>
    public string BillingExportDataset { get; set; } = string.Empty;

    /// <summary>The detailed export table id, e.g. <c>gcp_billing_export_resource_v1_XXXXXX_XXXXXX_XXXXXX</c>.</summary>
    public string BillingExportTable { get; set; } = string.Empty;

    /// <summary>How often the worker re-runs the aggregated export queries. The export lags hours, so daily is the design point.</summary>
    public int SyncIntervalHours { get; set; } = 24;

    /// <summary>Trailing window (days) of daily spend to materialise for the trend chart + spike detection.</summary>
    public int TrendDays { get; set; } = 90;

    /// <summary>Safety cap on BigQuery bytes billed per query (default 2 GB); a query exceeding it fails instead of costing money.</summary>
    public long MaximumBytesBilled { get; set; } = 2_000_000_000;

    /// <summary>
    /// Comma-separated billing sub-account ids to exclude from customer attribution — typically the
    /// distributor's own project/billing account (e.g. the export host), whose spend is not a resold customer's.
    /// </summary>
    public string ExcludeBillingAccountIds { get; set; } = string.Empty;

    /// <summary>Parsed <see cref="ExcludeBillingAccountIds"/>.</summary>
    public IReadOnlyList<string> ExcludeBillingAccountIdList =>
        ExcludeBillingAccountIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Trailing window (days) used by spike detection's rolling mean/stddev.</summary>
    public int SpikeWindowDays { get; set; } = 30;

    /// <summary>Std-dev multiplier: a day whose cost exceeds mean + k·stddev over the window is flagged as a spike.</summary>
    public double SpikeStdDevK { get; set; } = 3.0;

    /// <summary>True when the BigQuery export sync has everything it needs to run.</summary>
    public bool BigQueryConfigured =>
        BigQueryEnabled
        && !string.IsNullOrWhiteSpace(BigQueryProjectId)
        && !string.IsNullOrWhiteSpace(BillingExportDataset)
        && !string.IsNullOrWhiteSpace(BillingExportTable);
}
