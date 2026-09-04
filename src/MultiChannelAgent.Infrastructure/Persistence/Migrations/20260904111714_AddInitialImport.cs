using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MultiChannelAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInitialImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImportOperations",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InventoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedEntryCount = table.Column<int>(type: "int", nullable: false),
                    AppliedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportOperations", x => x.OperationId);
                    table.ForeignKey(
                        name: "FK_ImportOperations_Inventories_InventoryId",
                        column: x => x.InventoryId,
                        principalTable: "Inventories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ImportOperations_Participants_ActorId",
                        column: x => x.ActorId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ImportProposals",
                columns: table => new
                {
                    ProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ParticipantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InventoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileDigest = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    EntriesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExpectedStockEntryCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAtTicks = table.Column<long>(type: "bigint", nullable: false),
                    SettledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SettledAtTicks = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportProposals", x => x.ProposalId);
                    table.ForeignKey(
                        name: "FK_ImportProposals_Inventories_InventoryId",
                        column: x => x.InventoryId,
                        principalTable: "Inventories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ImportProposals_Participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalTable: "Participants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ImportUploads",
                columns: table => new
                {
                    ProposalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportUploads", x => x.ProposalId);
                    table.ForeignKey(
                        name: "FK_ImportUploads_ImportProposals_ProposalId",
                        column: x => x.ProposalId,
                        principalTable: "ImportProposals",
                        principalColumn: "ProposalId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportOperations_ActorId",
                table: "ImportOperations",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportOperations_InventoryId",
                table: "ImportOperations",
                column: "InventoryId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportOperations_ProposalId",
                table: "ImportOperations",
                column: "ProposalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImportProposals_InventoryId",
                table: "ImportProposals",
                column: "InventoryId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportProposals_ParticipantId_InventoryId",
                table: "ImportProposals",
                columns: new[] { "ParticipantId", "InventoryId" },
                unique: true,
                filter: "Status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_ImportProposals_SettledAtTicks",
                table: "ImportProposals",
                column: "SettledAtTicks");

            migrationBuilder.CreateIndex(
                name: "IX_ImportProposals_Status_ExpiresAtTicks",
                table: "ImportProposals",
                columns: new[] { "Status", "ExpiresAtTicks" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportProposals_TokenHash",
                table: "ImportProposals",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportOperations");

            migrationBuilder.DropTable(
                name: "ImportUploads");

            migrationBuilder.DropTable(
                name: "ImportProposals");
        }
    }
}
