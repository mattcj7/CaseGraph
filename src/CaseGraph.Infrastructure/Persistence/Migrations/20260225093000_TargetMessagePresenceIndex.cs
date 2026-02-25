using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseGraph.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WorkspaceDbContext))]
[Migration("20260225093000_TargetMessagePresenceIndex")]
public partial class TargetMessagePresenceIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TargetMessagePresenceRecord",
            columns: table => new
            {
                PresenceId = table.Column<Guid>(type: "TEXT", nullable: false),
                CaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                MessageEventId = table.Column<Guid>(type: "TEXT", nullable: false),
                MatchedIdentifierId = table.Column<Guid>(type: "TEXT", nullable: false),
                Role = table.Column<string>(type: "TEXT", nullable: false),
                EvidenceItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                SourceLocator = table.Column<string>(type: "TEXT", nullable: false),
                MessageTimestampUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                FirstSeenUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                LastSeenUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TargetMessagePresenceRecord", x => x.PresenceId);
                table.ForeignKey(
                    name: "FK_TargetMessagePresenceRecord_IdentifierRecord_MatchedIdentifierId",
                    column: x => x.MatchedIdentifierId,
                    principalTable: "IdentifierRecord",
                    principalColumn: "IdentifierId",
                    onDelete: ReferentialAction.Cascade
                );
                table.ForeignKey(
                    name: "FK_TargetMessagePresenceRecord_MessageEventRecord_MessageEventId",
                    column: x => x.MessageEventId,
                    principalTable: "MessageEventRecord",
                    principalColumn: "MessageEventId",
                    onDelete: ReferentialAction.Cascade
                );
                table.ForeignKey(
                    name: "FK_TargetMessagePresenceRecord_TargetRecord_TargetId",
                    column: x => x.TargetId,
                    principalTable: "TargetRecord",
                    principalColumn: "TargetId",
                    onDelete: ReferentialAction.Cascade
                );
            }
        );

        migrationBuilder.CreateIndex(
            name: "IX_TargetMessagePresenceRecord_CaseId_EvidenceItemId",
            table: "TargetMessagePresenceRecord",
            columns: new[] { "CaseId", "EvidenceItemId" }
        );

        migrationBuilder.CreateIndex(
            name: "IX_TargetMessagePresenceRecord_CaseId_TargetId_MatchedIdentifierId",
            table: "TargetMessagePresenceRecord",
            columns: new[] { "CaseId", "TargetId", "MatchedIdentifierId" }
        );

        migrationBuilder.CreateIndex(
            name: "IX_TargetMessagePresenceRecord_CaseId_TargetId_MessageEventId",
            table: "TargetMessagePresenceRecord",
            columns: new[] { "CaseId", "TargetId", "MessageEventId" }
        );

        migrationBuilder.CreateIndex(
            name: "IX_TargetMessagePresenceRecord_CaseId_TargetId_MessageEventId_MatchedIdentifierId_Role",
            table: "TargetMessagePresenceRecord",
            columns: new[] { "CaseId", "TargetId", "MessageEventId", "MatchedIdentifierId", "Role" },
            unique: true
        );

        migrationBuilder.CreateIndex(
            name: "IX_TargetMessagePresenceRecord_MatchedIdentifierId",
            table: "TargetMessagePresenceRecord",
            column: "MatchedIdentifierId"
        );

        migrationBuilder.CreateIndex(
            name: "IX_TargetMessagePresenceRecord_MessageEventId",
            table: "TargetMessagePresenceRecord",
            column: "MessageEventId"
        );

        migrationBuilder.CreateIndex(
            name: "IX_TargetMessagePresenceRecord_TargetId",
            table: "TargetMessagePresenceRecord",
            column: "TargetId"
        );
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "TargetMessagePresenceRecord");
    }
}
