using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bluestaq.Notes.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNoteAuditFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CreatedByUserId",
                table: "notes",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "UpdatedByUserId",
                table: "notes",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Existing notes could only ever have been written by their owner.
            migrationBuilder.Sql("UPDATE notes SET CreatedByUserId = OwnerId, UpdatedByUserId = OwnerId;");

            migrationBuilder.CreateIndex(
                name: "IX_notes_OwnerId_CreatedAt",
                table: "notes",
                columns: new[] { "OwnerId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_notes_OwnerId_CreatedAt",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "UpdatedByUserId",
                table: "notes");
        }
    }
}
