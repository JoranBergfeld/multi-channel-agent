using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MultiChannelAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamsConversationReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var shortText = ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite" ? "TEXT" : "nvarchar(256)";
            var longText = ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite" ? "TEXT" : "nvarchar(max)";
            var timestamp = ActiveProvider == "Microsoft.EntityFrameworkCore.Sqlite" ? "TEXT" : "datetimeoffset";

            migrationBuilder.CreateTable(
                name: "TeamsConversationReferences",
                columns: table => new
                {
                    ChannelConversationId = table.Column<string>(type: shortText, maxLength: 256, nullable: false),
                    SerializedReference = table.Column<string>(type: longText, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: timestamp, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamsConversationReferences", x => x.ChannelConversationId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TeamsConversationReferences");
        }
    }
}
