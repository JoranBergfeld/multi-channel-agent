using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MultiChannelAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceEquivalentStockWithoutMirrorColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StockEntries_InventoryId_NormalizedName_UnitId_LocationUniquenessKey",
                table: "StockEntries");

            migrationBuilder.DropColumn(
                name: "LocationUniquenessKey",
                table: "StockEntries");

            migrationBuilder.CreateIndex(
                name: "IX_StockEntries_InventoryId_NormalizedName_UnitId",
                table: "StockEntries",
                columns: new[] { "InventoryId", "NormalizedName", "UnitId" },
                unique: true,
                filter: "LocationId IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StockEntries_InventoryId_NormalizedName_UnitId_LocationId",
                table: "StockEntries",
                columns: new[] { "InventoryId", "NormalizedName", "UnitId", "LocationId" },
                unique: true,
                filter: "LocationId IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StockEntries_InventoryId_NormalizedName_UnitId",
                table: "StockEntries");

            migrationBuilder.DropIndex(
                name: "IX_StockEntries_InventoryId_NormalizedName_UnitId_LocationId",
                table: "StockEntries");

            migrationBuilder.AddColumn<Guid>(
                name: "LocationUniquenessKey",
                table: "StockEntries",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_StockEntries_InventoryId_NormalizedName_UnitId_LocationUniquenessKey",
                table: "StockEntries",
                columns: new[] { "InventoryId", "NormalizedName", "UnitId", "LocationUniquenessKey" },
                unique: true);
        }
    }
}
