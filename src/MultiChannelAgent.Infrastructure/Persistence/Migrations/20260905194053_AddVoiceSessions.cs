using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MultiChannelAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVoiceSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VoiceSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParticipantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChannelConversationId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ControlSessionId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    OwnerInstanceId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    OccupiesSlot = table.Column<bool>(type: "bit", nullable: false),
                    StartedAtTicks = table.Column<long>(type: "bigint", nullable: false),
                    LastHeartbeatAtTicks = table.Column<long>(type: "bigint", nullable: false),
                    EndedAtTicks = table.Column<long>(type: "bigint", nullable: true),
                    ExpiresAtTicks = table.Column<long>(type: "bigint", nullable: false),
                    WarningAtTicks = table.Column<long>(type: "bigint", nullable: false),
                    IdleExpiresAtTicks = table.Column<long>(type: "bigint", nullable: false),
                    WarningIssued = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoiceSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VoiceSessions_OccupiesSlot",
                table: "VoiceSessions",
                column: "OccupiesSlot");

            migrationBuilder.CreateIndex(
                name: "IX_VoiceSessions_Owner_Status",
                table: "VoiceSessions",
                columns: new[] { "OwnerInstanceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_VoiceSessions_ParticipantId_OccupiesSlot",
                table: "VoiceSessions",
                column: "ParticipantId",
                unique: true,
                filter: "[OccupiesSlot] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_VoiceSessions_Status_Expiry",
                table: "VoiceSessions",
                columns: new[] { "Status", "ExpiresAtTicks", "IdleExpiresAtTicks" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VoiceSessions");
        }
    }
}
