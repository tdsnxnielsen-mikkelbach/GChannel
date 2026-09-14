using Microsoft.EntityFrameworkCore;

namespace GChannel.ApiService.Data;

/// <summary>
/// Application database. Stores audit history of Channel API operations plus the §10 persistent
/// read-model (resellers, customers and sync bookkeeping) that the dashboard/estate views read from
/// instead of a live Channel API fan-out per request.
/// </summary>
public sealed class GChannelDbContext(DbContextOptions<GChannelDbContext> options) : DbContext(options)
{
    public DbSet<IdentityCheckLog> IdentityCheckLogs => Set<IdentityCheckLog>();

    // §10 read-model.
    public DbSet<ResellerLinkRecord> ResellerLinks => Set<ResellerLinkRecord>();
    public DbSet<CustomerRecord> CustomerRecords => Set<CustomerRecord>();
    public DbSet<EntitlementRecord> EntitlementRecords => Set<EntitlementRecord>();
    public DbSet<SyncCursor> SyncCursors => Set<SyncCursor>();

    // §15 actual-spend read-model (BigQuery detailed billing export).
    public DbSet<CustomerSpendRecord> CustomerSpend => Set<CustomerSpendRecord>();
    public DbSet<SpendDailyRecord> SpendDaily => Set<SpendDailyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IdentityCheckLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Domain).HasMaxLength(255).IsRequired();
            entity.Property(e => e.PerformedBy).HasMaxLength(320);
            entity.HasIndex(e => e.Domain);
        });

        modelBuilder.Entity<ResellerLinkRecord>(entity =>
        {
            entity.HasKey(e => e.LinkId);
            entity.Property(e => e.LinkId).HasMaxLength(128);
            entity.Property(e => e.ResellerCloudId).HasMaxLength(128);
            entity.Property(e => e.PrimaryDomain).HasMaxLength(255);
            entity.Property(e => e.LinkState).HasMaxLength(32);
            entity.Property(e => e.SyncError).HasMaxLength(512);
            entity.HasIndex(e => e.LastSyncedUtc);
            entity.HasIndex(e => e.LinkState);
        });

        modelBuilder.Entity<CustomerRecord>(entity =>
        {
            entity.HasKey(e => e.CustomerId);
            entity.Property(e => e.CustomerId).HasMaxLength(128);
            entity.Property(e => e.OrgName).HasMaxLength(512);
            entity.Property(e => e.Domain).HasMaxLength(255);
            entity.Property(e => e.CloudIdentityId).HasMaxLength(128);
            entity.Property(e => e.CloudIdentityDomain).HasMaxLength(255);
            entity.Property(e => e.OwningLinkId).HasMaxLength(128);
            entity.HasIndex(e => e.OwningLinkId);
            entity.HasIndex(e => e.LastSyncedUtc);
            entity.HasIndex(e => e.IsDeleted);
        });

        modelBuilder.Entity<EntitlementRecord>(entity =>
        {
            entity.HasKey(e => e.EntitlementId);
            entity.Property(e => e.EntitlementId).HasMaxLength(128);
            entity.Property(e => e.CustomerId).HasMaxLength(128);
            entity.Property(e => e.OwningLinkId).HasMaxLength(128);
            entity.Property(e => e.ProductId).HasMaxLength(128);
            entity.Property(e => e.ProductName).HasMaxLength(255);
            entity.Property(e => e.SkuId).HasMaxLength(128);
            entity.Property(e => e.SkuName).HasMaxLength(255);
            entity.Property(e => e.OfferId).HasMaxLength(128);
            entity.Property(e => e.OfferName).HasMaxLength(255);
            entity.Property(e => e.State).HasMaxLength(32);
            entity.Property(e => e.Currency).HasMaxLength(8);
            entity.Property(e => e.PlanDescription).HasMaxLength(128);
            entity.Property(e => e.UnitPrice).HasColumnType("decimal(18,6)");
            entity.Property(e => e.RepricingPercent).HasColumnType("decimal(9,4)");
            entity.Property(e => e.IonCostPerSeat).HasColumnType("decimal(18,6)");
            entity.Property(e => e.IonMarginPercent).HasColumnType("decimal(9,4)");
            entity.Property(e => e.IonCurrency).HasMaxLength(8);
            entity.HasIndex(e => e.CustomerId);
            entity.HasIndex(e => e.OwningLinkId);
            entity.HasIndex(e => e.ProductId);
            entity.HasIndex(e => e.IsDeleted);
        });

        modelBuilder.Entity<SyncCursor>(entity =>
        {
            entity.HasKey(e => e.Scope);
            entity.Property(e => e.Scope).HasMaxLength(64);
            entity.Property(e => e.Notes).HasMaxLength(512);
        });

        modelBuilder.Entity<CustomerSpendRecord>(entity =>
        {
            entity.HasKey(e => new { e.BillingSubAccountId, e.InvoiceMonth, e.Currency });
            entity.Property(e => e.BillingSubAccountId).HasMaxLength(64);
            entity.Property(e => e.InvoiceMonth).HasMaxLength(6);
            entity.Property(e => e.Currency).HasMaxLength(8);
            entity.Property(e => e.CustomerId).HasMaxLength(128);
            entity.Property(e => e.OwningLinkId).HasMaxLength(128);
            entity.Property(e => e.Cost).HasColumnType("decimal(18,6)");
            entity.Property(e => e.GrossCost).HasColumnType("decimal(18,6)");
            entity.Property(e => e.Credits).HasColumnType("decimal(18,6)");
            entity.HasIndex(e => e.CustomerId);
            entity.HasIndex(e => e.OwningLinkId);
            entity.HasIndex(e => e.InvoiceMonth);
        });

        modelBuilder.Entity<SpendDailyRecord>(entity =>
        {
            entity.HasKey(e => new { e.BillingSubAccountId, e.Day, e.Currency });
            entity.Property(e => e.BillingSubAccountId).HasMaxLength(64);
            entity.Property(e => e.Currency).HasMaxLength(8);
            entity.Property(e => e.CustomerId).HasMaxLength(128);
            entity.Property(e => e.OwningLinkId).HasMaxLength(128);
            entity.Property(e => e.Cost).HasColumnType("decimal(18,6)");
            entity.HasIndex(e => e.CustomerId);
            entity.HasIndex(e => e.Day);
        });
    }
}

