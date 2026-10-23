using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyProject.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationDispatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationDispatch",
                columns: table => new
                {
                    SourceKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    RecipientUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationDispatch", x => new { x.SourceKey, x.RecipientUserId });
                    table.ForeignKey(
                        name: "FK_NotificationDispatch_MyUser_RecipientUserId",
                        column: x => x.RecipientUserId,
                        principalTable: "MyUser",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDispatch_RecipientUserId",
                table: "NotificationDispatch",
                column: "RecipientUserId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationDispatch_SentAtUtc",
                table: "NotificationDispatch",
                column: "SentAtUtc");

            // 升級前已發出、還留著的帶鍵通知補成去重紀錄，升級後同一個鍵不會再發一次。
            migrationBuilder.Sql(
                """
                INSERT OR IGNORE INTO "NotificationDispatch" ("SourceKey", "RecipientUserId", "SentAtUtc")
                SELECT "SourceKey", "RecipientUserId", MIN("CreatedAtUtc")
                FROM "Notification"
                WHERE "SourceKey" IS NOT NULL
                GROUP BY "SourceKey", "RecipientUserId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationDispatch");
        }
    }
}
