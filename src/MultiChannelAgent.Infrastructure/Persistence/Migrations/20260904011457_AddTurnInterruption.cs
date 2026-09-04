using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MultiChannelAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTurnInterruption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "WasInterrupted",
                table: "InboxEntries",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WasInterrupted",
                table: "InboxEntries");
        }
    }
}
