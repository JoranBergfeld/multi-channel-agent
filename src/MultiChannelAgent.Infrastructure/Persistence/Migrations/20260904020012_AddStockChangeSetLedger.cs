using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MultiChannelAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStockChangeSetLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StockChangeSetOperations",
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
                    table.PrimaryKey("PK_StockChangeSetOperations", x => x.OperationId);
                    table.ForeignKey(
                        name: "FK_StockChangeSetOperations_Inventories_InventoryId",
                        column: x => x.InventoryId,
                        principalTable: "Inventories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StockChangeSetEffects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Effect = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SourceStockEntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceUnitCanonicalName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SourceLocationName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SourcePreviousQuantity = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    SourceResultingQuantity = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    SourceRetired = table.Column<bool>(type: "bit", nullable: false),
                    DestinationStockEntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DestinationName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DestinationUnitCanonicalName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DestinationLocationName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DestinationPreviousQuantity = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: true),
                    DestinationResultingQuantity = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: true),
                    TransferredQuantity = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    NewName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockChangeSetEffects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StockChangeSetEffects_StockChangeSetOperations_OperationId",
                        column: x => x.OperationId,
                        principalTable: "StockChangeSetOperations",
                        principalColumn: "OperationId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockChangeSetEffects_OperationId_Order",
                table: "StockChangeSetEffects",
                columns: new[] { "OperationId", "Order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockChangeSetOperations_AppliedAt",
                table: "StockChangeSetOperations",
                column: "AppliedAt");

            migrationBuilder.CreateIndex(
                name: "IX_StockChangeSetOperations_InventoryId_ConfirmedByTurnId",
                table: "StockChangeSetOperations",
                columns: new[] { "InventoryId", "ConfirmedByTurnId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockChangeSetOperations_ProposalId",
                table: "StockChangeSetOperations",
                column: "ProposalId",
                unique: true,
                filter: "ProposalId IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StockChangeSetEffects");

            migrationBuilder.DropTable(
                name: "StockChangeSetOperations");
        }
    }
}
