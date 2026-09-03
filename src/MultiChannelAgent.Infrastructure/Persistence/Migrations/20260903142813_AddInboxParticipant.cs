using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MultiChannelAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInboxParticipant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParticipantId",
                table: "InboxEntries",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_InboxEntries_ChannelConversationId_Status_ReceivedAt",
                table: "InboxEntries",
                columns: new[] { "ChannelConversationId", "Status", "ReceivedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InboxEntries_ChannelConversationId_Status_ReceivedAt",
                table: "InboxEntries");

            migrationBuilder.DropColumn(
                name: "ParticipantId",
                table: "InboxEntries");
        }
    }
}
