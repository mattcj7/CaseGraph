using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseGraph.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WorkspaceDbContext))]
[Migration("20260213190000_InitialWorkspaceSchema")]
public partial class InitialWorkspaceSchema : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AuditEventRecord",
            columns: table => new
            {
                AuditEventId = table.Column<Guid>(type: "TEXT", nullable: false),
                TimestampUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                Operator = table.Column<string>(type: "TEXT", nullable: false),
                ActionType = table.Column<string>(type: "TEXT", nullable: false),
                CaseId = table.Column<Guid>(type: "TEXT", nullable: true),
                EvidenceItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                Summary = table.Column<string>(type: "TEXT", nullable: false),
                JsonPayload = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuditEventRecord", x => x.AuditEventId);
            });

        migrationBuilder.CreateTable(
            name: "CaseRecord",
            columns: table => new
            {
                CaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                LastOpenedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CaseRecord", x => x.CaseId);
            });

        migrationBuilder.CreateTable(
            name: "JobRecord",
            columns: table => new
            {
                JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                StartedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                CompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                JobType = table.Column<string>(type: "TEXT", nullable: false),
                CaseId = table.Column<Guid>(type: "TEXT", nullable: true),
                EvidenceItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                Progress = table.Column<double>(type: "REAL", nullable: false),
                StatusMessage = table.Column<string>(type: "TEXT", nullable: false),
                ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                JsonPayload = table.Column<string>(type: "TEXT", nullable: false),
                CorrelationId = table.Column<string>(type: "TEXT", nullable: false),
                Operator = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_JobRecord", x => x.JobId);
            });

        migrationBuilder.CreateTable(
            name: "EvidenceItemRecord",
            columns: table => new
            {
                EvidenceItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                CaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                OriginalPath = table.Column<string>(type: "TEXT", nullable: false),
                OriginalFileName = table.Column<string>(type: "TEXT", nullable: false),
                AddedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                Sha256Hex = table.Column<string>(type: "TEXT", nullable: false),
                FileExtension = table.Column<string>(type: "TEXT", nullable: false),
                SourceType = table.Column<string>(type: "TEXT", nullable: false),
                ManifestRelativePath = table.Column<string>(type: "TEXT", nullable: false),
                StoredRelativePath = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_EvidenceItemRecord", x => x.EvidenceItemId);
                table.ForeignKey(
                    name: "FK_EvidenceItemRecord_CaseRecord_CaseId",
                    column: x => x.CaseId,
                    principalTable: "CaseRecord",
                    principalColumn: "CaseId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "MessageThreadRecord",
            columns: table => new
            {
                ThreadId = table.Column<Guid>(type: "TEXT", nullable: false),
                CaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                EvidenceItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                Platform = table.Column<string>(type: "TEXT", nullable: false),
                ThreadKey = table.Column<string>(type: "TEXT", nullable: false),
                Title = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                SourceLocator = table.Column<string>(type: "TEXT", nullable: false),
                IngestModuleVersion = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MessageThreadRecord", x => x.ThreadId);
                table.ForeignKey(
                    name: "FK_MessageThreadRecord_CaseRecord_CaseId",
                    column: x => x.CaseId,
                    principalTable: "CaseRecord",
                    principalColumn: "CaseId",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_MessageThreadRecord_EvidenceItemRecord_EvidenceItemId",
                    column: x => x.EvidenceItemId,
                    principalTable: "EvidenceItemRecord",
                    principalColumn: "EvidenceItemId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "MessageEventRecord",
            columns: table => new
            {
                MessageEventId = table.Column<Guid>(type: "TEXT", nullable: false),
                ThreadId = table.Column<Guid>(type: "TEXT", nullable: false),
                CaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                EvidenceItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                Platform = table.Column<string>(type: "TEXT", nullable: false),
                TimestampUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                Direction = table.Column<string>(type: "TEXT", nullable: false),
                Sender = table.Column<string>(type: "TEXT", nullable: true),
                Recipients = table.Column<string>(type: "TEXT", nullable: true),
                Body = table.Column<string>(type: "TEXT", nullable: true),
                IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                SourceLocator = table.Column<string>(type: "TEXT", nullable: false),
                IngestModuleVersion = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MessageEventRecord", x => x.MessageEventId);
                table.ForeignKey(
                    name: "FK_MessageEventRecord_MessageThreadRecord_ThreadId",
                    column: x => x.ThreadId,
                    principalTable: "MessageThreadRecord",
                    principalColumn: "ThreadId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "MessageParticipantRecord",
            columns: table => new
            {
                ParticipantId = table.Column<Guid>(type: "TEXT", nullable: false),
                ThreadId = table.Column<Guid>(type: "TEXT", nullable: false),
                Value = table.Column<string>(type: "TEXT", nullable: false),
                Kind = table.Column<string>(type: "TEXT", nullable: false),
                SourceLocator = table.Column<string>(type: "TEXT", nullable: false),
                IngestModuleVersion = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MessageParticipantRecord", x => x.ParticipantId);
                table.ForeignKey(
                    name: "FK_MessageParticipantRecord_MessageThreadRecord_ThreadId",
                    column: x => x.ThreadId,
                    principalTable: "MessageThreadRecord",
                    principalColumn: "ThreadId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_EvidenceItemRecord_CaseId",
            table: "EvidenceItemRecord",
            column: "CaseId");

        migrationBuilder.CreateIndex(
            name: "IX_MessageEventRecord_EvidenceItemId_SourceLocator",
            table: "MessageEventRecord",
            columns: new[] { "EvidenceItemId", "SourceLocator" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_MessageEventRecord_ThreadId",
            table: "MessageEventRecord",
            column: "ThreadId");

        migrationBuilder.CreateIndex(
            name: "IX_MessageParticipantRecord_ThreadId",
            table: "MessageParticipantRecord",
            column: "ThreadId");

        migrationBuilder.CreateIndex(
            name: "IX_MessageThreadRecord_CaseId_Platform",
            table: "MessageThreadRecord",
            columns: new[] { "CaseId", "Platform" });

        migrationBuilder.CreateIndex(
            name: "IX_MessageThreadRecord_EvidenceItemId",
            table: "MessageThreadRecord",
            column: "EvidenceItemId");

        migrationBuilder.Sql(
            """
            CREATE VIRTUAL TABLE IF NOT EXISTS MessageEventFts
            USING fts5(MessageEventId UNINDEXED, CaseId UNINDEXED, Platform, Sender, Recipients, Body);
            """);

        migrationBuilder.Sql(
            """
            CREATE TRIGGER IF NOT EXISTS MessageEventRecord_Fts_Insert
            AFTER INSERT ON MessageEventRecord
            BEGIN
                INSERT INTO MessageEventFts(MessageEventId, CaseId, Platform, Sender, Recipients, Body)
                VALUES (
                    NEW.MessageEventId,
                    NEW.CaseId,
                    COALESCE(NEW.Platform, ''),
                    COALESCE(NEW.Sender, ''),
                    COALESCE(NEW.Recipients, ''),
                    COALESCE(NEW.Body, '')
                );
            END;
            """);

        migrationBuilder.Sql(
            """
            CREATE TRIGGER IF NOT EXISTS MessageEventRecord_Fts_Update
            AFTER UPDATE ON MessageEventRecord
            BEGIN
                DELETE FROM MessageEventFts WHERE MessageEventId = OLD.MessageEventId;
                INSERT INTO MessageEventFts(MessageEventId, CaseId, Platform, Sender, Recipients, Body)
                VALUES (
                    NEW.MessageEventId,
                    NEW.CaseId,
                    COALESCE(NEW.Platform, ''),
                    COALESCE(NEW.Sender, ''),
                    COALESCE(NEW.Recipients, ''),
                    COALESCE(NEW.Body, '')
                );
            END;
            """);

        migrationBuilder.Sql(
            """
            CREATE TRIGGER IF NOT EXISTS MessageEventRecord_Fts_Delete
            AFTER DELETE ON MessageEventRecord
            BEGIN
                DELETE FROM MessageEventFts WHERE MessageEventId = OLD.MessageEventId;
            END;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS MessageEventRecord_Fts_Delete;");
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS MessageEventRecord_Fts_Update;");
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS MessageEventRecord_Fts_Insert;");
        migrationBuilder.Sql("DROP TABLE IF EXISTS MessageEventFts;");

        migrationBuilder.DropTable(name: "AuditEventRecord");
        migrationBuilder.DropTable(name: "JobRecord");
        migrationBuilder.DropTable(name: "MessageParticipantRecord");
        migrationBuilder.DropTable(name: "MessageEventRecord");
        migrationBuilder.DropTable(name: "MessageThreadRecord");
        migrationBuilder.DropTable(name: "EvidenceItemRecord");
        migrationBuilder.DropTable(name: "CaseRecord");
    }
}
