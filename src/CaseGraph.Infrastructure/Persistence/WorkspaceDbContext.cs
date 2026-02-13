using CaseGraph.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CaseGraph.Infrastructure.Persistence;

public sealed class WorkspaceDbContext : DbContext
{
    public WorkspaceDbContext(DbContextOptions<WorkspaceDbContext> options)
        : base(options)
    {
    }

    public DbSet<CaseRecord> Cases => Set<CaseRecord>();

    public DbSet<EvidenceItemRecord> EvidenceItems => Set<EvidenceItemRecord>();

    public DbSet<AuditEventRecord> AuditEvents => Set<AuditEventRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CaseRecord>(entity =>
        {
            entity.ToTable("CaseRecord");
            entity.HasKey(e => e.CaseId);
            entity.Property(e => e.Name).IsRequired();
        });

        modelBuilder.Entity<EvidenceItemRecord>(entity =>
        {
            entity.ToTable("EvidenceItemRecord");
            entity.HasKey(e => e.EvidenceItemId);
            entity.Property(e => e.DisplayName).IsRequired();
            entity.Property(e => e.OriginalPath).IsRequired();
            entity.Property(e => e.OriginalFileName).IsRequired();
            entity.Property(e => e.Sha256Hex).IsRequired();
            entity.Property(e => e.FileExtension).IsRequired();
            entity.Property(e => e.SourceType).IsRequired();
            entity.Property(e => e.ManifestRelativePath).IsRequired();
            entity.Property(e => e.StoredRelativePath).IsRequired();

            entity.HasOne(e => e.Case)
                .WithMany(c => c.EvidenceItems)
                .HasForeignKey(e => e.CaseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditEventRecord>(entity =>
        {
            entity.ToTable("AuditEventRecord");
            entity.HasKey(e => e.AuditEventId);
            entity.Property(e => e.Operator).IsRequired();
            entity.Property(e => e.ActionType).IsRequired();
            entity.Property(e => e.Summary).IsRequired();
        });
    }
}
