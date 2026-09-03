using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MultiChannelAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOutcomePayloadExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PayloadExpiresAtTicks",
                table: "Outcomes",
                type: "bigint",
                nullable: true);

            // Payloads recorded before retention existed are the oldest of all, so they expire
            // immediately: the next cleanup pass discards them rather than leaving them retained
            // forever with nothing to expire them.
            migrationBuilder.Sql("UPDATE Outcomes SET PayloadExpiresAtTicks = 0 WHERE Payload IS NOT NULL;");

            migrationBuilder.CreateIndex(
                name: "IX_Outcomes_PayloadExpiresAtTicks",
                table: "Outcomes",
                column: "PayloadExpiresAtTicks",
                filter: "PayloadExpiresAtTicks IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Outcomes_PayloadExpiresAtTicks",
                table: "Outcomes");

            migrationBuilder.DropColumn(
                name: "PayloadExpiresAtTicks",
                table: "Outcomes");
        }
    }
}
