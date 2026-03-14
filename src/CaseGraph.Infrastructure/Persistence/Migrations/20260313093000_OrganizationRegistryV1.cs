using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseGraph.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WorkspaceDbContext))]
[Migration("20260313093000_OrganizationRegistryV1")]
public partial class OrganizationRegistryV1 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "OrganizationRecord",
            columns: table => new
            {
                OrganizationId = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                NameNormalized = table.Column<string>(type: "TEXT", nullable: false),
                Type = table.Column<string>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                ParentOrganizationId = table.Column<Guid>(type: "TEXT", nullable: true),
                Summary = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OrganizationRecord", x => x.OrganizationId);
                table.ForeignKey(
                    name: "FK_OrganizationRecord_OrganizationRecord_ParentOrganizationId",
                    column: x => x.ParentOrganizationId,
                    principalTable: "OrganizationRecord",
                    principalColumn: "OrganizationId",
                    onDelete: ReferentialAction.Restrict
                );
            }
        );

        migrationBuilder.CreateTable(
            name: "OrganizationAlias",
            columns: table => new
            {
                AliasId = table.Column<Guid>(type: "TEXT", nullable: false),
                OrganizationId = table.Column<Guid>(type: "TEXT", nullable: false),
                Alias = table.Column<string>(type: "TEXT", nullable: false),
                AliasNormalized = table.Column<string>(type: "TEXT", nullable: false),
                Notes = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OrganizationAlias", x => x.AliasId);
                table.ForeignKey(
                    name: "FK_OrganizationAlias_OrganizationRecord_OrganizationId",
                    column: x => x.OrganizationId,
                    principalTable: "OrganizationRecord",
                    principalColumn: "OrganizationId",
                    onDelete: ReferentialAction.Cascade
                );
            }
        );

        migrationBuilder.CreateTable(
            name: "OrganizationMembership",
            columns: table => new
            {
                MembershipId = table.Column<Guid>(type: "TEXT", nullable: false),
                OrganizationId = table.Column<Guid>(type: "TEXT", nullable: false),
                GlobalEntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                Role = table.Column<string>(type: "TEXT", nullable: true),
                Status = table.Column<string>(type: "TEXT", nullable: false),
                Confidence = table.Column<int>(type: "INTEGER", nullable: false),
                BasisSummary = table.Column<string>(type: "TEXT", nullable: true),
                StartDateUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                EndDateUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                LastConfirmedDateUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                Reviewer = table.Column<string>(type: "TEXT", nullable: true),
                ReviewNotes = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OrganizationMembership", x => x.MembershipId);
                table.ForeignKey(
                    name: "FK_OrganizationMembership_OrganizationRecord_OrganizationId",
                    column: x => x.OrganizationId,
                    principalTable: "OrganizationRecord",
                    principalColumn: "OrganizationId",
                    onDelete: ReferentialAction.Cascade
                );
                table.ForeignKey(
                    name: "FK_OrganizationMembership_PersonEntity_GlobalEntityId",
                    column: x => x.GlobalEntityId,
                    principalTable: "PersonEntity",
                    principalColumn: "GlobalEntityId",
                    onDelete: ReferentialAction.Cascade
                );
            }
        );

        migrationBuilder.CreateIndex(
            name: "IX_OrganizationRecord_NameNormalized",
            table: "OrganizationRecord",
            column: "NameNormalized"
        );

        migrationBuilder.CreateIndex(
            name: "IX_OrganizationRecord_ParentOrganizationId",
            table: "OrganizationRecord",
            column: "ParentOrganizationId"
        );

        migrationBuilder.CreateIndex(
            name: "IX_OrganizationAlias_OrganizationId_AliasNormalized",
            table: "OrganizationAlias",
            columns: new[] { "OrganizationId", "AliasNormalized" },
            unique: true
        );

        migrationBuilder.CreateIndex(
            name: "IX_OrganizationMembership_GlobalEntityId",
            table: "OrganizationMembership",
            column: "GlobalEntityId"
        );

        migrationBuilder.CreateIndex(
            name: "IX_OrganizationMembership_OrganizationId_GlobalEntityId",
            table: "OrganizationMembership",
            columns: new[] { "OrganizationId", "GlobalEntityId" },
            unique: true
        );
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "OrganizationAlias");
        migrationBuilder.DropTable(name: "OrganizationMembership");
        migrationBuilder.DropTable(name: "OrganizationRecord");
    }
}
