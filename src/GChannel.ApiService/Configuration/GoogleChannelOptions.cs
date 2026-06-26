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
    /// Upper bound (seconds) on how long a single throttled retry waits. The Channel API may send a
    /// <c>Retry-After</c> header on 429s (which we honour); this caps it so a large value can't stall
    /// the request beyond the dashboard's time budget.
    /// </summary>
    public int MaxRetryDelaySeconds { get; set; } = 60;

    /// <summary>
    /// Max concurrent per-customer <c>entitlements.list</c> calls when building the dashboard summary.
    /// The Channel API enforces a per-minute request quota, so a high value bursts past it and
    /// triggers HTTP 429s; lower this if the dashboard reports throttled customers. Minimum 1.
    /// </summary>
    public int DashboardMaxConcurrency { get; set; } = 6;

    /// <summary>
    /// Client-side pacing (requests per minute) for the dashboard's <c>entitlements.list</c> calls so
    /// the aggregation stays under the Channel API's "ListEntitlements requests per minute" quota and
    /// avoids 429s. Set to match (or just under) your project's quota; <c>0</c> disables pacing.
    /// </summary>
    public int DashboardRequestsPerMinute { get; set; } = 60;

    /// <summary>
    /// Time budget (seconds) for the on-demand dashboard's per-customer entitlement phase. Kept under
    /// the HTTP client's per-attempt timeout (60s) so the endpoint always responds in time with a
    /// (possibly partial) result. Roughly <c>DashboardBudgetSeconds × DashboardRequestsPerMinute / 60</c>
    /// customers are reachable per on-demand request; raise it (with headroom under 60s) to reach more,
    /// or enable the background refresh for a complete result. Minimum 5.
    /// </summary>
    public int DashboardBudgetSeconds { get; set; } = 45;

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
    /// Workload Identity Federation credential configuration (the <c>external_account</c> JSON produced
    /// by <c>gcloud iam workload-identity-pools create-cred-config</c>). When set, the Pub/Sub subscriber
    /// authenticates to Google using the host's own identity (e.g. the Azure managed identity) and short-
    /// lived federated tokens instead of a downloaded service-account key — Google's recommended approach.
    /// This is configuration, not a secret (it contains no private key). Takes precedence over
    /// <see cref="ServiceAccountKeyJson"/>/<see cref="ServiceAccountKeyPath"/> for Pub/Sub.
    /// <para>Note: this does <em>not</em> apply to the background dashboard refresh, which needs
    /// domain-wide delegation — a capability the .NET auth library only supports on downloaded
    /// service-account keys.</para>
    /// </summary>
    public string WorkloadIdentityCredentialJson { get; set; } = string.Empty;

    /// <summary>
    /// Path to a Workload Identity Federation credential configuration file. Alternative to
    /// <see cref="WorkloadIdentityCredentialJson"/>.
    /// </summary>
    public string WorkloadIdentityCredentialPath { get; set; } = string.Empty;

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

    /// <summary>
    /// The Google Cloud project id that hosts the Pub/Sub subscription for Channel notifications.
    /// This is your own project, where you create a subscription against the Google-owned topic
    /// returned by <c>accounts.register</c>. Required to run the notification subscriber.
    /// </summary>
    public string PubSubProjectId { get; set; } = string.Empty;

    /// <summary>
    /// The Pub/Sub subscription id (within <see cref="PubSubProjectId"/>) the background subscriber
    /// pulls Channel change events from. Blank disables the subscriber.
    /// </summary>
    public string PubSubSubscriptionId { get; set; } = string.Empty;

    /// <summary>Maximum number of recent notifications retained in the rolling Redis feed. Minimum 1.</summary>
    public int PubSubMaxNotifications { get; set; } = 200;

    /// <summary>True when a service-account credential is configured (JSON or file).</summary>
    public bool HasServiceAccountCredential =>
        !string.IsNullOrWhiteSpace(ServiceAccountKeyJson) || !string.IsNullOrWhiteSpace(ServiceAccountKeyPath);

    /// <summary>True when a Workload Identity Federation credential configuration is provided (JSON or file).</summary>
    public bool HasWorkloadIdentityCredential =>
        !string.IsNullOrWhiteSpace(WorkloadIdentityCredentialJson) || !string.IsNullOrWhiteSpace(WorkloadIdentityCredentialPath);

    /// <summary>True when the periodic background dashboard refresh should run.</summary>
    public bool BackgroundRefreshEnabled =>
        BackgroundRefreshSeconds > 0 && HasServiceAccountCredential && !string.IsNullOrWhiteSpace(ImpersonateUser);

    /// <summary>
    /// True when the background Pub/Sub notification subscriber should run: a project + subscription
    /// are configured and a credential is available to authenticate to Pub/Sub — either a Workload
    /// Identity Federation configuration (preferred) or a downloaded service-account key.
    /// </summary>
    public bool PubSubEnabled =>
        !string.IsNullOrWhiteSpace(PubSubProjectId)
        && !string.IsNullOrWhiteSpace(PubSubSubscriptionId)
        && (HasWorkloadIdentityCredential || HasServiceAccountCredential);

    /// <summary>Normalised account name guaranteed to start with "accounts/".</summary>
    public string AccountName =>
        string.IsNullOrWhiteSpace(AccountId)
            ? string.Empty
            : AccountId.StartsWith("accounts/", StringComparison.OrdinalIgnoreCase)
                ? AccountId
                : $"accounts/{AccountId}";
}