/// <summary>Audit record written each time a Cloud Identity existence check runs.</summary>
public sealed class IdentityCheckLog
{
    public int Id { get; set; }
    public string Domain { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public int AccountsFound { get; set; }
    public string? PerformedBy { get; set; }
    public DateTimeOffset PerformedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>§10 read-model: one row per channel partner link, refreshed incrementally by staleness.</summary>
public sealed class ResellerLinkRecord
{
    /// <summary>Short link id (last segment of the link resource name).</summary>
    public string LinkId { get; set; } = string.Empty;
    public string? ResellerCloudId { get; set; }
    public string? PrimaryDomain { get; set; }
    public string LinkState { get; set; } = string.Empty;
    public int CustomerCount { get; set; }
    public DateTimeOffset? CreateTime { get; set; }
    public DateTimeOffset LastSyncedUtc { get; set; }
    public string? SyncError { get; set; }
}

/// <summary>§10 read-model: one row per customer (direct = null OwningLinkId, else owned by a reseller).</summary>
public sealed class CustomerRecord
{
    /// <summary>Short customer id (last segment of the customer resource name).</summary>
    public string CustomerId { get; set; } = string.Empty;
    public string? OrgName { get; set; }
    public string? Domain { get; set; }
    public string? CloudIdentityId { get; set; }
    /// <summary>Cloud Identity primary domain (may differ from <see cref="Domain"/>) — §15 spend-attribution fallback key.</summary>
    public string? CloudIdentityDomain { get; set; }
    /// <summary>Owning channel partner link id, or null for the account's direct customers.</summary>
    public string? OwningLinkId { get; set; }
    public DateTimeOffset? CreateTime { get; set; }
    public DateTimeOffset LastSyncedUtc { get; set; }
    /// <summary>Sum of active seats (num_units) across the customer's entitlements; denormalised for fast reseller ranking.</summary>
    public long SeatCount { get; set; }
    /// <summary>True when this customer IS one of our resellers buying for their own use (its Cloud Identity or domain matches a channel-partner link) — a DIRECT sale from us. False = a reseller's end customer (indirect).</summary>
    public bool IsResellerSelf { get; set; }
    /// <summary>Soft-delete flag set when a customer disappears from a fresh list pass.</summary>
    public bool IsDeleted { get; set; }
}

/// <summary>§10 read-model: one row per entitlement (product mix + seat totals come from these).</summary>
public sealed class EntitlementRecord
{
    /// <summary>Short entitlement id (last segment of the entitlement resource name).</summary>
    public string EntitlementId { get; set; } = string.Empty;
    public string CustomerId { get; set; } = string.Empty;
    /// <summary>Owning channel partner link id, or null for the account's direct customers (denormalised from the customer).</summary>
    public string? OwningLinkId { get; set; }
    /// <summary>Cloud Billing (sub-)account backing this entitlement — the §15 join key to the BigQuery cost export's <c>billing_account_id</c>. Null when the entitlement carries no billing account (e.g. non-GCP SKUs).</summary>
    public string? BillingAccount { get; set; }
    /// <summary>Provisioned-service id; for GCP entitlements this is the Cloud Billing sub-account (the precise §15 join key, since Channel leaves <see cref="BillingAccount"/> empty for GCP).</summary>
    public string? ProvisioningId { get; set; }
    public string? ProductId { get; set; }
    /// <summary>Friendly product display name denormalised at sync time so the dashboard product-mix can render without a live catalog call.</summary>
    public string? ProductName { get; set; }
    public string? SkuId { get; set; }
    /// <summary>Friendly SKU display name denormalised at sync time so the entitlement list renders without a live catalog call.</summary>
    public string? SkuName { get; set; }
    public string? OfferId { get; set; }
    /// <summary>Friendly offer display name denormalised at sync time so the entitlement list renders without a live catalog call.</summary>
    public string? OfferName { get; set; }
    public string State { get; set; } = string.Empty;
    /// <summary>Seat footprint (num_units, falling back to a flexible plan's max_units cap) — used for seat counts and reseller ranking, NOT for pricing.</summary>
    public long Seats { get; set; }
    /// <summary>Committed/billable seats (num_units only). Money rollups multiply by this so a flexible plan's max_units cap doesn't inflate the estate value. 0 for usage/flexible plans with no committed seats.</summary>
    public long BillableSeats { get; set; }
    public bool IsTrial { get; set; }
    /// <summary>Entitlement create time, denormalised so the read-model-backed entitlement list can show the "Created" column.</summary>
    public DateTimeOffset? CreateTime { get; set; }
    /// <summary>Commitment/renewal end time (<c>CommitmentSettings.EndTime</c>), denormalised so the customer list can show the next renewal date without a live call.</summary>
    public DateTimeOffset? CommitmentEndTime { get; set; }
    /// <summary>Whether auto-renewal is enabled for this entitlement (<c>CommitmentSettings.RenewalSettings.EnableRenewal</c>), or null when it has no renewal settings. Denormalised so the customer list can show auto-renew without a live call.</summary>
    public bool? RenewalEnabled { get; set; }
    /// <summary>Human-friendly plan summary (e.g. "Annual Plan (Monthly Payment)"), denormalised at sync time so subscription cards render without a live offer-plan call.</summary>
    public string? PlanDescription { get; set; }
    /// <summary>Wholesale effective per-seat price from the entitlement's offer, normalised to a PER-MONTH figure (a per-cycle offer price is divided by the cycle length, e.g. an annual offer ÷ 12). The reseller's cost from Google. 0 if unknown.</summary>
    public decimal UnitPrice { get; set; }
    /// <summary>ISO currency code for <see cref="UnitPrice"/> (e.g. "USD"), or null when no price was resolved.</summary>
    public string? Currency { get; set; }
    /// <summary>Repricing mark-up percent applied to this entitlement (§6): per-entitlement config, else owning-link channel-partner config, else 0.</summary>
    public decimal RepricingPercent { get; set; }
    /// <summary>Real per-seat cost from Ion (TD SYNNEX StreamOne) via the Ion Gateway, or null when unmatched/unconfigured. The actual wholesale cost vs the §11 list-price estimate.</summary>
    public decimal? IonCostPerSeat { get; set; }
    /// <summary>Ion gross margin percent on the transacted price ((price-cost)/price*100), or null when unmatched.</summary>
    public decimal? IonMarginPercent { get; set; }
    /// <summary>ISO currency for <see cref="IonCostPerSeat"/>, or null.</summary>
    public string? IonCurrency { get; set; }
    public DateTimeOffset LastSyncedUtc { get; set; }
    /// <summary>Denormalised from the owning customer: true when the customer is a reseller buying for its own use (direct sale) vs a reseller's end customer (indirect). Lets the dashboard split estate value by source without a join.</summary>
    public bool IsResellerSelf { get; set; }
    /// <summary>Soft-delete flag set when an entitlement disappears from a fresh list pass.</summary>
    public bool IsDeleted { get; set; }
}

/// <summary>§10 read-model: per-scope sync bookkeeping (e.g. "links", "customers").</summary>
public sealed class SyncCursor
{
    public string Scope { get; set; } = string.Empty;
    public DateTimeOffset? LastFullPassUtc { get; set; }
    public DateTimeOffset? LastCycleUtc { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// §15 read-model: actual billed spend per billing sub-account per invoice month (single currency),
/// materialised from the BigQuery detailed cost export. Resolved to a Channel customer + owning reseller
/// link via the entitlement billing sub-account; CustomerId/OwningLinkId are null when the sub-account
/// doesn't map to a known entitlement (kept so spend is never silently dropped).
/// </summary>
public sealed class CustomerSpendRecord
{
    /// <summary>Bare billing sub-account id, e.g. <c>010528-357BBF-249088</c> (the export's <c>billing_account_id</c>).</summary>
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

/// <summary>§15 read-model: actual billed spend per billing sub-account per day (single currency), for the trend chart + spike detection.</summary>
public sealed class SpendDailyRecord
{
    public string BillingSubAccountId { get; set; } = string.Empty;
    public DateOnly Day { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string? CustomerId { get; set; }
    public string? OwningLinkId { get; set; }
    /// <summary>Net cost after credits for the day.</summary>
    public decimal Cost { get; set; }
    public DateTimeOffset LastSyncedUtc { get; set; }
}