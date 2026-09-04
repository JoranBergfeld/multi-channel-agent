using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MultiChannelAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTurnProgressEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TurnProgressEvents",
                columns: table => new
                {
                    TurnId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtTicks = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TurnProgressEvents", x => new { x.TurnId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_TurnProgressEvents_InboxEntries_TurnId",
                        column: x => x.TurnId,
                        principalTable: "InboxEntries",
                        principalColumn: "TurnId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TurnProgressEvents_ExpiresAtTicks",
                table: "TurnProgressEvents",
                column: "ExpiresAtTicks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TurnProgressEvents");
        }
    }
}
