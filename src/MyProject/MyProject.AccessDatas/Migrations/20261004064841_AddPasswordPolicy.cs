using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyProject.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddPasswordPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MustChangePassword",
                table: "MyUser",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "PasswordChangedAtUtc",
                table: "MyUser",
                type: "TEXT",
                nullable: true);

            // 已有本機密碼的帳號，到期天數從升級這一刻起算（沒有更早的設定時間可用）；只用 Google 登入的帳號維持 null。
            // 格式與 EF Core SQLite 寫入 DateTime 的格式相容（yyyy-MM-dd HH:mm:ss.fff）。
            migrationBuilder.Sql("UPDATE \"MyUser\" SET \"PasswordChangedAtUtc\" = strftime('%Y-%m-%d %H:%M:%f', 'now') WHERE \"Password\" <> '';");

            migrationBuilder.CreateTable(
                name: "PasswordHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MyUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasswordHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PasswordHistory_MyUser_MyUserId",
                        column: x => x.MyUserId,
                        principalTable: "MyUser",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PasswordHistory_MyUserId_CreatedAtUtc",
                table: "PasswordHistory",
                columns: new[] { "MyUserId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PasswordHistory");

            migrationBuilder.DropColumn(
                name: "MustChangePassword",
                table: "MyUser");

            migrationBuilder.DropColumn(
                name: "PasswordChangedAtUtc",
                table: "MyUser");
        }
    }
}
