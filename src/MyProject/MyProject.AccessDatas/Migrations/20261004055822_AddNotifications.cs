using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyProject.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Announcement",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Content = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    StartAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TargetKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TargetId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedByAccount = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Announcement", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notification",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RecipientUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Body = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Link = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SourceKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReadAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notification", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Notification_MyUser_RecipientUserId",
                        column: x => x.RecipientUserId,
                        principalTable: "MyUser",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AnnouncementDismissal",
                columns: table => new
                {
                    AnnouncementId = table.Column<int>(type: "INTEGER", nullable: false),
                    MyUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    DismissedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnnouncementDismissal", x => new { x.AnnouncementId, x.MyUserId });
                    table.ForeignKey(
                        name: "FK_AnnouncementDismissal_Announcement_AnnouncementId",
                        column: x => x.AnnouncementId,
                        principalTable: "Announcement",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AnnouncementDismissal_MyUser_MyUserId",
                        column: x => x.MyUserId,
                        principalTable: "MyUser",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnnouncementDismissal_MyUserId",
                table: "AnnouncementDismissal",
                column: "MyUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Notification_CreatedAtUtc",
                table: "Notification",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Notification_RecipientUserId_CreatedAtUtc",
                table: "Notification",
                columns: new[] { "RecipientUserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Notification_RecipientUserId_ReadAtUtc",
                table: "Notification",
                columns: new[] { "RecipientUserId", "ReadAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Notification_RecipientUserId_SourceKey",
                table: "Notification",
                columns: new[] { "RecipientUserId", "SourceKey" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnnouncementDismissal");

            migrationBuilder.DropTable(
                name: "Notification");

            migrationBuilder.DropTable(
                name: "Announcement");
        }
    }
}
