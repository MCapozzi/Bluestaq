using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bluestaq.Notes.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNoteSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Visibility",
                table: "notes",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "note_shares",
                columns: table => new
                {
                    NoteId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    SharedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_note_shares", x => new { x.NoteId, x.UserId });
                    table.ForeignKey(
                        name: "FK_note_shares_notes_NoteId",
                        column: x => x.NoteId,
                        principalTable: "notes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_note_shares_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_notes_Visibility",
                table: "notes",
                column: "Visibility");

            migrationBuilder.CreateIndex(
                name: "IX_note_shares_UserId",
                table: "note_shares",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "note_shares");

            migrationBuilder.DropIndex(
                name: "IX_notes_Visibility",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "Visibility",
                table: "notes");
        }
    }
}
