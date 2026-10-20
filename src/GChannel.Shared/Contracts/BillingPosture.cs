namespace GChannel.Shared.Contracts;

// Billing IAM security posture — materialised nightly by the worker's BillingPostureSyncService from
// every reseller billing (sub-)account's IAM policy + the member identity/security resolver, and served
// by /api/billing/posture/*. See docs/billing-permissions.md (Part 3).

/// <summary>
/// Best-practice rules a billing-account member is held to. A member is <em>assessable</em> only when
/// its tenant is readable (our own, or a resold customer with reseller admin access); consumer accounts
/// and unreadable tenants can't be assessed and are reported separately rather than counted as failing.
/// </summary>
public static class PostureRules
{
    public static bool IsAssessable(GoogleAccountKind kind, bool securityVisible) =>
        kind == GoogleAccountKind.Managed && securityVisible;

    public static bool TwoSvOk(bool? enrolled) => enrolled == true;

    public static bool AdvancedProtectionOk(AdvancedProtectionStatus status) =>
        status == AdvancedProtectionStatus.Enrolled;

    public static bool KeysOk(int? securityKeys, int? passkeys) =>
        (securityKeys ?? 0) + (passkeys ?? 0) > 0;
}

/// <summary>One member row from the nightly scan: a (billing account, email, role, inherited) grant plus posture.</summary>
public sealed record BillingPostureMember
{
    public required string BillingAccountId { get; init; }
    public required string Email { get; init; }
    public required string MemberType { get; init; }
    public required string Role { get; init; }
    public required string RoleDisplayName { get; init; }
    public bool Inherited { get; init; }

    public GoogleAccountKind AccountKind { get; init; }
    public string? WorkspaceCustomerId { get; init; }
    /// <summary>True when 2SV / Advanced Protection / keys were readable for this member.</summary>
    public bool SecurityVisible { get; init; }

    public bool? TwoStepVerificationEnrolled { get; init; }
    public bool? TwoStepVerificationEnforced { get; init; }
    public AdvancedProtectionStatus AdvancedProtection { get; init; }
    public DateTimeOffset? AdvancedProtectionAsOf { get; init; }
    public int? SecurityKeys { get; init; }
    public int? Passkeys { get; init; }
    public DateOnly? KeysAsOf { get; init; }
    public bool? IsAdmin { get; init; }
    public bool? Suspended { get; init; }

    /// <summary>True when this is a billing admin role (<c>roles/billing.admin</c>).</summary>
    public bool IsBillingAdmin => string.Equals(Role, "roles/billing.admin", StringComparison.OrdinalIgnoreCase);

    public bool Assessable => PostureRules.IsAssessable(AccountKind, SecurityVisible);
    public bool TwoSvOk => PostureRules.TwoSvOk(TwoStepVerificationEnrolled);
    public bool AdvancedProtectionOk => PostureRules.AdvancedProtectionOk(AdvancedProtection);
    public bool KeysOk => PostureRules.KeysOk(SecurityKeys, Passkeys);

    /// <summary>Assessable and passes all three rules.</summary>
    public bool Compliant => Assessable && TwoSvOk && AdvancedProtectionOk && KeysOk;

    /// <summary>Assessable and fails at least one rule.</summary>
    public bool NonCompliant => Assessable && !Compliant;

    /// <summary>A consumer (unmanaged) Google Account holding Billing Account Administrator.</summary>
    public bool IsConsumerAdmin => AccountKind == GoogleAccountKind.Consumer && IsBillingAdmin;
}

/// <summary>Counts for one slice of members (direct or inherited).</summary>
public sealed record PostureBucket
{
    /// <summary>Distinct member grants in this slice.</summary>
    public int Members { get; init; }
    public int Assessable { get; init; }
    public int Compliant { get; init; }
    public int NonCompliant { get; init; }
    public int TwoSvOff { get; init; }
    public int NoAdvancedProtection { get; init; }
    public int NoKeys { get; init; }
    public int ConsumerAccounts { get; init; }
    public int ConsumerAdmins { get; init; }
    /// <summary>Managed but in a tenant we can't read — not assessable.</summary>
    public int Unassessable { get; init; }

    public double CompliancePercent => Assessable == 0 ? 0 : Math.Round(100.0 * Compliant / Assessable, 1);
}

/// <summary>Per billing (sub-)account roll-up from the nightly scan.</summary>
public sealed record BillingPostureAccount
{
    public required string BillingAccountId { get; init; }
    public string? DisplayName { get; init; }
    public string? MasterBillingAccountId { get; init; }
    public bool IsSubaccount => !string.IsNullOrEmpty(MasterBillingAccountId);

    /// <summary>Channel customer the sub-account maps to via the §15 spend read-model, when known.</summary>
    public string? CustomerId { get; init; }

    public PostureBucket Direct { get; init; } = new();
    public PostureBucket Inherited { get; init; } = new();

    /// <summary>True when a consumer Google Account holds Billing Account Administrator directly on this account.</summary>
    public bool HasDirectConsumerAdmin => Direct.ConsumerAdmins > 0;

    /// <summary>Why the IAM policy couldn't be read in the last scan, if it failed.</summary>
    public string? Error { get; init; }
    public DateTimeOffset LastSyncedUtc { get; init; }
}

/// <summary>Estate-wide KPIs + per-account table for the security posture page.</summary>
public sealed record BillingPostureSummary
{
    /// <summary>When the last scan completed, or null when it has never run.</summary>
    public DateTimeOffset? AsOf { get; init; }
    /// <summary>True when the service-account / impersonation prerequisites aren't configured.</summary>
    public bool Unconfigured { get; init; }
    /// <summary>Last scan error, if the most recent run failed.</summary>
    public string? LastRunError { get; init; }

    public int AccountsScanned { get; init; }
    public int AccountsWithErrors { get; init; }
    public int AccountsWithDirectConsumerAdmin { get; init; }
    public int AccountsWithNonCompliantDirect { get; init; }
    public int AccountsWithNonCompliantInherited { get; init; }

    /// <summary>Distinct people/groups across all accounts' direct grants.</summary>
    public PostureBucket Direct { get; init; } = new();
    /// <summary>Distinct people/groups across all accounts' inherited grants (master-policy members).</summary>
    public PostureBucket Inherited { get; init; } = new();

    public IReadOnlyList<BillingPostureAccount> Accounts { get; init; } = [];
}

/// <summary>Member rows for one billing account (drill-down from the posture page).</summary>
public sealed record BillingPostureMembersResult
{
    public required string BillingAccountId { get; init; }
    public IReadOnlyList<BillingPostureMember> Members { get; init; } = [];
    public DateTimeOffset? AsOf { get; init; }
}

/// <summary>A distinct member appearing on one or more billing accounts, for the action list.</summary>
public sealed record BillingPostureFinding
{
    public required BillingPostureMember Member { get; init; }
    /// <summary>Billing account ids this exact (email, role, inherited) grant appears on.</summary>
    public IReadOnlyList<string> BillingAccountIds { get; init; } = [];
}

/// <summary>Flat list of non-compliant / consumer-admin members across the estate.</summary>
public sealed record BillingPostureFindingsResult
{
    public IReadOnlyList<BillingPostureFinding> Findings { get; init; } = [];
    public DateTimeOffset? AsOf { get; init; }
}
