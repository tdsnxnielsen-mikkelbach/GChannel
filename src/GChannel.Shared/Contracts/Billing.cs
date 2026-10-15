namespace GChannel.Shared.Contracts;

// Cloud Billing Budget API contracts (billingbudgets.googleapis.com). Unlike the rest of the app these
// do NOT come from the Cloud Channel API — they read/write budgets on the reseller's Cloud Billing
// accounts (and per-customer sub-accounts) using a service-account credential. See
// docs/todos/15-cloud-billing-bigquery-export.md.

/// <summary>A Cloud Billing account or sub-account the reseller can attach budgets to.</summary>
public sealed record BillingAccountInfo
{
    /// <summary>Bare billing account id, e.g. <c>01F210-8698CB-C16E8D</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Friendly display name, e.g. "GCP DKK" or the resold customer's name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>True when the billing account is open (active).</summary>
    public bool Open { get; init; }

    /// <summary>Parent (master) billing account id when this is a sub-account, else null.</summary>
    public string? MasterBillingAccountId { get; init; }

    /// <summary>True when this is a sub-account of a reseller master account.</summary>
    public bool IsSubaccount => !string.IsNullOrEmpty(MasterBillingAccountId);
}

/// <summary>The reseller's billing accounts, discovered live (best-effort) or from configuration.</summary>
public sealed record BillingAccountsResult
{
    public IReadOnlyList<BillingAccountInfo> Accounts { get; init; } = [];

    /// <summary>True when live discovery via the Cloud Billing API succeeded.</summary>
    public bool DiscoveryAvailable { get; init; }

    /// <summary>Why live discovery was unavailable (e.g. the Cloud Billing API isn't enabled), when applicable.</summary>
    public string? DiscoveryError { get; init; }
}

/// <summary>A budget defined on a billing (sub-)account, with its threshold alert rules.</summary>
public sealed record BudgetInfo
{
    /// <summary>Full resource name, <c>billingAccounts/{id}/budgets/{budgetId}</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The budget id (last path segment of <see cref="Name"/>).</summary>
    public required string BudgetId { get; init; }

    /// <summary>The billing account id the budget belongs to.</summary>
    public required string BillingAccountId { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>Budgeted amount in <see cref="CurrencyCode"/>. Zero when the budget tracks the last period's spend.</summary>
    public decimal Amount { get; init; }

    /// <summary>True when the budget auto-tracks the previous period's spend instead of a fixed amount.</summary>
    public bool UsesLastPeriodAmount { get; init; }

    public string? CurrencyCode { get; init; }

    /// <summary>Alert threshold percentages (e.g. 0.5, 0.9, 1.0) that trigger notifications.</summary>
    public IReadOnlyList<double> ThresholdPercents { get; init; } = [];

    /// <summary>Reset cadence, e.g. <c>MONTH</c>, <c>QUARTER</c>, <c>YEAR</c> (calendar period), or null for a custom period.</summary>
    public string? CalendarPeriod { get; init; }
}

/// <summary>Budgets for one billing account.</summary>
public sealed record BudgetsResult
{
    public required string BillingAccountId { get; init; }
    public IReadOnlyList<BudgetInfo> Budgets { get; init; } = [];
}

/// <summary>Create (no <see cref="BudgetId"/>) or update (with <see cref="BudgetId"/>) a budget.</summary>
public sealed record SaveBudgetRequest
{
    public required string BillingAccountId { get; init; }

    /// <summary>Set to update an existing budget; leave null/empty to create.</summary>
    public string? BudgetId { get; init; }

    public required string DisplayName { get; init; }

    public decimal Amount { get; init; }

    public required string CurrencyCode { get; init; }

    /// <summary>Alert thresholds as fractions (0.5 = 50%). Defaults applied server-side when empty.</summary>
    public IReadOnlyList<double> ThresholdPercents { get; init; } = [];

    /// <summary>Reset cadence: <c>MONTH</c> (default), <c>QUARTER</c> or <c>YEAR</c>.</summary>
    public string? CalendarPeriod { get; init; }
}

/// <summary>A principal granted a role on a billing (sub-)account via its Cloud Billing IAM policy.</summary>
public sealed record BillingAccountMember
{
    /// <summary>The principal's identifier, e.g. an email for users/groups/service accounts.</summary>
    public required string Email { get; init; }

    /// <summary>IAM member type from the policy prefix: <c>user</c>, <c>group</c>, <c>serviceAccount</c>, <c>domain</c>, etc.</summary>
    public required string MemberType { get; init; }

    /// <summary>The granted role id, e.g. <c>roles/billing.admin</c>.</summary>
    public required string Role { get; init; }

    /// <summary>Friendly role name, e.g. "Billing Account Administrator", or the raw role id when unknown.</summary>
    public required string RoleDisplayName { get; init; }

    /// <summary>True when the grant comes from the master billing account (inherited), false when set on the sub-account itself.</summary>
    public bool Inherited { get; init; }
}

/// <summary>
/// The principals on a billing (sub-)account's Cloud Billing IAM policy, split into those set directly
/// on the account and those inherited from its master billing account.
/// </summary>
public sealed record BillingAccountMembersResult
{
    public required string BillingAccountId { get; init; }

    /// <summary>The master (parent) billing account id when this is a sub-account, else null.</summary>
    public string? MasterBillingAccountId { get; init; }

