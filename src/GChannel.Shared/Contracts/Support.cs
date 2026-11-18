namespace GChannel.Shared.Contracts;

/// <summary>
/// A Cloud Customer Care support case (Cloud Support API v2 <c>Case</c>), as mirrored by the app. Cases live
/// under the reseller's Google Cloud organization; the worker keeps the mirror current from the support event
/// subscription (Pub/Sub) plus a daily <c>cases.search</c> catch-up.
/// </summary>
public sealed record SupportCase
{
    /// <summary>Full resource name, e.g. <c>organizations/123/cases/456</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The case number (last segment of <see cref="Name"/>).</summary>
    public required string CaseId { get; init; }

    public string? DisplayName { get; init; }
    public string? Description { get; init; }

    /// <summary>NEW, IN_PROGRESS_GOOGLE_SUPPORT, ACTION_REQUIRED, SOLUTION_PROVIDED or CLOSED.</summary>
    public string? State { get; init; }

    /// <summary>P0 (critical) … P4 (low).</summary>
    public string? Priority { get; init; }

    public string? ClassificationId { get; init; }
    public string? ClassificationName { get; init; }
    public bool Escalated { get; init; }
    public bool TestCase { get; init; }
    public string? CreatorName { get; init; }
    public string? CreatorEmail { get; init; }
    public string? ContactEmail { get; init; }
    public IReadOnlyList<string> SubscriberEmailAddresses { get; init; } = [];
    public string? LanguageCode { get; init; }
    public string? TimeZone { get; init; }
    public DateTimeOffset? CreateTime { get; init; }
    public DateTimeOffset? UpdateTime { get; init; }

