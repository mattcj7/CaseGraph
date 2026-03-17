using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseGraph.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WorkspaceDbContext))]
[Migration("20260316113000_GangDocumentationSupervisorWorkflowV1")]
public partial class GangDocumentationSupervisorWorkflowV1 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "GangDocumentationReview",
            columns: table => new
            {
                ReviewId = table.Column<Guid>(type: "TEXT", nullable: false),
                DocumentationId = table.Column<Guid>(type: "TEXT", nullable: false),
                WorkflowStatus = table.Column<string>(type: "TEXT", nullable: false),
                ReviewerName = table.Column<string>(type: "TEXT", nullable: true),
                ReviewerIdentity = table.Column<string>(type: "TEXT", nullable: true),
                SubmittedForReviewAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                ReviewedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                ApprovedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                ReviewDueDateUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                DecisionNote = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_GangDocumentationReview", x => x.ReviewId);
                table.ForeignKey(
                    name: "FK_GangDocumentationReview_GangDocumentationRecord_DocumentationId",
                    column: x => x.DocumentationId,
                    principalTable: "GangDocumentationRecord",
                    principalColumn: "DocumentationId",
                    onDelete: ReferentialAction.Cascade
                );
            }
        );

        migrationBuilder.AddColumn<string>(
            name: "PreviousWorkflowStatus",
            table: "GangDocumentationStatusHistory",
            type: "TEXT",
            nullable: true
        );

        migrationBuilder.AddColumn<string>(
            name: "NewWorkflowStatus",
            table: "GangDocumentationStatusHistory",
            type: "TEXT",
            nullable: true
        );

        migrationBuilder.AddColumn<string>(
            name: "DecisionNote",
            table: "GangDocumentationStatusHistory",
            type: "TEXT",
            nullable: true
        );

        migrationBuilder.AddColumn<string>(
            name: "ChangedByIdentity",
            table: "GangDocumentationStatusHistory",
            type: "TEXT",
            nullable: true
        );

        migrationBuilder.Sql(
            @"
INSERT INTO GangDocumentationReview (
    ReviewId,
    DocumentationId,
    WorkflowStatus,
    ReviewerName,
    ReviewerIdentity,
    SubmittedForReviewAtUtc,
    ReviewedAtUtc,
    ApprovedAtUtc,
    ReviewDueDateUtc,
    DecisionNote
)
SELECT
    DocumentationId,
    DocumentationId,
    CASE lower(DocumentationStatus)
        WHEN 'pending review' THEN 'pending supervisor review'
        WHEN 'draft' THEN 'draft'
        WHEN 'approved' THEN 'approved'
        WHEN 'inactive' THEN 'inactive'
        WHEN 'purge review' THEN 'purge review'
        ELSE 'draft'
    END,
    Reviewer,
    NULL,
    CASE lower(DocumentationStatus)
        WHEN 'pending review' THEN UpdatedAtUtc
        ELSE NULL
    END,
    CASE lower(DocumentationStatus)
        WHEN 'approved' THEN UpdatedAtUtc
        ELSE NULL
    END,
    CASE lower(DocumentationStatus)
        WHEN 'approved' THEN UpdatedAtUtc
        ELSE NULL
    END,
    ReviewDueDateUtc,
    NULL
FROM GangDocumentationRecord;"
        );

        migrationBuilder.Sql(
            @"
UPDATE GangDocumentationRecord
SET
    DocumentationStatus = CASE lower(DocumentationStatus)
        WHEN 'pending review' THEN 'pending supervisor review'
        WHEN 'draft' THEN 'draft'
        WHEN 'approved' THEN 'approved'
        WHEN 'inactive' THEN 'inactive'
        WHEN 'purge review' THEN 'purge review'
        ELSE 'draft'
    END,
    ApprovalStatus = CASE lower(DocumentationStatus)
        WHEN 'approved' THEN 'approved'
        WHEN 'pending review' THEN 'pending approval'
        WHEN 'purge review' THEN 'pending approval'
        ELSE 'not submitted'
    END;"
        );

        migrationBuilder.CreateIndex(
            name: "IX_GangDocumentationReview_DocumentationId",
            table: "GangDocumentationReview",
            column: "DocumentationId",
            unique: true
        );
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "GangDocumentationReview");

        migrationBuilder.DropColumn(
            name: "PreviousWorkflowStatus",
            table: "GangDocumentationStatusHistory"
        );

        migrationBuilder.DropColumn(
            name: "NewWorkflowStatus",
            table: "GangDocumentationStatusHistory"
        );

        migrationBuilder.DropColumn(
            name: "DecisionNote",
            table: "GangDocumentationStatusHistory"
        );

        migrationBuilder.DropColumn(
            name: "ChangedByIdentity",
            table: "GangDocumentationStatusHistory"
        );
    }
}
