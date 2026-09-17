using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyProject.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddTokenUsageCost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ⚠️ EF 自動產生時把這兩欄配成「改名」（都是 TEXT），那是誤判 ——
            // EstimatedCost 是預留但從未被寫入的 decimal 費用欄，CostRateSnapshot 是費率快照 JSON，
            // 兩者毫無關係。改成明確的移除＋新增，讓 migration 讀起來就是它實際做的事。
            // 舊欄全為 NULL（無任何寫入端），移除零資料損失。
            migrationBuilder.DropColumn(
                name: "EstimatedCost",
                table: "TokenUsageLog");

            migrationBuilder.AddColumn<string>(
                name: "CostRateSnapshot",
                table: "TokenUsageLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CharacterCount",
                table: "TokenUsageLog",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CostExchangeRate",
                table: "TokenUsageLog",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CostLongContext",
                table: "TokenUsageLog",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CostPriceKey",
                table: "TokenUsageLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CostTwd",
                table: "TokenUsageLog",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "CostUsd",
                table: "TokenUsageLog",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImageCachedInputCount",
                table: "TokenUsageLog",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImageInputCount",
                table: "TokenUsageLog",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImageOutputCount",
                table: "TokenUsageLog",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CharacterCount",
                table: "TokenUsageLog");

            migrationBuilder.DropColumn(
                name: "CostExchangeRate",
                table: "TokenUsageLog");

            migrationBuilder.DropColumn(
                name: "CostLongContext",
                table: "TokenUsageLog");

            migrationBuilder.DropColumn(
                name: "CostPriceKey",
                table: "TokenUsageLog");

            migrationBuilder.DropColumn(
                name: "CostTwd",
                table: "TokenUsageLog");

            migrationBuilder.DropColumn(
                name: "CostUsd",
                table: "TokenUsageLog");

            migrationBuilder.DropColumn(
                name: "ImageCachedInputCount",
                table: "TokenUsageLog");

            migrationBuilder.DropColumn(
                name: "ImageInputCount",
                table: "TokenUsageLog");

            migrationBuilder.DropColumn(
                name: "ImageOutputCount",
                table: "TokenUsageLog");

            migrationBuilder.DropColumn(
                name: "CostRateSnapshot",
                table: "TokenUsageLog");

            migrationBuilder.AddColumn<decimal>(
                name: "EstimatedCost",
                table: "TokenUsageLog",
                type: "TEXT",
                nullable: true);
        }
    }
}
