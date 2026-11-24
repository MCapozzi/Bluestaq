using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bluestaq.Notes.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrackContentEdits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ContentUpdatedAt",
                table: "notes",
                type: "datetime(6)",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<long>(
                name: "ContentUpdatedByUserId",
                table: "notes",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Existing notes: their last edit is the best available record of their last content change.
            migrationBuilder.Sql("UPDATE notes SET ContentUpdatedAt = UpdatedAt, ContentUpdatedByUserId = UpdatedByUserId;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContentUpdatedAt",
                table: "notes");

            migrationBuilder.DropColumn(
                name: "ContentUpdatedByUserId",
                table: "notes");
        }
    }
}
