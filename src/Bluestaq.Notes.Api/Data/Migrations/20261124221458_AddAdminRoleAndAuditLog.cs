using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace Bluestaq.Notes.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminRoleAndAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Role",
                table: "users",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "admin_audit_log",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    OccurredAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    AdminUserId = table.Column<long>(type: "bigint", nullable: false),
                    AdminUsername = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false),
                    Action = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    HttpMethod = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    Path = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false),
                    QueryString = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true),
                    TargetNoteId = table.Column<long>(type: "bigint", nullable: true),
                    TargetUserId = table.Column<long>(type: "bigint", nullable: true),
                    StatusCode = table.Column<int>(type: "int", nullable: false),
                    ClientIp = table.Column<string>(type: "varchar(45)", maxLength: 45, nullable: true),
                    TraceId = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_audit_log", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_admin_audit_log_AdminUserId_OccurredAt",
                table: "admin_audit_log",
                columns: new[] { "AdminUserId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_admin_audit_log_TargetNoteId",
                table: "admin_audit_log",
                column: "TargetNoteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_audit_log");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "users");
        }
    }
}
