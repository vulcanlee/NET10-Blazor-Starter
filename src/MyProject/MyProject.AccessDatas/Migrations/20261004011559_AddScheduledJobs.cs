using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyProject.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobRun",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TriggeredByAccount = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ScheduledForUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: true),
                    TraceId = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobRun", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledJobState",
                columns: table => new
                {
                    JobName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastScheduledForUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledJobState", x => x.JobName);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobRun_JobName_StartedAtUtc",
                table: "JobRun",
                columns: new[] { "JobName", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_JobRun_StartedAtUtc",
                table: "JobRun",
                column: "StartedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobRun");

            migrationBuilder.DropTable(
                name: "ScheduledJobState");
        }
    }
}
