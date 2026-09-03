using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MultiChannelAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScopeInboxNativeMessageKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InboxEntries_NativeMessageId",
                table: "InboxEntries");

            migrationBuilder.CreateIndex(
                name: "IX_InboxEntries_ParticipantId_ChannelConversationId_NativeMessageId",
                table: "InboxEntries",
                columns: new[] { "ParticipantId", "ChannelConversationId", "NativeMessageId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InboxEntries_ParticipantId_ChannelConversationId_NativeMessageId",
                table: "InboxEntries");

            migrationBuilder.CreateIndex(
                name: "IX_InboxEntries_NativeMessageId",
                table: "InboxEntries",
                column: "NativeMessageId",
                unique: true);
        }
    }
}
