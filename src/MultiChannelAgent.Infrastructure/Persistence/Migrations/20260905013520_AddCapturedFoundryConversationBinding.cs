using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MultiChannelAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCapturedFoundryConversationBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FoundryConversationGeneration",
                table: "InboxEntries",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FoundryConversationId",
                table: "InboxEntries",
                type: "uniqueidentifier",
                nullable: true);

            // Turns accepted before this column existed predate any conversation reset by definition,
            // so the binding their conversation currently holds is the one they belong to. Leaving
            // them null would be correct too - the factory falls back to exactly this - but filling
            // them in keeps the fallback a genuinely dead path for anything the deploy left behind.
            //
            // EXEC is what makes this safe on the generated-script path: that script puts the two
            // ALTER TABLEs and this statement in a single batch, and SQL Server binds column names
            // for an already-existing table when it compiles the batch, so naming the brand-new
            // columns directly here would fail with "Invalid column name" before touching a row.
            // Deferring compilation to execution time leaves the runtime path unchanged.
            migrationBuilder.Sql(
                """
                EXEC(N'
                    UPDATE i
                    SET i.FoundryConversationId = b.FoundryConversationId,
                        i.FoundryConversationGeneration = b.Generation
                    FROM InboxEntries AS i
                    INNER JOIN FoundryConversationBindings AS b
                        ON b.ParticipantId = i.ParticipantId
                        AND b.ChannelConversationId = i.ChannelConversationId;
                ');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FoundryConversationGeneration",
                table: "InboxEntries");

            migrationBuilder.DropColumn(
                name: "FoundryConversationId",
                table: "InboxEntries");
        }
    }
}
