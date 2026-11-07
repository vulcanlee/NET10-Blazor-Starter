using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyProject.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminPatrolReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminPatrolReport",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ReportId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    AttentionSections = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    TriggeredByAccount = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    JobRunId = table.Column<int>(type: "INTEGER", nullable: false),
                    ModelName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    IsTruncated = table.Column<bool>(type: "INTEGER", nullable: false),
                    ContentFile = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminPatrolReport", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminPatrolReport_CreatedAt",
                table: "AdminPatrolReport",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AdminPatrolReport_ReportId",
                table: "AdminPatrolReport",
                column: "ReportId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminPatrolReport");
        }
    }
}
