using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyProject.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLogHashChain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Chain",
                table: "AuditLog",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "AuditLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Hash",
                table: "AuditLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KeyId",
                table: "AuditLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrevHash",
                table: "AuditLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Seq",
                table: "AuditLog",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AuditChainHead",
                columns: table => new
                {
                    Chain = table.Column<int>(type: "INTEGER", nullable: false),
                    StartSeq = table.Column<long>(type: "INTEGER", nullable: false),
                    StartPrevHash = table.Column<string>(type: "TEXT", nullable: false),
                    LastSeq = table.Column<long>(type: "INTEGER", nullable: false),
                    LastHash = table.Column<string>(type: "TEXT", nullable: false),
                    KeyId = table.Column<string>(type: "TEXT", nullable: false),
                    KeySinceSeq = table.Column<long>(type: "INTEGER", nullable: false),
                    LegacyMaxId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RebuiltAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Mac = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditChainHead", x => x.Chain);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_Chain_Seq",
                table: "AuditLog",
                columns: new[] { "Chain", "Seq" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditChainHead");

            migrationBuilder.DropIndex(
                name: "IX_AuditLog_Chain_Seq",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "Chain",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "Hash",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "KeyId",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "PrevHash",
                table: "AuditLog");

            migrationBuilder.DropColumn(
                name: "Seq",
                table: "AuditLog");
        }
    }
}
