using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MultiChannelAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventoryVersions",
                columns: table => new
                {
                    InventoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryVersions", x => x.InventoryId);
                });

            // Every Inventory that already exists gets its starting version here rather than lazily,
            // so the bump this migration enables is always an update of a row that exists - and so a
            // Participant watching an Inventory created before this deploy is told about its very
            // next change, not its second one. This backfill, and the save-time seeding of new
            // Inventories, are what keep the table consistent with Inventories in the absence of a
            // foreign key; the store's fallback insertion is the third line of defence, not the first.
            migrationBuilder.Sql(
                """
                INSERT INTO InventoryVersions (InventoryId, Version)
                SELECT i.Id, 0
                FROM Inventories AS i
                WHERE NOT EXISTS (SELECT 1 FROM InventoryVersions AS v WHERE v.InventoryId = i.Id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryVersions");
        }
    }
}
