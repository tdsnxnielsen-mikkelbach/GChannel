using Microsoft.EntityFrameworkCore;

namespace GChannel.ApiService.Data;

/// <summary>Application database. Stores audit history of Channel API operations.</summary>
public sealed class GChannelDbContext(DbContextOptions<GChannelDbContext> options) : DbContext(options)
{
    public DbSet<IdentityCheckLog> IdentityCheckLogs => Set<IdentityCheckLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IdentityCheckLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Domain).HasMaxLength(255).IsRequired();
            entity.Property(e => e.PerformedBy).HasMaxLength(320);
            entity.HasIndex(e => e.Domain);
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
