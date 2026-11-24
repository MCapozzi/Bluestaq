using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace Bluestaq.Notes.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNoteAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "note_alerts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    RecipientUserId = table.Column<long>(type: "bigint", nullable: false),
                    NoteId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    ActorUserId = table.Column<long>(type: "bigint", nullable: false),
                    FirstOccurredAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    LastOccurredAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Occurrences = table.Column<int>(type: "int", nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_note_alerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_note_alerts_notes_NoteId",
                        column: x => x.NoteId,
                        principalTable: "notes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_note_alerts_users_RecipientUserId",
                        column: x => x.RecipientUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_note_alerts_NoteId",
                table: "note_alerts",
                column: "NoteId");

            migrationBuilder.CreateIndex(
                name: "IX_note_alerts_RecipientUserId_DeliveredAt",
                table: "note_alerts",
                columns: new[] { "RecipientUserId", "DeliveredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "note_alerts");
        }
    }
}
