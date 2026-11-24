using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bluestaq.Notes.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class DailyDigestOptIns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DigestPublicNotes",
                table: "users",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "DigestSharedNotes",
                table: "users",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "PublicSince",
                table: "notes",
                type: "datetime(6)",
                nullable: true);

            // Daily or weekly subscribers keep getting shared-note digests; existing public notes count as public since their last update.
            migrationBuilder.Sql("UPDATE users SET DigestSharedNotes = 1 WHERE DigestFrequency <> 0;");
            migrationBuilder.Sql("UPDATE notes SET PublicSince = UpdatedAt WHERE Visibility = 1;");

            migrationBuilder.DropColumn(
                name: "DigestFrequency",
                table: "users");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DigestFrequency",
                table: "users",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("UPDATE users SET DigestFrequency = 1 WHERE DigestSharedNotes = 1 OR DigestPublicNotes = 1;");

            migrationBuilder.DropColumn(
                name: "DigestPublicNotes",
                table: "users");

            migrationBuilder.DropColumn(
                name: "DigestSharedNotes",
                table: "users");

            migrationBuilder.DropColumn(
                name: "PublicSince",
                table: "notes");

        }
    }
}