    /// <summary>Members granted directly on this account (editable here, not inherited).</summary>
    public IReadOnlyList<BillingAccountMember> Direct { get; init; } = [];

    /// <summary>Members inherited from the master billing account.</summary>
    public IReadOnlyList<BillingAccountMember> Inherited { get; init; } = [];

    /// <summary>True when the IAM policy could be read (service account has <c>billing.accounts.getIamPolicy</c>).</summary>
    public bool Available { get; init; }

    /// <summary>Why the policy couldn't be read, when applicable (e.g. missing permission).</summary>
    public string? Error { get; init; }
}

/// <summary>Classification of the Google Account behind an IAM member email.</summary>
public enum GoogleAccountKind
{
    /// <summary>Couldn't be determined (lookup failed or the member isn't a user).</summary>
    Unknown = 0,

    /// <summary>A managed account in a domain-verified Google Workspace / Cloud Identity tenant.</summary>
    Managed,

    /// <summary>A Cloud Identity "team" account — email-verified but not domain-verified, so only partially managed.</summary>
    ManagedTeam,

    /// <summary>A consumer (unmanaged) Google Account: gmail.com, or a custom email with no Cloud Identity tenant.</summary>
    Consumer,
}

/// <summary>Advanced Protection Program enrollment as inferred from the Workspace audit log.</summary>
public enum AdvancedProtectionStatus
{
    /// <summary>The audit log was readable but holds no enroll/unenroll event in the retention window.</summary>
    Unknown = 0,

    /// <summary>The most recent retained audit event is an Advanced Protection enrollment.</summary>
    Enrolled,

    /// <summary>The most recent retained audit event is an Advanced Protection unenrollment.</summary>
    NotEnrolled,

    /// <summary>The audit log could not be queried (permission / API error) — see <see cref="MemberIdentityInfo.AdvancedProtectionError"/>.</summary>
    Unavailable,
}

/// <summary>Request to classify and security-check the Google Accounts behind a set of member emails.</summary>
public sealed record MemberIdentityRequest
{
    public IReadOnlyList<string> Emails { get; init; } = [];

    /// <summary>When true, cached results are ignored and every email is re-resolved against Google.</summary>
    public bool Refresh { get; init; }
}

/// <summary>Identity + security posture of one IAM member's Google Account, assembled best-effort from several Google APIs.</summary>
public sealed record MemberIdentityInfo
{
    public required string Email { get; init; }

    public GoogleAccountKind AccountKind { get; init; }

    /// <summary>Human-readable basis for <see cref="AccountKind"/> (which signal decided it).</summary>
    public string? AccountKindReason { get; init; }

    /// <summary>True when the user was found in the reseller's own Workspace directory (Admin SDK).</summary>
    public bool InResellerDirectory { get; init; }

    /// <summary>
    /// The Workspace customer id (e.g. <c>C01aihlmh</c>) whose directory answered for this user, when it is a
    /// resold customer we have reseller Admin-console access to. Null for our own tenant or when not found.
    /// </summary>
    public string? WorkspaceCustomerId { get; init; }

    /// <summary>True when 2SV / Advanced Protection were readable (own tenant or a resold customer with reseller access).</summary>
    public bool SecurityVisible => InResellerDirectory || WorkspaceCustomerId is not null;

    /// <summary>2-Step Verification enrolled (Admin SDK Directory <c>isEnrolledIn2Sv</c>); null when not readable.</summary>
    public bool? TwoStepVerificationEnrolled { get; init; }

    /// <summary>2-Step Verification enforced by policy (Admin SDK Directory <c>isEnforcedIn2Sv</c>); null when not readable.</summary>
    public bool? TwoStepVerificationEnforced { get; init; }

    public bool? Suspended { get; init; }

    public bool? IsAdmin { get; init; }

    public AdvancedProtectionStatus AdvancedProtection { get; init; }

    /// <summary>Timestamp of the audit event that decided <see cref="AdvancedProtection"/>, when any.</summary>
    public DateTimeOffset? AdvancedProtectionAsOf { get; init; }

    /// <summary>Why the Advanced Protection audit query failed, when <see cref="AdvancedProtection"/> is <c>Unavailable</c>.</summary>
    public string? AdvancedProtectionError { get; init; }

    /// <summary>Hardware security keys enrolled (Reports usage <c>accounts:num_security_keys</c>); null when not readable.</summary>
    public int? SecurityKeys { get; init; }

    /// <summary>Passkeys enrolled (Reports usage <c>accounts:num_passkeys_enrolled</c>); null when not readable.</summary>
    public int? Passkeys { get; init; }

    /// <summary>Date the key/passkey counts were reported for (usage reports lag a few days).</summary>
    public DateOnly? KeysAsOf { get; init; }

    /// <summary>Per-member lookup error, when a signal couldn't be read.</summary>
    public string? Error { get; init; }
}

/// <summary>Identity + security results for a batch of member emails.</summary>
public sealed record MemberIdentityResult
{
    public IReadOnlyList<MemberIdentityInfo> Members { get; init; } = [];

    /// <summary>True when the Admin SDK (Directory/Reports) lookups were possible (service account + impersonation configured).</summary>
    public bool DirectoryAvailable { get; init; }

    /// <summary>Why the Admin SDK lookups were unavailable, when applicable.</summary>
    public string? DirectoryError { get; init; }
}
