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
}
