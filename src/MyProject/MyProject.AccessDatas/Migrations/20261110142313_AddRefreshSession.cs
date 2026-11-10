using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyProject.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddRefreshSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RefreshSession",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MyUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    FamilyId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SecurityStamp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IsPersistent = table.Column<bool>(type: "INTEGER", nullable: false),
                    ClientKind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    LoginSessionId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    DeviceLabel = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AbsoluteExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedReason = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    ReplacedById = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshSession", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RefreshSession_MyUser_MyUserId",
                        column: x => x.MyUserId,
                        principalTable: "MyUser",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RefreshSession_ExpiresAtUtc",
                table: "RefreshSession",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshSession_FamilyId",
                table: "RefreshSession",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshSession_LoginSessionId",
                table: "RefreshSession",
                column: "LoginSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshSession_MyUserId",
                table: "RefreshSession",
                column: "MyUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshSession_TokenHash",
                table: "RefreshSession",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RefreshSession");
        }
    }
}
