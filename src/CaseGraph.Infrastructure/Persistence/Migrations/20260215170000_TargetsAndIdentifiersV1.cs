using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseGraph.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WorkspaceDbContext))]
[Migration("20260215170000_TargetsAndIdentifiersV1")]
public partial class TargetsAndIdentifiersV1 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "IdentifierRecord",
            columns: table => new
            {
                IdentifierId = table.Column<Guid>(type: "TEXT", nullable: false),
                CaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                Type = table.Column<string>(type: "TEXT", nullable: false),
                ValueRaw = table.Column<string>(type: "TEXT", nullable: false),
                ValueNormalized = table.Column<string>(type: "TEXT", nullable: false),
                Notes = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                SourceType = table.Column<string>(type: "TEXT", nullable: false),
                SourceEvidenceItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                SourceLocator = table.Column<string>(type: "TEXT", nullable: false),
                IngestModuleVersion = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_IdentifierRecord", x => x.IdentifierId);
                table.ForeignKey(
                    name: "FK_IdentifierRecord_CaseRecord_CaseId",
                    column: x => x.CaseId,
                    principalTable: "CaseRecord",
                    principalColumn: "CaseId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "TargetRecord",
            columns: table => new
            {
                TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                CaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                PrimaryAlias = table.Column<string>(type: "TEXT", nullable: true),
                Notes = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                SourceType = table.Column<string>(type: "TEXT", nullable: false),
                SourceEvidenceItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                SourceLocator = table.Column<string>(type: "TEXT", nullable: false),
                IngestModuleVersion = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TargetRecord", x => x.TargetId);
                table.ForeignKey(
                    name: "FK_TargetRecord_CaseRecord_CaseId",
                    column: x => x.CaseId,
                    principalTable: "CaseRecord",
                    principalColumn: "CaseId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "MessageParticipantLinkRecord",
            columns: table => new
            {
                ParticipantLinkId = table.Column<Guid>(type: "TEXT", nullable: false),
                CaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                MessageEventId = table.Column<Guid>(type: "TEXT", nullable: false),
                Role = table.Column<string>(type: "TEXT", nullable: false),
                ParticipantRaw = table.Column<string>(type: "TEXT", nullable: false),
                IdentifierId = table.Column<Guid>(type: "TEXT", nullable: false),
                TargetId = table.Column<Guid>(type: "TEXT", nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                SourceType = table.Column<string>(type: "TEXT", nullable: false),
                SourceEvidenceItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                SourceLocator = table.Column<string>(type: "TEXT", nullable: false),
                IngestModuleVersion = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MessageParticipantLinkRecord", x => x.ParticipantLinkId);
                table.ForeignKey(
                    name: "FK_MessageParticipantLinkRecord_IdentifierRecord_IdentifierId",
                    column: x => x.IdentifierId,
                    principalTable: "IdentifierRecord",
                    principalColumn: "IdentifierId",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_MessageParticipantLinkRecord_MessageEventRecord_MessageEventId",
                    column: x => x.MessageEventId,
                    principalTable: "MessageEventRecord",
                    principalColumn: "MessageEventId",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_MessageParticipantLinkRecord_TargetRecord_TargetId",
                    column: x => x.TargetId,
                    principalTable: "TargetRecord",
                    principalColumn: "TargetId",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "TargetAliasRecord",
            columns: table => new
            {
                AliasId = table.Column<Guid>(type: "TEXT", nullable: false),
                TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                CaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                Alias = table.Column<string>(type: "TEXT", nullable: false),
                AliasNormalized = table.Column<string>(type: "TEXT", nullable: false),
                SourceType = table.Column<string>(type: "TEXT", nullable: false),
                SourceEvidenceItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                SourceLocator = table.Column<string>(type: "TEXT", nullable: false),
                IngestModuleVersion = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TargetAliasRecord", x => x.AliasId);
                table.ForeignKey(
                    name: "FK_TargetAliasRecord_TargetRecord_TargetId",
                    column: x => x.TargetId,
                    principalTable: "TargetRecord",
                    principalColumn: "TargetId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "TargetIdentifierLinkRecord",
            columns: table => new
            {
                LinkId = table.Column<Guid>(type: "TEXT", nullable: false),
                CaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                IdentifierId = table.Column<Guid>(type: "TEXT", nullable: false),
                IsPrimary = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                SourceType = table.Column<string>(type: "TEXT", nullable: false),
                SourceEvidenceItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                SourceLocator = table.Column<string>(type: "TEXT", nullable: false),
                IngestModuleVersion = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TargetIdentifierLinkRecord", x => x.LinkId);
                table.ForeignKey(
                    name: "FK_TargetIdentifierLinkRecord_IdentifierRecord_IdentifierId",
                    column: x => x.IdentifierId,
                    principalTable: "IdentifierRecord",
                    principalColumn: "IdentifierId",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_TargetIdentifierLinkRecord_TargetRecord_TargetId",
                    column: x => x.TargetId,
                    principalTable: "TargetRecord",
                    principalColumn: "TargetId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_IdentifierRecord_CaseId_Type_ValueNormalized",
            table: "IdentifierRecord",
            columns: new[] { "CaseId", "Type", "ValueNormalized" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_MessageParticipantLinkRecord_CaseId_IdentifierId",
            table: "MessageParticipantLinkRecord",
            columns: new[] { "CaseId", "IdentifierId" });

        migrationBuilder.CreateIndex(
            name: "IX_MessageParticipantLinkRecord_CaseId_MessageEventId",
            table: "MessageParticipantLinkRecord",
            columns: new[] { "CaseId", "MessageEventId" });

        migrationBuilder.CreateIndex(
            name: "IX_MessageParticipantLinkRecord_CaseId_TargetId",
            table: "MessageParticipantLinkRecord",
            columns: new[] { "CaseId", "TargetId" });

        migrationBuilder.CreateIndex(
            name: "IX_MessageParticipantLinkRecord_IdentifierId",
            table: "MessageParticipantLinkRecord",
            column: "IdentifierId");

        migrationBuilder.CreateIndex(
            name: "IX_MessageParticipantLinkRecord_MessageEventId",
            table: "MessageParticipantLinkRecord",
            column: "MessageEventId");

        migrationBuilder.CreateIndex(
            name: "IX_MessageParticipantLinkRecord_TargetId",
            table: "MessageParticipantLinkRecord",
            column: "TargetId");

        migrationBuilder.CreateIndex(
            name: "IX_TargetAliasRecord_CaseId_AliasNormalized_TargetId",
            table: "TargetAliasRecord",
            columns: new[] { "CaseId", "AliasNormalized", "TargetId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_TargetAliasRecord_TargetId",
            table: "TargetAliasRecord",
            column: "TargetId");

        migrationBuilder.CreateIndex(
            name: "IX_TargetIdentifierLinkRecord_IdentifierId",
            table: "TargetIdentifierLinkRecord",
            column: "IdentifierId");

        migrationBuilder.CreateIndex(
            name: "IX_TargetIdentifierLinkRecord_TargetId_IdentifierId",
            table: "TargetIdentifierLinkRecord",
            columns: new[] { "TargetId", "IdentifierId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_TargetRecord_CaseId_DisplayName",
            table: "TargetRecord",
            columns: new[] { "CaseId", "DisplayName" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "MessageParticipantLinkRecord");
        migrationBuilder.DropTable(name: "TargetAliasRecord");
        migrationBuilder.DropTable(name: "TargetIdentifierLinkRecord");
        migrationBuilder.DropTable(name: "IdentifierRecord");
        migrationBuilder.DropTable(name: "TargetRecord");
    }
}
