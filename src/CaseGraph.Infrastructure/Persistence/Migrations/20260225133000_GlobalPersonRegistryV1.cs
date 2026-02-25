using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaseGraph.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WorkspaceDbContext))]
[Migration("20260225133000_GlobalPersonRegistryV1")]
public partial class GlobalPersonRegistryV1 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PersonEntity",
            columns: table => new
            {
                GlobalEntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PersonEntity", x => x.GlobalEntityId);
            }
        );

        migrationBuilder.AddColumn<Guid>(
            name: "GlobalEntityId",
            table: "TargetRecord",
            type: "TEXT",
            nullable: true
        );

        migrationBuilder.CreateTable(
            name: "PersonAlias",
            columns: table => new
            {
                AliasId = table.Column<Guid>(type: "TEXT", nullable: false),
                GlobalEntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                Alias = table.Column<string>(type: "TEXT", nullable: false),
                AliasNormalized = table.Column<string>(type: "TEXT", nullable: false),
                Notes = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PersonAlias", x => x.AliasId);
                table.ForeignKey(
                    name: "FK_PersonAlias_PersonEntity_GlobalEntityId",
                    column: x => x.GlobalEntityId,
                    principalTable: "PersonEntity",
                    principalColumn: "GlobalEntityId",
                    onDelete: ReferentialAction.Cascade
                );
            }
        );

        migrationBuilder.CreateTable(
            name: "PersonIdentifier",
            columns: table => new
            {
                PersonIdentifierId = table.Column<Guid>(type: "TEXT", nullable: false),
                GlobalEntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                Type = table.Column<string>(type: "TEXT", nullable: false),
                ValueNormalized = table.Column<string>(type: "TEXT", nullable: false),
                ValueDisplay = table.Column<string>(type: "TEXT", nullable: false),
                IsPrimary = table.Column<bool>(type: "INTEGER", nullable: false),
                Notes = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PersonIdentifier", x => x.PersonIdentifierId);
                table.ForeignKey(
                    name: "FK_PersonIdentifier_PersonEntity_GlobalEntityId",
                    column: x => x.GlobalEntityId,
                    principalTable: "PersonEntity",
                    principalColumn: "GlobalEntityId",
                    onDelete: ReferentialAction.Cascade
                );
            }
        );

        migrationBuilder.CreateIndex(
            name: "IX_PersonAlias_GlobalEntityId_AliasNormalized",
            table: "PersonAlias",
            columns: new[] { "GlobalEntityId", "AliasNormalized" },
            unique: true
        );

        migrationBuilder.CreateIndex(
            name: "IX_PersonEntity_DisplayName",
            table: "PersonEntity",
            column: "DisplayName"
        );

        migrationBuilder.CreateIndex(
            name: "IX_PersonIdentifier_GlobalEntityId_Type_ValueNormalized",
            table: "PersonIdentifier",
            columns: new[] { "GlobalEntityId", "Type", "ValueNormalized" },
            unique: true
        );

        migrationBuilder.CreateIndex(
            name: "IX_PersonIdentifier_Type_ValueNormalized",
            table: "PersonIdentifier",
            columns: new[] { "Type", "ValueNormalized" },
            unique: true
        );

        migrationBuilder.CreateIndex(
            name: "IX_TargetRecord_CaseId_GlobalEntityId",
            table: "TargetRecord",
            columns: new[] { "CaseId", "GlobalEntityId" }
        );

    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "PersonAlias");
        migrationBuilder.DropTable(name: "PersonIdentifier");
        migrationBuilder.DropTable(name: "PersonEntity");

        migrationBuilder.DropIndex(
            name: "IX_TargetRecord_CaseId_GlobalEntityId",
            table: "TargetRecord"
        );

        migrationBuilder.DropColumn(
            name: "GlobalEntityId",
            table: "TargetRecord"
        );
    }
}
