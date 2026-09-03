using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MultiChannelAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInboxReceivedAtTicks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InboxEntries_Status_ReceivedAt",
                table: "InboxEntries");

            migrationBuilder.AddColumn<long>(
                name: "ReceivedAtTicks",
                table: "InboxEntries",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Existing Turns keep the instant they were actually received: leaving them at 0 would
            // make every one of them look older than everything else forever. Ticks are counted from
            // 0001-01-01 UTC in two steps (whole days, then the time of day) because counting the
            // whole span in nanoseconds overflows a bigint.
            migrationBuilder.Sql(
                """
                UPDATE InboxEntries
                SET ReceivedAtTicks =
                    DATEDIFF_BIG(day, CONVERT(date, '0001-01-01'), CONVERT(date, SWITCHOFFSET(ReceivedAt, 0)))
                        * CAST(864000000000 AS bigint)
                    + DATEDIFF_BIG(nanosecond, CONVERT(time, '00:00:00'), CONVERT(time, SWITCHOFFSET(ReceivedAt, 0))) / 100;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_InboxEntries_Status_ReceivedAtTicks",
                table: "InboxEntries",
                columns: new[] { "Status", "ReceivedAtTicks" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InboxEntries_Status_ReceivedAtTicks",
                table: "InboxEntries");

            migrationBuilder.DropColumn(
                name: "ReceivedAtTicks",
                table: "InboxEntries");

            migrationBuilder.CreateIndex(
                name: "IX_InboxEntries_Status_ReceivedAt",
                table: "InboxEntries",
                columns: new[] { "Status", "ReceivedAt" });
        }
    }
}
