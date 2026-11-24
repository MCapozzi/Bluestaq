using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bluestaq.Notes.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class KeepAlertsAfterNoteDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_note_alerts_notes_NoteId",
                table: "note_alerts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_note_alerts_notes_NoteId",
                table: "note_alerts",
                column: "NoteId",
                principalTable: "notes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
