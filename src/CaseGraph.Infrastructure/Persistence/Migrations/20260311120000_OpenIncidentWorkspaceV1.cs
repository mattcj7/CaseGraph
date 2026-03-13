using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseGraph.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WorkspaceDbContext))]
[Migration("20260311120000_OpenIncidentWorkspaceV1")]
public partial class OpenIncidentWorkspaceV1 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "IncidentRecord",
            columns: table => new
            {
                IncidentId = table.Column<Guid>(type: "TEXT", nullable: false),
                CaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                Title = table.Column<string>(type: "TEXT", nullable: false),
                IncidentType = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                SummaryNotes = table.Column<string>(type: "TEXT", nullable: false),
                PrimaryOccurrenceUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                OffenseWindowStartUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                OffenseWindowEndUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_IncidentRecord", x => x.IncidentId);
                table.ForeignKey(
                    name: "FK_IncidentRecord_CaseRecord_CaseId",
                    column: x => x.CaseId,
                    principalTable: "CaseRecord",
                    principalColumn: "CaseId",
                    onDelete: ReferentialAction.Cascade
                );
            }
        );

        migrationBuilder.CreateTable(
            name: "IncidentLocationRecord",
            columns: table => new
            {
                IncidentLocationId = table.Column<Guid>(type: "TEXT", nullable: false),
                IncidentId = table.Column<Guid>(type: "TEXT", nullable: false),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                Label = table.Column<string>(type: "TEXT", nullable: false),
                Latitude = table.Column<double>(type: "REAL", nullable: false),
                Longitude = table.Column<double>(type: "REAL", nullable: false),
                RadiusMeters = table.Column<double>(type: "REAL", nullable: false),
                Notes = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_IncidentLocationRecord", x => x.IncidentLocationId);
                table.ForeignKey(
                    name: "FK_IncidentLocationRecord_IncidentRecord_IncidentId",
                    column: x => x.IncidentId,
                    principalTable: "IncidentRecord",
                    principalColumn: "IncidentId",
                    onDelete: ReferentialAction.Cascade
                );
            }
        );

        migrationBuilder.CreateTable(
            name: "IncidentPinnedResultRecord",
            columns: table => new
            {
                IncidentPinnedResultId = table.Column<Guid>(type: "TEXT", nullable: false),
                IncidentId = table.Column<Guid>(type: "TEXT", nullable: false),
                ResultType = table.Column<string>(type: "TEXT", nullable: false),
                SourceRecordId = table.Column<Guid>(type: "TEXT", nullable: false),
                SourceEvidenceItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                SourceLocator = table.Column<string>(type: "TEXT", nullable: false),
                Citation = table.Column<string>(type: "TEXT", nullable: false),
                Title = table.Column<string>(type: "TEXT", nullable: false),
                Summary = table.Column<string>(type: "TEXT", nullable: false),
                EventUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                Latitude = table.Column<double>(type: "REAL", nullable: true),
                Longitude = table.Column<double>(type: "REAL", nullable: true),
                SceneLabel = table.Column<string>(type: "TEXT", nullable: true),
                PinnedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_IncidentPinnedResultRecord", x => x.IncidentPinnedResultId);
                table.ForeignKey(
                    name: "FK_IncidentPinnedResultRecord_IncidentRecord_IncidentId",
                    column: x => x.IncidentId,
                    principalTable: "IncidentRecord",
                    principalColumn: "IncidentId",
                    onDelete: ReferentialAction.Cascade
                );
            }
        );

        migrationBuilder.CreateIndex(
            name: "IX_IncidentLocationRecord_IncidentId_SortOrder",
            table: "IncidentLocationRecord",
            columns: new[] { "IncidentId", "SortOrder" }
        );

        migrationBuilder.CreateIndex(
            name: "IX_IncidentPinnedResultRecord_IncidentId",
            table: "IncidentPinnedResultRecord",
            column: "IncidentId"
        );

        migrationBuilder.CreateIndex(
            name: "IX_IncidentPinnedResultRecord_IncidentId_ResultType_SourceRecordId",
            table: "IncidentPinnedResultRecord",
            columns: new[] { "IncidentId", "ResultType", "SourceRecordId" },
            unique: true
        );

        migrationBuilder.CreateIndex(
            name: "IX_IncidentRecord_CaseId_OffenseWindowStartUtc_OffenseWindowEndUtc",
            table: "IncidentRecord",
            columns: new[] { "CaseId", "OffenseWindowStartUtc", "OffenseWindowEndUtc" }
        );

        migrationBuilder.CreateIndex(
            name: "IX_IncidentRecord_CaseId_UpdatedUtc",
            table: "IncidentRecord",
            columns: new[] { "CaseId", "UpdatedUtc" }
        );
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "IncidentLocationRecord");
        migrationBuilder.DropTable(name: "IncidentPinnedResultRecord");
        migrationBuilder.DropTable(name: "IncidentRecord");
    }
}
