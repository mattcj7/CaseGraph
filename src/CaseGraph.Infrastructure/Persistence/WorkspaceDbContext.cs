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

    public DbSet<JobOrderKeyRecord> JobOrderKeys => Set<JobOrderKeyRecord>();

    public DbSet<CaseOrderKeyRecord> CaseOrderKeys => Set<CaseOrderKeyRecord>();

    public DbSet<EvidenceOrderKeyRecord> EvidenceOrderKeys => Set<EvidenceOrderKeyRecord>();

    public DbSet<AuditOrderKeyRecord> AuditOrderKeys => Set<AuditOrderKeyRecord>();

    public DbSet<MessageThreadRecord> MessageThreads => Set<MessageThreadRecord>();

    public DbSet<MessageEventRecord> MessageEvents => Set<MessageEventRecord>();

    public DbSet<MessageParticipantRecord> MessageParticipants => Set<MessageParticipantRecord>();

    public DbSet<TargetRecord> Targets => Set<TargetRecord>();

    public DbSet<TargetAliasRecord> TargetAliases => Set<TargetAliasRecord>();

    public DbSet<IdentifierRecord> Identifiers => Set<IdentifierRecord>();

    public DbSet<TargetIdentifierLinkRecord> TargetIdentifierLinks => Set<TargetIdentifierLinkRecord>();

    public DbSet<MessageParticipantLinkRecord> MessageParticipantLinks => Set<MessageParticipantLinkRecord>();

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

        modelBuilder.Entity<JobOrderKeyRecord>(entity =>
        {
            entity.HasNoKey();
            entity.ToSqlQuery(
                """
                SELECT
                    JobId,
                    CaseId,
                    EvidenceItemId,
                    JobType,
                    CAST(CreatedAtUtc AS TEXT) AS CreatedAtUtc,
                    CAST(StartedAtUtc AS TEXT) AS StartedAtUtc,
                    CAST(CompletedAtUtc AS TEXT) AS CompletedAtUtc
                FROM JobRecord
                """
            );
        });

        modelBuilder.Entity<CaseOrderKeyRecord>(entity =>
        {
            entity.HasNoKey();
            entity.ToSqlQuery(
                """
                SELECT
                    CaseId,
                    CAST(CreatedAtUtc AS TEXT) AS CreatedAtUtc,
                    CAST(LastOpenedAtUtc AS TEXT) AS LastOpenedAtUtc
                FROM CaseRecord
                """
            );
        });

        modelBuilder.Entity<EvidenceOrderKeyRecord>(entity =>
        {
            entity.HasNoKey();
            entity.ToSqlQuery(
                """
                SELECT
                    EvidenceItemId,
                    CaseId,
                    CAST(AddedAtUtc AS TEXT) AS AddedAtUtc
                FROM EvidenceItemRecord
                """
            );
        });

        modelBuilder.Entity<AuditOrderKeyRecord>(entity =>
        {
            entity.HasNoKey();
            entity.ToSqlQuery(
                """
                SELECT
                    AuditEventId,
                    CaseId,
                    CAST(TimestampUtc AS TEXT) AS TimestampUtc
                FROM AuditEventRecord
                """
            );
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

        modelBuilder.Entity<TargetRecord>(entity =>
        {
            entity.ToTable("TargetRecord");
            entity.HasKey(e => e.TargetId);
            entity.Property(e => e.DisplayName).IsRequired();
            entity.Property(e => e.SourceType).IsRequired();
            entity.Property(e => e.SourceLocator).IsRequired();
            entity.Property(e => e.IngestModuleVersion).IsRequired();
            entity.HasIndex(e => new { e.CaseId, e.DisplayName });

            entity.HasOne(e => e.Case)
                .WithMany()
                .HasForeignKey(e => e.CaseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TargetAliasRecord>(entity =>
        {
            entity.ToTable("TargetAliasRecord");
            entity.HasKey(e => e.AliasId);
            entity.Property(e => e.Alias).IsRequired();
            entity.Property(e => e.AliasNormalized).IsRequired();
            entity.Property(e => e.SourceType).IsRequired();
            entity.Property(e => e.SourceLocator).IsRequired();
            entity.Property(e => e.IngestModuleVersion).IsRequired();
            entity.HasIndex(e => new { e.CaseId, e.AliasNormalized, e.TargetId }).IsUnique();

            entity.HasOne(e => e.Target)
                .WithMany(t => t.Aliases)
                .HasForeignKey(e => e.TargetId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IdentifierRecord>(entity =>
        {
            entity.ToTable("IdentifierRecord");
            entity.HasKey(e => e.IdentifierId);
            entity.Property(e => e.Type).IsRequired();
            entity.Property(e => e.ValueRaw).IsRequired();
            entity.Property(e => e.ValueNormalized).IsRequired();
            entity.Property(e => e.SourceType).IsRequired();
            entity.Property(e => e.SourceLocator).IsRequired();
            entity.Property(e => e.IngestModuleVersion).IsRequired();
            entity.HasIndex(e => new { e.CaseId, e.Type, e.ValueNormalized }).IsUnique();

            entity.HasOne(e => e.Case)
                .WithMany()
                .HasForeignKey(e => e.CaseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TargetIdentifierLinkRecord>(entity =>
        {
            entity.ToTable("TargetIdentifierLinkRecord");
            entity.HasKey(e => e.LinkId);
            entity.Property(e => e.SourceType).IsRequired();
            entity.Property(e => e.SourceLocator).IsRequired();
            entity.Property(e => e.IngestModuleVersion).IsRequired();
            entity.HasIndex(e => new { e.TargetId, e.IdentifierId }).IsUnique();

            entity.HasOne(e => e.Target)
                .WithMany(t => t.IdentifierLinks)
                .HasForeignKey(e => e.TargetId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Identifier)
                .WithMany(i => i.TargetLinks)
                .HasForeignKey(e => e.IdentifierId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MessageParticipantLinkRecord>(entity =>
        {
            entity.ToTable("MessageParticipantLinkRecord");
            entity.HasKey(e => e.ParticipantLinkId);
            entity.Property(e => e.Role).IsRequired();
            entity.Property(e => e.ParticipantRaw).IsRequired();
            entity.Property(e => e.SourceType).IsRequired();
            entity.Property(e => e.SourceLocator).IsRequired();
            entity.Property(e => e.IngestModuleVersion).IsRequired();
            entity.HasIndex(e => new { e.CaseId, e.IdentifierId });
            entity.HasIndex(e => new { e.CaseId, e.TargetId });
            entity.HasIndex(e => new { e.CaseId, e.MessageEventId });

            entity.HasOne(e => e.MessageEvent)
                .WithMany()
                .HasForeignKey(e => e.MessageEventId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Identifier)
                .WithMany()
                .HasForeignKey(e => e.IdentifierId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Target)
                .WithMany()
                .HasForeignKey(e => e.TargetId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
