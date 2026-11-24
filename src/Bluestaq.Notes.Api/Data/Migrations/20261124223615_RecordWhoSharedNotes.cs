using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bluestaq.Notes.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecordWhoSharedNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SharedByUserId",
                table: "note_shares",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SharedByUserId",
                table: "note_shares");
        }
    }
}
