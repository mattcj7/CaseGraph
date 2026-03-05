using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseGraph.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WorkspaceDbContext))]
[Migration("20260304153000_LocationObservationV1")]
public partial class LocationObservationV1 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "LocationObservationRecord",
            columns: table => new
            {
                LocationObservationId = table.Column<Guid>(type: "TEXT", nullable: false),
                CaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                ObservedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                Latitude = table.Column<double>(type: "REAL", nullable: false),
                Longitude = table.Column<double>(type: "REAL", nullable: false),
                AccuracyMeters = table.Column<double>(type: "REAL", nullable: true),
                AltitudeMeters = table.Column<double>(type: "REAL", nullable: true),
                SpeedMps = table.Column<double>(type: "REAL", nullable: true),
                HeadingDegrees = table.Column<double>(type: "REAL", nullable: true),
                SourceType = table.Column<string>(type: "TEXT", nullable: false),
                SourceLabel = table.Column<string>(type: "TEXT", nullable: true),
                SubjectType = table.Column<string>(type: "TEXT", nullable: true),
                SubjectId = table.Column<Guid>(type: "TEXT", nullable: true),
                SourceEvidenceItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                SourceLocator = table.Column<string>(type: "TEXT", nullable: false),
                IngestModuleVersion = table.Column<string>(type: "TEXT", nullable: false),
                CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LocationObservationRecord", x => x.LocationObservationId);
                table.ForeignKey(
                    name: "FK_LocationObservationRecord_CaseRecord_CaseId",
                    column: x => x.CaseId,
                    principalTable: "CaseRecord",
                    principalColumn: "CaseId",
                    onDelete: ReferentialAction.Cascade
                );
                table.ForeignKey(
                    name: "FK_LocationObservationRecord_EvidenceItemRecord_SourceEvidenceItemId",
                    column: x => x.SourceEvidenceItemId,
                    principalTable: "EvidenceItemRecord",
                    principalColumn: "EvidenceItemId",
                    onDelete: ReferentialAction.Cascade
                );
            }
        );

        migrationBuilder.CreateIndex(
            name: "IX_LocationObservationRecord_CaseId_Latitude_Longitude",
            table: "LocationObservationRecord",
            columns: new[] { "CaseId", "Latitude", "Longitude" }
        );

        migrationBuilder.CreateIndex(
            name: "IX_LocationObservationRecord_SourceEvidenceItemId",
            table: "LocationObservationRecord",
            column: "SourceEvidenceItemId"
        );

        migrationBuilder.Sql(
            """
            CREATE INDEX IF NOT EXISTS IX_LocationObservationRecord_CaseId_ObservedUtc_Desc
            ON LocationObservationRecord (CaseId, ObservedUtc DESC);
            """
        );

        migrationBuilder.Sql(
            """
            CREATE INDEX IF NOT EXISTS IX_LocationObservationRecord_CaseId_SubjectType_SubjectId_ObservedUtc_Desc
            ON LocationObservationRecord (CaseId, SubjectType, SubjectId, ObservedUtc DESC);
            """
        );
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX IF EXISTS IX_LocationObservationRecord_CaseId_SubjectType_SubjectId_ObservedUtc_Desc;");
        migrationBuilder.Sql("DROP INDEX IF EXISTS IX_LocationObservationRecord_CaseId_ObservedUtc_Desc;");
        migrationBuilder.DropTable(name: "LocationObservationRecord");
    }
}
