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

    public DbSet<JobRecord> Jobs => Set<JobRecord>();

    public DbSet<MessageThreadRecord> MessageThreads => Set<MessageThreadRecord>();

    public DbSet<MessageEventRecord> MessageEvents => Set<MessageEventRecord>();

    public DbSet<MessageParticipantRecord> MessageParticipants => Set<MessageParticipantRecord>();

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

        modelBuilder.Entity<JobRecord>(entity =>
        {
            entity.ToTable("JobRecord");
            entity.HasKey(e => e.JobId);
            entity.Property(e => e.Status).IsRequired();
            entity.Property(e => e.JobType).IsRequired();
            entity.Property(e => e.StatusMessage).IsRequired();
            entity.Property(e => e.JsonPayload).IsRequired();
            entity.Property(e => e.CorrelationId).IsRequired();
            entity.Property(e => e.Operator).IsRequired();
        });

        modelBuilder.Entity<MessageThreadRecord>(entity =>
        {
            entity.ToTable("MessageThreadRecord");
            entity.HasKey(e => e.ThreadId);
            entity.Property(e => e.Platform).IsRequired();
            entity.Property(e => e.ThreadKey).IsRequired();
            entity.Property(e => e.SourceLocator).IsRequired();
            entity.Property(e => e.IngestModuleVersion).IsRequired();
            entity.HasIndex(e => new { e.CaseId, e.Platform });
            entity.HasIndex(e => e.EvidenceItemId);

            entity.HasOne<CaseRecord>()
                .WithMany()
                .HasForeignKey(e => e.CaseId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne<EvidenceItemRecord>()
                .WithMany()
                .HasForeignKey(e => e.EvidenceItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MessageEventRecord>(entity =>
        {
            entity.ToTable("MessageEventRecord");
            entity.HasKey(e => e.MessageEventId);
            entity.Property(e => e.Platform).IsRequired();
            entity.Property(e => e.Direction).IsRequired();
            entity.Property(e => e.SourceLocator).IsRequired();
            entity.Property(e => e.IngestModuleVersion).IsRequired();
            entity.HasIndex(e => new { e.EvidenceItemId, e.SourceLocator }).IsUnique();

            entity.HasOne(e => e.Thread)
                .WithMany(t => t.MessageEvents)
                .HasForeignKey(e => e.ThreadId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MessageParticipantRecord>(entity =>
        {
            entity.ToTable("MessageParticipantRecord");
            entity.HasKey(e => e.ParticipantId);
            entity.Property(e => e.Value).IsRequired();
            entity.Property(e => e.Kind).IsRequired();
            entity.Property(e => e.SourceLocator).IsRequired();
            entity.Property(e => e.IngestModuleVersion).IsRequired();

            entity.HasOne(e => e.Thread)
                .WithMany(t => t.Participants)
                .HasForeignKey(e => e.ThreadId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
