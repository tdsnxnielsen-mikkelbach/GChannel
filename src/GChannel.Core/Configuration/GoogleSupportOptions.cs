namespace GChannel.Core.Configuration;

/// <summary>
/// Cloud Customer Care integration (cloudsupport.googleapis.com): the support cases of the reseller's Google Cloud
/// organization, kept current by a support event subscription that publishes to a Pub/Sub topic in our project
/// (Google's "Synchronize cases using Pub/Sub" connector). Authenticates with the reseller service-account key
/// reused from <c>GoogleChannel:ServiceAccountKeyJson</c> (no impersonation).
/// </summary>
public sealed class GoogleSupportOptions
{
    public const string SectionName = "GoogleSupport";

    /// <summary>Numeric Google Cloud organization id whose cases are mirrored (<c>organizations/{id}</c>). Blank disables the feature.</summary>
    public string OrganizationId { get; set; } = string.Empty;

    /// <summary>Project hosting the <see cref="PubSubTopicId"/> topic and subscription. Blank falls back to <c>GoogleChannel:PubSubProjectId</c>.</summary>
    public string PubSubProjectId { get; set; } = string.Empty;

    /// <summary>Topic Customer Care publishes to (shown on the page and used when resuming the event subscription).</summary>
    public string PubSubTopicId { get; set; } = "support-events";

    /// <summary>Pull subscription (on <see cref="PubSubTopicId"/>) the worker reads support events from. Blank disables the subscriber.</summary>
    public string PubSubSubscriptionId { get; set; } = string.Empty;

    /// <summary>Daily <c>cases.list</c> catch-up that refreshes cases whose events were missed. Needs <see cref="OrganizationId"/>.</summary>
    public bool CatchUpEnabled { get; set; } = true;

    /// <summary>UTC hour (0–23) the daily catch-up runs.</summary>
    public int CatchUpHourUtc { get; set; } = 4;

    /// <summary>Support events kept in the feed. Minimum 50.</summary>
    public int MaxEvents { get; set; } = 1000;

    /// <summary>The organization resource name (<c>organizations/{id}</c>), or empty when not configured.</summary>
    public string OrganizationName
    {
        get
        {
            var id = OrganizationId.Trim();
            if (id.Length == 0) { return string.Empty; }
            return id.StartsWith("organizations/", StringComparison.OrdinalIgnoreCase) ? id : $"organizations/{id}";
        }
    }

    /// <summary>The Pub/Sub project, falling back to the Channel notifications project.</summary>
    public string EffectivePubSubProjectId(GoogleChannelOptions channel) =>
        string.IsNullOrWhiteSpace(PubSubProjectId) ? channel.PubSubProjectId : PubSubProjectId;

    /// <summary>The full topic name Customer Care should publish to, or null when the project is unknown.</summary>
    public string? TopicName(GoogleChannelOptions channel)
    {
        var project = EffectivePubSubProjectId(channel);
        return string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(PubSubTopicId)
            ? null
            : $"projects/{project}/topics/{PubSubTopicId}";
    }

    /// <summary>True when the organization and the service-account key needed for the Cloud Support API are set.</summary>
    public bool IsConfigured(GoogleChannelOptions channel) =>
        !string.IsNullOrWhiteSpace(OrganizationId) && channel.HasServiceAccountCredential;

    /// <summary>True when the worker should pull support events from Pub/Sub.</summary>
    public bool SubscriberEnabled(GoogleChannelOptions channel) =>
        !string.IsNullOrWhiteSpace(EffectivePubSubProjectId(channel))
        && !string.IsNullOrWhiteSpace(PubSubSubscriptionId)
        && (channel.HasWorkloadIdentityCredential || channel.HasServiceAccountCredential);

    /// <summary>True when the daily catch-up should run.</summary>
    public bool CatchUpActive(GoogleChannelOptions channel) => CatchUpEnabled && IsConfigured(channel);
}
