using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseGraph.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WorkspaceDbContext))]
[Migration("20260314110000_GangDocumentationV1")]
public partial class GangDocumentationV1 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "GangDocumentationRecord",
            columns: table => new
            {
                DocumentationId = table.Column<Guid>(type: "TEXT", nullable: false),
                CaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                GlobalEntityId = table.Column<Guid>(type: "TEXT", nullable: true),
                OrganizationId = table.Column<Guid>(type: "TEXT", nullable: false),
                SubgroupOrganizationId = table.Column<Guid>(type: "TEXT", nullable: true),
                AffiliationRole = table.Column<string>(type: "TEXT", nullable: false),
                DocumentationStatus = table.Column<string>(type: "TEXT", nullable: false),
                ApprovalStatus = table.Column<string>(type: "TEXT", nullable: false),
                Reviewer = table.Column<string>(type: "TEXT", nullable: true),
                ReviewDueDateUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                Summary = table.Column<string>(type: "TEXT", nullable: false),
                Notes = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GangDocumentationRecord", x => x.DocumentationId);
                table.ForeignKey(
                    name: "FK_GangDocumentationRecord_OrganizationRecord_OrganizationId",
                    column: x => x.OrganizationId,
                    principalTable: "OrganizationRecord",
                    principalColumn: "OrganizationId",
                    onDelete: ReferentialAction.Restrict
                );
                table.ForeignKey(
                    name: "FK_GangDocumentationRecord_OrganizationRecord_SubgroupOrganizationId",
                    column: x => x.SubgroupOrganizationId,
                    principalTable: "OrganizationRecord",
                    principalColumn: "OrganizationId",
                    onDelete: ReferentialAction.Restrict
                );
                table.ForeignKey(
                    name: "FK_GangDocumentationRecord_PersonEntity_GlobalEntityId",
                    column: x => x.GlobalEntityId,
                    principalTable: "PersonEntity",
                    principalColumn: "GlobalEntityId",
                    onDelete: ReferentialAction.SetNull
                );
                table.ForeignKey(
                    name: "FK_GangDocumentationRecord_TargetRecord_TargetId",
                    column: x => x.TargetId,
                    principalTable: "TargetRecord",
                    principalColumn: "TargetId",
                    onDelete: ReferentialAction.Cascade
                );
            }
        );

        migrationBuilder.CreateTable(
            name: "GangDocumentationCriterion",
            columns: table => new
            {
                CriterionId = table.Column<Guid>(type: "TEXT", nullable: false),
                DocumentationId = table.Column<Guid>(type: "TEXT", nullable: false),
                CriterionType = table.Column<string>(type: "TEXT", nullable: false),
                IsMet = table.Column<bool>(type: "INTEGER", nullable: false),
                BasisSummary = table.Column<string>(type: "TEXT", nullable: false),
                ObservedDateUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                SourceNote = table.Column<string>(type: "TEXT", nullable: true),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GangDocumentationCriterion", x => x.CriterionId);
                table.ForeignKey(
                    name: "FK_GangDocumentationCriterion_GangDocumentationRecord_DocumentationId",
                    column: x => x.DocumentationId,
                    principalTable: "GangDocumentationRecord",
                    principalColumn: "DocumentationId",
                    onDelete: ReferentialAction.Cascade
                );
            }
        );

        migrationBuilder.CreateTable(
            name: "GangDocumentationStatusHistory",
            columns: table => new
            {
                HistoryEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                DocumentationId = table.Column<Guid>(type: "TEXT", nullable: false),
                ActionType = table.Column<string>(type: "TEXT", nullable: false),
                Summary = table.Column<string>(type: "TEXT", nullable: false),
                ChangedBy = table.Column<string>(type: "TEXT", nullable: true),
                ChangedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GangDocumentationStatusHistory", x => x.HistoryEntryId);
                table.ForeignKey(
                    name: "FK_GangDocumentationStatusHistory_GangDocumentationRecord_DocumentationId",
                    column: x => x.DocumentationId,
                    principalTable: "GangDocumentationRecord",
                    principalColumn: "DocumentationId",
                    onDelete: ReferentialAction.Cascade
                );
            }
        );

        migrationBuilder.CreateIndex(
            name: "IX_GangDocumentationRecord_CaseId_TargetId_UpdatedAtUtc",
            table: "GangDocumentationRecord",
            columns: new[] { "CaseId", "TargetId", "UpdatedAtUtc" }
        );

        migrationBuilder.CreateIndex(
            name: "IX_GangDocumentationRecord_CaseId_GlobalEntityId",
            table: "GangDocumentationRecord",
            columns: new[] { "CaseId", "GlobalEntityId" }
        );

        migrationBuilder.CreateIndex(
            name: "IX_GangDocumentationRecord_GlobalEntityId",
            table: "GangDocumentationRecord",
            column: "GlobalEntityId"
        );

        migrationBuilder.CreateIndex(
            name: "IX_GangDocumentationRecord_OrganizationId",
            table: "GangDocumentationRecord",
            column: "OrganizationId"
        );

        migrationBuilder.CreateIndex(
            name: "IX_GangDocumentationRecord_SubgroupOrganizationId",
            table: "GangDocumentationRecord",
            column: "SubgroupOrganizationId"
        );

        migrationBuilder.CreateIndex(
            name: "IX_GangDocumentationRecord_TargetId",
            table: "GangDocumentationRecord",
            column: "TargetId"
        );

        migrationBuilder.CreateIndex(
            name: "IX_GangDocumentationCriterion_DocumentationId_SortOrder",
            table: "GangDocumentationCriterion",
            columns: new[] { "DocumentationId", "SortOrder" }
        );

        migrationBuilder.CreateIndex(
            name: "IX_GangDocumentationStatusHistory_DocumentationId_ChangedAtUtc",
            table: "GangDocumentationStatusHistory",
            columns: new[] { "DocumentationId", "ChangedAtUtc" }
        );
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "GangDocumentationCriterion");
        migrationBuilder.DropTable(name: "GangDocumentationStatusHistory");
        migrationBuilder.DropTable(name: "GangDocumentationRecord");
    }
}
