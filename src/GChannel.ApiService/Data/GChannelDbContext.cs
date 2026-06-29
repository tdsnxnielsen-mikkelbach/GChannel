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
            entity.Property(e => e.SkuId).HasMaxLength(128);
            entity.Property(e => e.OfferId).HasMaxLength(128);
            entity.Property(e => e.State).HasMaxLength(32);
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
    /// <summary>Owning channel partner link id, or null for the account's direct customers.</summary>
    public string? OwningLinkId { get; set; }
    public DateTimeOffset? CreateTime { get; set; }
    public DateTimeOffset LastSyncedUtc { get; set; }
    /// <summary>Sum of active seats (num_units) across the customer's entitlements; denormalised for fast reseller ranking.</summary>
    public long SeatCount { get; set; }
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
    public string? ProductId { get; set; }
    public string? SkuId { get; set; }
    public string? OfferId { get; set; }
    public string State { get; set; } = string.Empty;
    public long Seats { get; set; }
    public bool IsTrial { get; set; }
    public DateTimeOffset LastSyncedUtc { get; set; }
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