    /// <summary>True for every state except CLOSED.</summary>
    public bool IsOpen => !string.Equals(State, "CLOSED", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A comment on a support case.</summary>
public sealed record SupportComment
{
    public required string Name { get; init; }
    public string? Body { get; init; }
    public string? CreatorName { get; init; }
    public string? CreatorEmail { get; init; }

    /// <summary>True when the comment was written by Google Support (rather than by the organization).</summary>
    public bool FromGoogleSupport { get; init; }

    public DateTimeOffset? CreateTime { get; init; }
}

/// <summary>An attachment on a support case (metadata only — download it from the Google Cloud console).</summary>
public sealed record SupportAttachment
{
    public required string Name { get; init; }
    public string? Filename { get; init; }
    public string? MimeType { get; init; }
    public long? SizeBytes { get; init; }
    public string? CreatorName { get; init; }
    public DateTimeOffset? CreateTime { get; init; }
}

/// <summary>
/// One support event received from the Cloud Support event subscription. The Pub/Sub message carries only
/// attributes: the changed resource and the notification type.
/// </summary>
public sealed record SupportEvent
{
    /// <summary>Pub/Sub message id (also the feed document id, so redeliveries are idempotent).</summary>
    public required string MessageId { get; init; }

    /// <summary>CASE_CREATED, CASE_UPDATED, COMMENT_ADDED, ATTACHMENT_ADDED, ATTACHMENT_DELETED (more may appear).</summary>
    public string? NotificationType { get; init; }

    /// <summary>The changed resource: a case, or a comment / attachment under one.</summary>
    public string? ResourceName { get; init; }

    /// <summary>The case the resource belongs to (<c>organizations/…/cases/…</c>), when it could be derived.</summary>
    public string? CaseName { get; init; }

    public DateTimeOffset ReceivedAt { get; init; }

    /// <summary>Set by the worker after processing: why the case could not be refreshed (null on success).</summary>
    public string? Error { get; init; }
}

/// <summary>The organization's <c>SupportEventSubscription</c> (Customer Care → Pub/Sub publishing).</summary>
public sealed record SupportEventSubscriptionInfo
{
    public required string Name { get; init; }
    public string? PubSubTopic { get; init; }

    /// <summary>WORKING, FAILING or DELETED.</summary>
    public string? State { get; init; }

    /// <summary>PERMISSION_DENIED, TOPIC_NOT_FOUND or OTHER while <see cref="State"/> is FAILING.</summary>
    public string? FailureReason { get; init; }

    public DateTimeOffset? CreateTime { get; init; }
    public DateTimeOffset? UpdateTime { get; init; }
}

/// <summary>Support event subscription health for the overview page.</summary>
public sealed record SupportSubscriptionStatus
{
    /// <summary>False when <c>GoogleSupport:OrganizationId</c> or the service-account key is missing.</summary>
    public bool Configured { get; init; }

    /// <summary>The topic the app expects Customer Care to publish to (<c>projects/…/topics/…</c>), if configured.</summary>
    public string? ExpectedTopic { get; init; }

    public IReadOnlyList<SupportEventSubscriptionInfo> Subscriptions { get; init; } = [];

    /// <summary>Why the subscriptions could not be read (missing IAM, API disabled, …).</summary>
    public string? Error { get; init; }
}

/// <summary>The mirrored support cases plus the pipeline's state.</summary>
public sealed record SupportCasesResult
{
    public IReadOnlyList<SupportCase> Cases { get; init; } = [];

    /// <summary>The organization the cases belong to (<c>organizations/…</c>), when configured.</summary>
    public string? Organization { get; init; }

    /// <summary>False when the organization id or the service-account key is missing.</summary>
    public bool Configured { get; init; }

    /// <summary>True when the worker's Pub/Sub subscriber for support events is configured.</summary>
    public bool SubscriberEnabled { get; init; }

    /// <summary>When the last full <c>cases.search</c> catch-up finished.</summary>
    public DateTimeOffset? LastCatchUpUtc { get; init; }

    public DateTimeOffset? NextCatchUpUtc { get; init; }
    public bool CatchUpRunning { get; init; }

    /// <summary>Error of the last catch-up run, if any.</summary>
    public string? CatchUpError { get; init; }
}

/// <summary>One case with its comment thread and attachments.</summary>
public sealed record SupportCaseDetail
{
    public required SupportCase Case { get; init; }

    /// <summary>Comments, oldest first.</summary>
    public IReadOnlyList<SupportComment> Comments { get; init; } = [];

    public IReadOnlyList<SupportAttachment> Attachments { get; init; } = [];

    /// <summary>Support events received for this case, newest first.</summary>
    public IReadOnlyList<SupportEvent> Events { get; init; } = [];

    /// <summary>When the app last read the case from Google.</summary>
    public DateTimeOffset? LastSyncedUtc { get; init; }

    /// <summary>Set when a live refresh failed and the stored copy is shown.</summary>
    public string? RefreshError { get; init; }
}

/// <summary>Recent support events, newest first.</summary>
public sealed record SupportEventsResult
{
    public IReadOnlyList<SupportEvent> Events { get; init; } = [];
}

/// <summary>A case classification (the issue category a new case must name).</summary>
public sealed record SupportClassification
{
    public required string Id { get; init; }
    public string? DisplayName { get; init; }
}

public sealed record SupportClassificationsResult
{
    public IReadOnlyList<SupportClassification> Classifications { get; init; } = [];
}

/// <summary>Creates a support case under the configured organization.</summary>
public sealed record CreateSupportCaseRequest
{
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required string ClassificationId { get; init; }

    /// <summary>P0 … P4.</summary>
    public string Priority { get; init; } = "P3";

    /// <summary>Optional additional email addresses that receive case updates.</summary>
    public IReadOnlyList<string> SubscriberEmailAddresses { get; init; } = [];

    /// <summary>Marks the case as a test case — Google does not work on it.</summary>
    public bool TestCase { get; init; }
}

/// <summary>Adds a comment to a case.</summary>
public sealed record AddSupportCommentRequest
{
    public required string Body { get; init; }
}

/// <summary>Escalates a case (needs a paid support plan that allows escalations).</summary>
public sealed record EscalateSupportCaseRequest
{
    /// <summary>RESOLUTION_TIME, TECHNICAL_EXPERTISE or BUSINESS_IMPACT.</summary>
    public required string Reason { get; init; }

    public required string Justification { get; init; }
}
