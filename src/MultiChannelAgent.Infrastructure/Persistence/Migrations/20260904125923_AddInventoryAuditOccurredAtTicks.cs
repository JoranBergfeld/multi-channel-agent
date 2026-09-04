using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MultiChannelAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryAuditOccurredAtTicks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InventoryAudits_ExpiresAtUtc",
                table: "InventoryAudits");

            migrationBuilder.AddColumn<long>(
                name: "OccurredAtUtcTicks",
                table: "InventoryAudits",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Existing facts keep the instant they actually occurred. Leaving them at 0 would put
            // every one of them ninety days past retention, and the first sweep after this migration
            // would delete the whole audit history rather than the part that had aged out. Ticks are
            // counted from 0001-01-01 UTC in two steps (whole days, then the time of day) because
            // counting the whole span in nanoseconds overflows a bigint - the same arithmetic
            // AddInboxReceivedAtTicks used for the same reason.
            migrationBuilder.Sql(
                """
                UPDATE InventoryAudits
                SET OccurredAtUtcTicks =
                    DATEDIFF_BIG(day, CONVERT(date, '0001-01-01'), CONVERT(date, SWITCHOFFSET(OccurredAtUtc, 0)))
                        * CAST(864000000000 AS bigint)
                    + DATEDIFF_BIG(nanosecond, CONVERT(time, '00:00:00'), CONVERT(time, SWITCHOFFSET(OccurredAtUtc, 0))) / 100;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryAudits_OccurredAtUtcTicks",
                table: "InventoryAudits",
                column: "OccurredAtUtcTicks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InventoryAudits_OccurredAtUtcTicks",
                table: "InventoryAudits");

            migrationBuilder.DropColumn(
                name: "OccurredAtUtcTicks",
                table: "InventoryAudits");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryAudits_ExpiresAtUtc",
                table: "InventoryAudits",
                column: "ExpiresAtUtc");
        }
    }
}
