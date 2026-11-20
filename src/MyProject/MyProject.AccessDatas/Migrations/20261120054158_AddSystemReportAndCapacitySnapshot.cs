using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyProject.AccessDatas.Migrations;

/// <inheritdoc />
public partial class _20261120054158_AddSystemReportAndCapacitySnapshot : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CapacitySnapshot",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                SnapshotDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                CapturedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                Category = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                UsedBytes = table.Column<long>(type: "INTEGER", nullable: true),
                TotalBytes = table.Column<long>(type: "INTEGER", nullable: true),
                FreeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                ItemCount = table.Column<long>(type: "INTEGER", nullable: true),
                Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CapacitySnapshot", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "SystemReport",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                ReportId = table.Column<Guid>(type: "TEXT", nullable: false),
                Frequency = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                PeriodStart = table.Column<DateTime>(type: "TEXT", nullable: false),
                PeriodEnd = table.Column<DateTime>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                Level = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                RedCount = table.Column<int>(type: "INTEGER", nullable: false),
                OrangeCount = table.Column<int>(type: "INTEGER", nullable: false),
                YellowCount = table.Column<int>(type: "INTEGER", nullable: false),
                Trigger = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                TriggeredByAccount = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                JobRunId = table.Column<int>(type: "INTEGER", nullable: true),
                NarrativeStatus = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                ModelName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                DeliveryStatus = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                ContentFile = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SystemReport", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_CapacitySnapshot_SnapshotDate_Category_Key",
            table: "CapacitySnapshot",
            columns: new[] { "SnapshotDate", "Category", "Key" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_SystemReport_CreatedAt",
            table: "SystemReport",
            column: "CreatedAt");

        migrationBuilder.CreateIndex(
            name: "IX_SystemReport_Frequency_PeriodStart",
            table: "SystemReport",
            columns: new[] { "Frequency", "PeriodStart" });

        migrationBuilder.CreateIndex(
            name: "IX_SystemReport_ReportId",
            table: "SystemReport",
            column: "ReportId",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "CapacitySnapshot");

        migrationBuilder.DropTable(
            name: "SystemReport");
    }
}
