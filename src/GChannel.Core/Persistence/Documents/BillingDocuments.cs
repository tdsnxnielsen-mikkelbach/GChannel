namespace GChannel.Core.Persistence.Documents;

/// <summary>
/// Actual billed spend per billing sub-account per invoice month (single currency), materialised from the
/// BigQuery detailed cost export. CustomerId/OwningLinkId are null when the sub-account doesn't map to a
/// known entitlement (kept so spend is never silently dropped).
/// </summary>
public sealed class CustomerSpendRecord
{
    public static string MakeId(string billingSubAccountId, string invoiceMonth, string currency)
        => $"{billingSubAccountId}|{invoiceMonth}|{currency}";

    public string Id { get; set; } = string.Empty;
    /// <summary>Bare billing sub-account id, e.g. <c>010528-357BBF-249088</c>.</summary>
    public string BillingSubAccountId { get; set; } = string.Empty;
    /// <summary>Invoice month as <c>YYYYMM</c>.</summary>
    public string InvoiceMonth { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public string? CustomerId { get; set; }
    public string? OwningLinkId { get; set; }
    /// <summary>Net cost after credits (GrossCost + Credits).</summary>
    public decimal Cost { get; set; }
    public decimal GrossCost { get; set; }
    /// <summary>Sum of credits (zero or negative).</summary>
    public decimal Credits { get; set; }
    public DateTimeOffset LastSyncedUtc { get; set; }
}

/// <summary>Actual billed spend per billing sub-account per day (single currency), for the trend chart + spike detection.</summary>
public sealed class SpendDailyRecord
{
    public static string MakeId(string billingSubAccountId, DateOnly day, string currency)
        => $"{billingSubAccountId}|{day:yyyy-MM-dd}|{currency}";

    public string Id { get; set; } = string.Empty;
    public string BillingSubAccountId { get; set; } = string.Empty;
    public DateOnly Day { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string? CustomerId { get; set; }
    public string? OwningLinkId { get; set; }
    /// <summary>Net cost after credits for the day.</summary>
    public decimal Cost { get; set; }
    public DateTimeOffset LastSyncedUtc { get; set; }
}

/// <summary>
/// Billed spend per (sub-account, month, currency, seller, service) from the export's transaction_type /
/// seller_name / service.description — the native-Google vs Marketplace/partner split.
/// </summary>
public sealed class CustomerSpendBySellerRecord
{
    public static string MakeId(string billingSubAccountId, string invoiceMonth, string currency, string transactionType, string sellerKey, string serviceKey)
        => $"{billingSubAccountId}|{invoiceMonth}|{currency}|{transactionType}|{sellerKey}|{serviceKey}";

    public string Id { get; set; } = string.Empty;
    public string BillingSubAccountId { get; set; } = string.Empty;
    public string InvoiceMonth { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    /// <summary>Export transaction_type: GOOGLE / THIRD_PARTY_RESELLER / THIRD_PARTY_AGENCY, or "" when absent.</summary>
    public string TransactionType { get; set; } = string.Empty;
    /// <summary>Key-safe seller: lower-cased seller_name, or "" for Google rows.</summary>
    public string SellerKey { get; set; } = string.Empty;
    /// <summary>Key-safe service: lower-cased service.description, or "".</summary>
    public string ServiceKey { get; set; } = string.Empty;
    public string? SellerName { get; set; }
    public string? ServiceDescription { get; set; }
    public string? CustomerId { get; set; }
    public string? OwningLinkId { get; set; }
    public decimal Cost { get; set; }
    public decimal GrossCost { get; set; }
    public decimal Credits { get; set; }
    public DateTimeOffset LastSyncedUtc { get; set; }
}

/// <summary>A Marketplace order on a billing account, from the Consumer Procurement API (nightly scan).</summary>
public sealed class MarketplaceOrderRecord
{
    /// <summary>billingAccounts/{id}/orders/{orderId} — also the document identity.</summary>
    public string Id { get; set; } = string.Empty;
    public string Name { get => Id; set => Id = value; }
    public string BillingAccountId { get; set; } = string.Empty;
    public string? CustomerId { get; set; }
    public string? DisplayName { get; set; }
    public string? Provider { get; set; }
    public string? ProductsCsv { get; set; }
    public string? OffersCsv { get; set; }
    public DateTimeOffset? CreateTime { get; set; }
    public DateTimeOffset? UpdateTime { get; set; }
    public bool Active { get; set; }
    public DateTimeOffset LastSyncedUtc { get; set; }
}

/// <summary>Billing IAM posture: one document per billing (sub-)account scanned by the nightly job.</summary>
public sealed class BillingPostureAccountRecord
{
    /// <summary>The billing account id — also the document identity.</summary>
    public string Id { get; set; } = string.Empty;
    public string BillingAccountId { get => Id; set => Id = value; }
    public string? DisplayName { get; set; }
    public string? MasterBillingAccountId { get; set; }
    public bool Open { get; set; }
    /// <summary>Channel customer id resolved via the spend mapping for this sub-account, when known.</summary>
    public string? CustomerId { get; set; }
    /// <summary>Set when the IAM policy couldn't be read in the last scan; members from the previous scan are kept.</summary>
    public string? Error { get; set; }
    public DateTimeOffset LastSyncedUtc { get; set; }
}

/// <summary>Billing IAM posture: one (account, email, role, inherited) grant with the member's security posture.</summary>
public sealed class BillingPostureMemberRecord
{
    public static string MakeId(string billingAccountId, string email, string role, bool inherited)
        => $"{billingAccountId}|{email.ToLowerInvariant()}|{role}|{(inherited ? 1 : 0)}";

    public string Id { get; set; } = string.Empty;
    public string BillingAccountId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool Inherited { get; set; }
    public string MemberType { get; set; } = string.Empty;
    public string RoleDisplayName { get; set; } = string.Empty;
    /// <summary><see cref="GChannel.Shared.Contracts.GoogleAccountKind"/> as int.</summary>
    public int AccountKind { get; set; }
    public string? WorkspaceCustomerId { get; set; }
    public bool SecurityVisible { get; set; }
    public bool? TwoSvEnrolled { get; set; }
    public bool? TwoSvEnforced { get; set; }
    /// <summary><see cref="GChannel.Shared.Contracts.AdvancedProtectionStatus"/> as int.</summary>
    public int AdvancedProtection { get; set; }
    public DateTimeOffset? AdvancedProtectionAsOf { get; set; }
    public int? SecurityKeys { get; set; }
    public int? Passkeys { get; set; }
    public DateOnly? KeysAsOf { get; set; }
    public bool? IsAdmin { get; set; }
    public bool? Suspended { get; set; }
    public DateTimeOffset LastSyncedUtc { get; set; }
}
