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
