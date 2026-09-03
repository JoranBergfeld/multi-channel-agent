using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MultiChannelAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInboundTurnChannelContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Capabilities",
                table: "InboxEntries",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Channel",
                table: "InboxEntries",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PrincipalKind",
                table: "InboxEntries",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PrincipalSubject",
                table: "InboxEntries",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PrincipalTenantId",
                table: "InboxEntries",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InboxContentParts",
                columns: table => new
                {
                    TurnId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Provenance = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", maxLength: 32768, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxContentParts", x => new { x.TurnId, x.Order });
                    table.ForeignKey(
                        name: "FK_InboxContentParts_InboxEntries_TurnId",
                        column: x => x.TurnId,
                        principalTable: "InboxEntries",
                        principalColumn: "TurnId",
                        onDelete: ReferentialAction.Cascade);
                });

            // Existing Turns predate the completed inbound contract. Everything accepted so far came
            // from the signed-in web channel and was authored directly by its authenticated
            // Participant, so their content becomes exactly one direct part, and their principal is
            // that Participant's own Entra identity - the same values the web adapter records today.
            // This runs before the old column is dropped, so no accepted content is ever lost.
            migrationBuilder.Sql(
                """
                INSERT INTO InboxContentParts (TurnId, [Order], Provenance, Text)
                SELECT TurnId, 1, 'Direct', ContentText FROM InboxEntries;
                """);

            migrationBuilder.Sql(
                """
                UPDATE InboxEntries
                SET Channel = 'web',
                    PrincipalKind = 'EntraUser',
                    -- LOWER, because SQL Server renders a uniqueidentifier in uppercase hex while
                    -- .NET renders the same Guid in lowercase: without it, migrated rows would carry
                    -- a subject that never equals the one the application writes for that very same
                    -- Participant.
                    PrincipalSubject = LOWER(CONVERT(nvarchar(36), ParticipantId)),
                    Capabilities = 7;
                """);

            migrationBuilder.DropColumn(
                name: "ContentText",
                table: "InboxEntries");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentText",
                table: "InboxEntries",
                type: "nvarchar(max)",
                maxLength: 32768,
                nullable: false,
                defaultValue: "");

            // Restore the single flat content column from the direct parts, so rolling back keeps
            // every accepted Turn's content rather than silently emptying it.
            migrationBuilder.Sql(
                """
                UPDATE InboxEntries
                SET ContentText = COALESCE((
                    SELECT TOP 1 Text
                    FROM InboxContentParts
                    WHERE InboxContentParts.TurnId = InboxEntries.TurnId AND Provenance = 'Direct'
                    ORDER BY [Order]), '');
                """);

            migrationBuilder.DropTable(
                name: "InboxContentParts");

            migrationBuilder.DropColumn(
                name: "Capabilities",
                table: "InboxEntries");

            migrationBuilder.DropColumn(
                name: "Channel",
                table: "InboxEntries");

            migrationBuilder.DropColumn(
                name: "PrincipalKind",
                table: "InboxEntries");

            migrationBuilder.DropColumn(
                name: "PrincipalSubject",
                table: "InboxEntries");

            migrationBuilder.DropColumn(
                name: "PrincipalTenantId",
                table: "InboxEntries");
        }
    }
}
