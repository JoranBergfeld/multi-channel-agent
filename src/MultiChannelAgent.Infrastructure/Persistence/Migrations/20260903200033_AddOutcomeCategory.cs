using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MultiChannelAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOutcomeCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "Outcomes",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            // Existing rows predate the semantic category, so classify them from the only signal they
            // carry: a recorded failure was a system failure, and everything else was an answer.
            migrationBuilder.Sql(
                "UPDATE Outcomes SET Category = CASE WHEN Status = 'Failed' THEN 'TransientFailure' ELSE 'Completed' END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Category",
                table: "Outcomes");
        }
    }
}
