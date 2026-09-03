using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MultiChannelAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStockOrderKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NormalizedCanonicalName",
                table: "Units",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "",
                collation: "Latin1_General_100_BIN2");

            // Existing Units predate the order key. Canonical names are already trimmed and
            // whitespace-collapsed when written, so folding case reproduces exactly what the domain's
            // own normalization would have produced for them.
            migrationBuilder.Sql("UPDATE Units SET NormalizedCanonicalName = LOWER(CanonicalName);");

            migrationBuilder.AlterColumn<string>(
                name: "NormalizedName",
                table: "StockEntries",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<string>(
                name: "NormalizedName",
                table: "Locations",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                collation: "Latin1_General_100_BIN2",
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NormalizedCanonicalName",
                table: "Units");

            migrationBuilder.AlterColumn<string>(
                name: "NormalizedName",
                table: "StockEntries",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200,
                oldCollation: "Latin1_General_100_BIN2");

            migrationBuilder.AlterColumn<string>(
                name: "NormalizedName",
                table: "Locations",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200,
                oldCollation: "Latin1_General_100_BIN2");
        }
    }
}
