namespace GChannel.ApiService.Configuration;

/// <summary>Strongly-typed configuration for the Google Cloud Channel integration.</summary>
public sealed class GoogleChannelOptions
{
    public const string SectionName = "GoogleChannel";

    /// <summary>
    /// The reseller account resource name, e.g. "accounts/C01a2b3c".
    /// Found in the Partner Sales Console. Required for all Channel API calls.
    /// </summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>Application name reported to the Google API (telemetry/quota attribution).</summary>
    public string ApplicationName { get; set; } = "GChannel";

    /// <summary>Cache time-to-live (seconds) for idempotent lookups such as identity checks.</summary>
    public int CacheSeconds { get; set; } = 300;

    /// <summary>
    /// Maximum automatic retries for throttled (HTTP 429) or transient (HTTP 503) Channel API
    /// calls. Retries use exponential back-off; set to 0 to disable client-side retrying.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Maximum number of per-customer <c>entitlements.list</c> calls the dashboard aggregation runs
    /// concurrently. The Channel API enforces a per-minute request quota, so a high value bursts past
    /// it and triggers HTTP 429s; lower this if the dashboard reports throttled customers. Minimum 1.
    /// </summary>
    public int DashboardMaxConcurrency { get; set; } = 6;

    /// <summary>OAuth scope required to call the Channel reseller (order) APIs.</summary>
    public const string ChannelScope = "https://www.googleapis.com/auth/apps.order";

    /// <summary>
    /// Raw JSON of a Google service-account key, used by the background dashboard refresher when
    /// no per-request user token is available. Optional; if blank, <see cref="ServiceAccountKeyPath"/>
    /// is tried, otherwise the background refresh stays disabled. Treat as a secret (Key Vault).
    /// </summary>
    public string ServiceAccountKeyJson { get; set; } = string.Empty;

    /// <summary>
    /// Path to a Google service-account key file. Alternative to <see cref="ServiceAccountKeyJson"/>.
    /// </summary>
    public string ServiceAccountKeyPath { get; set; } = string.Empty;

    /// <summary>
    /// Reseller admin user the service account impersonates via domain-wide delegation (the Channel
    /// API has no service-account identity of its own). Required for the background refresh.
    /// </summary>
    public string ImpersonateUser { get; set; } = string.Empty;

    /// <summary>
    /// How often (seconds) a background worker recomputes the dashboard summary with the service
    /// account and warms the Redis cache. 0 (default) disables the background refresh so the
    /// dashboard is only computed on demand from the signed-in user's token.
    /// </summary>
    public int BackgroundRefreshSeconds { get; set; }

    /// <summary>True when a service-account credential is configured (JSON or file).</summary>
    public bool HasServiceAccountCredential =>
        !string.IsNullOrWhiteSpace(ServiceAccountKeyJson) || !string.IsNullOrWhiteSpace(ServiceAccountKeyPath);

    /// <summary>True when the periodic background dashboard refresh should run.</summary>
    public bool BackgroundRefreshEnabled =>
        BackgroundRefreshSeconds > 0 && HasServiceAccountCredential && !string.IsNullOrWhiteSpace(ImpersonateUser);

    /// <summary>Normalised account name guaranteed to start with "accounts/".</summary>
    public string AccountName =>
        string.IsNullOrWhiteSpace(AccountId)
            ? string.Empty
            : AccountId.StartsWith("accounts/", StringComparison.OrdinalIgnoreCase)
                ? AccountId
                : $"accounts/{AccountId}";
}
