using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MultiChannelAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReferenceAdministration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UnitTerms_InventoryId_NormalizedTerm",
                table: "UnitTerms");

            migrationBuilder.DropIndex(
                name: "IX_UnitTerms_InventoryId_UnitId",
                table: "UnitTerms");

            migrationBuilder.DropIndex(
                name: "IX_Locations_InventoryId_NormalizedName",
                table: "Locations");

            // The shared Unit term namespace is enforced and ordered against this column, so it has to
            // compare exactly as the domain does - ordinally. On a default SQL Server collation the
            // namespace would be accent-insensitive here and accent-sensitive on SQLite, and bounded
            // suggestions would come back in a different order on each.
            migrationBuilder.AlterColumn<string>(
                name: "NormalizedTerm",
                table: "UnitTerms",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(100)",
                oldMaxLength: 100);

            migrationBuilder.AddColumn<bool>(
                name: "IsReserved",
                table: "UnitTerms",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetiredAt",
                table: "UnitTerms",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ConcurrencyStamp",
                table: "Units",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetiredAt",
                table: "Units",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ConcurrencyStamp",
                table: "Locations",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetiredAt",
                table: "Locations",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpectedReferenceVersionsJson",
                table: "ConfirmationProposals",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpectedTermAbsencesJson",
                table: "ConfirmationProposals",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "ConfirmationProposals",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ReferenceChangesJson",
                table: "ConfirmationProposals",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ConfirmationProposalReferences",
                columns: table => new
                {
                    ProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReferenceKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ReferenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfirmationProposalReferences", x => new { x.ProposalId, x.ReferenceKind, x.ReferenceId });
                    table.ForeignKey(
                        name: "FK_ConfirmationProposalReferences_ConfirmationProposals_ProposalId",
                        column: x => x.ProposalId,
                        principalTable: "ConfirmationProposals",
                        principalColumn: "ProposalId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReferenceOperations",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InventoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConfirmedByTurnId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AppliedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferenceOperations", x => x.OperationId);
                    table.ForeignKey(
                        name: "FK_ReferenceOperations_Inventories_InventoryId",
                        column: x => x.InventoryId,
                        principalTable: "Inventories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReferenceEffects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReferenceKind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ReferenceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NewName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Alias = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AliasesJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferenceEffects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReferenceEffects_ReferenceOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "ReferenceOperations",
                        principalColumn: "OperationId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UnitTerms_InventoryId_NormalizedTerm",
                table: "UnitTerms",
                columns: new[] { "InventoryId", "NormalizedTerm" },
                unique: true,
                filter: "RetiredAt IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UnitTerms_InventoryId_UnitId_RetiredAt",
                table: "UnitTerms",
                columns: new[] { "InventoryId", "UnitId", "RetiredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Units_InventoryId_RetiredAt",
                table: "Units",
                columns: new[] { "InventoryId", "RetiredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Locations_InventoryId_NormalizedName",
                table: "Locations",
                columns: new[] { "InventoryId", "NormalizedName" },
                unique: true,
                filter: "RetiredAt IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Locations_InventoryId_RetiredAt",
                table: "Locations",
                columns: new[] { "InventoryId", "RetiredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ConfirmationProposalReferences_ReferenceKind_ReferenceId",
                table: "ConfirmationProposalReferences",
                columns: new[] { "ReferenceKind", "ReferenceId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReferenceEffects_OperationId_Order",
                table: "ReferenceEffects",
                columns: new[] { "OperationId", "Order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReferenceOperations_AppliedAt",
                table: "ReferenceOperations",
                column: "AppliedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ReferenceOperations_InventoryId_ConfirmedByTurnId",
                table: "ReferenceOperations",
                columns: new[] { "InventoryId", "ConfirmedByTurnId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReferenceOperations_ProposalId",
                table: "ReferenceOperations",
                column: "ProposalId",
                unique: true,
                filter: "ProposalId IS NOT NULL");

            // Every existing Unit and Location gets a real starting version. An empty Guid would still
            // work - the version only has to change on write - but a distinct one makes an accidental
            // "expected version was never read" bug visible instead of silently passing.
            migrationBuilder.Sql("UPDATE Units SET ConcurrencyStamp = NEWID() WHERE ConcurrencyStamp = '00000000-0000-0000-0000-000000000000';");
            migrationBuilder.Sql("UPDATE Locations SET ConcurrencyStamp = NEWID() WHERE ConcurrencyStamp = '00000000-0000-0000-0000-000000000000';");

            // Every term that exists today belongs to a reserved `each` Unit - nothing else has ever
            // been able to create one - so this marks exactly the five fixed terms per Inventory.
            migrationBuilder.Sql(
                "UPDATE UnitTerms SET IsReserved = 1 WHERE UnitId IN (SELECT Id FROM Units WHERE IsReserved = 1);");

            // Every proposal that exists today is a stock proposal; nothing else has ever been stored.
            migrationBuilder.Sql("UPDATE ConfirmationProposals SET Kind = 'Stock' WHERE Kind = '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "NormalizedTerm",
                table: "UnitTerms",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(100)",
                oldMaxLength: 100,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.DropTable(
                name: "ConfirmationProposalReferences");

            migrationBuilder.DropTable(
                name: "ReferenceEffects");

            migrationBuilder.DropTable(
                name: "ReferenceOperations");

            migrationBuilder.DropIndex(
                name: "IX_UnitTerms_InventoryId_NormalizedTerm",
                table: "UnitTerms");

            migrationBuilder.DropIndex(
                name: "IX_UnitTerms_InventoryId_UnitId_RetiredAt",
                table: "UnitTerms");

            migrationBuilder.DropIndex(
                name: "IX_Units_InventoryId_RetiredAt",
                table: "Units");

            migrationBuilder.DropIndex(
                name: "IX_Locations_InventoryId_NormalizedName",
                table: "Locations");

            migrationBuilder.DropIndex(
                name: "IX_Locations_InventoryId_RetiredAt",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "IsReserved",
                table: "UnitTerms");

            migrationBuilder.DropColumn(
                name: "RetiredAt",
                table: "UnitTerms");

            migrationBuilder.DropColumn(
                name: "ConcurrencyStamp",
                table: "Units");

            migrationBuilder.DropColumn(
                name: "RetiredAt",
                table: "Units");

            migrationBuilder.DropColumn(
                name: "ConcurrencyStamp",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "RetiredAt",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "ExpectedReferenceVersionsJson",
                table: "ConfirmationProposals");

            migrationBuilder.DropColumn(
                name: "ExpectedTermAbsencesJson",
                table: "ConfirmationProposals");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "ConfirmationProposals");

            migrationBuilder.DropColumn(
                name: "ReferenceChangesJson",
                table: "ConfirmationProposals");

            // Deliberately not unique. Retirement frees a name, so once anything has been retired and
            // its name reused there are legitimately several rows sharing a normalized term - and
            // recreating a unique index over them would fail after the RetiredAt column that told them
            // apart has already been dropped, leaving a half-migrated schema.
            migrationBuilder.CreateIndex(
                name: "IX_UnitTerms_InventoryId_NormalizedTerm",
                table: "UnitTerms",
                columns: new[] { "InventoryId", "NormalizedTerm" });

            migrationBuilder.CreateIndex(
                name: "IX_UnitTerms_InventoryId_UnitId",
                table: "UnitTerms",
                columns: new[] { "InventoryId", "UnitId" });

            // Not unique, for the same reason as IX_UnitTerms_InventoryId_NormalizedTerm above.
            migrationBuilder.CreateIndex(
                name: "IX_Locations_InventoryId_NormalizedName",
                table: "Locations",
                columns: new[] { "InventoryId", "NormalizedName" });
        }
    }
}
