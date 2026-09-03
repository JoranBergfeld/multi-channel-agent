using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MultiChannelAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInboxConversationSequence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InboxEntries_ChannelConversationId_Status_ReceivedAt",
                table: "InboxEntries");

            migrationBuilder.AddColumn<long>(
                name: "ConversationSequence",
                table: "InboxEntries",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Existing rows all default to 0, which the unique index below would reject for any
            // conversation holding more than one Turn. Backfill them with a deterministic order
            // derived from the only ordering information those rows carry - received time, with the
            // Turn identity as a stable tie-break for rows that share an instant - so the durable
            // order is total from the moment the constraint starts enforcing it.
            migrationBuilder.Sql("""
                WITH Ordered AS (
                    SELECT
                        TurnId,
                        ROW_NUMBER() OVER (
                            PARTITION BY ChannelConversationId
                            ORDER BY ReceivedAt, TurnId) AS NewSequence
                    FROM InboxEntries)
                UPDATE InboxEntries
                SET ConversationSequence = Ordered.NewSequence
                FROM InboxEntries
                INNER JOIN Ordered ON Ordered.TurnId = InboxEntries.TurnId;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_InboxEntries_ChannelConversationId_ConversationSequence",
                table: "InboxEntries",
                columns: new[] { "ChannelConversationId", "ConversationSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InboxEntries_ChannelConversationId_Status_ConversationSequence",
                table: "InboxEntries",
                columns: new[] { "ChannelConversationId", "Status", "ConversationSequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InboxEntries_ChannelConversationId_ConversationSequence",
                table: "InboxEntries");

            migrationBuilder.DropIndex(
                name: "IX_InboxEntries_ChannelConversationId_Status_ConversationSequence",
                table: "InboxEntries");

            migrationBuilder.DropColumn(
                name: "ConversationSequence",
                table: "InboxEntries");

            migrationBuilder.CreateIndex(
                name: "IX_InboxEntries_ChannelConversationId_Status_ReceivedAt",
                table: "InboxEntries",
                columns: new[] { "ChannelConversationId", "Status", "ReceivedAt" });
        }
    }
}
