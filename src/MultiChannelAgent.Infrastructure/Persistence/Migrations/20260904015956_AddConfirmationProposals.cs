using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MultiChannelAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConfirmationProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConfirmationProposals",
                columns: table => new
                {
                    ProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ParticipantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChannelConversationId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    InventoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProposedInTurnId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ChangesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExpectedVersionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExpectedAbsencesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtTicks = table.Column<long>(type: "bigint", nullable: false),
                    SettledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SettledAtTicks = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfirmationProposals", x => x.ProposalId);
                    table.ForeignKey(
                        name: "FK_ConfirmationProposals_Inventories_InventoryId",
                        column: x => x.InventoryId,
                        principalTable: "Inventories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConfirmationProposals_InventoryId",
                table: "ConfirmationProposals",
                column: "InventoryId");

            migrationBuilder.CreateIndex(
                name: "IX_ConfirmationProposals_ParticipantId_ChannelConversationId",
                table: "ConfirmationProposals",
                columns: new[] { "ParticipantId", "ChannelConversationId" },
                unique: true,
                filter: "Status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_ConfirmationProposals_SettledAtTicks",
                table: "ConfirmationProposals",
                column: "SettledAtTicks");

            migrationBuilder.CreateIndex(
                name: "IX_ConfirmationProposals_Status_ExpiresAtTicks",
                table: "ConfirmationProposals",
                columns: new[] { "Status", "ExpiresAtTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_ConfirmationProposals_TokenHash",
                table: "ConfirmationProposals",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConfirmationProposals");
        }
    }
}
