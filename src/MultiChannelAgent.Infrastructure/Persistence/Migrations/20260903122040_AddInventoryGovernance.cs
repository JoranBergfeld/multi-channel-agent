using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MultiChannelAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryGovernance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Participants",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ConcurrencyStamp",
                table: "Memberships",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "InventoryAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ActorKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    InventoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectParticipantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OutcomeCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryAudits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Participants_IsActive",
                table: "Participants",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryAudits_ExpiresAtUtc",
                table: "InventoryAudits",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryAudits_InventoryId",
                table: "InventoryAudits",
                column: "InventoryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryAudits");

            migrationBuilder.DropIndex(
                name: "IX_Participants_IsActive",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "Participants");

            migrationBuilder.DropColumn(
                name: "ConcurrencyStamp",
                table: "Memberships");
        }
    }
}
