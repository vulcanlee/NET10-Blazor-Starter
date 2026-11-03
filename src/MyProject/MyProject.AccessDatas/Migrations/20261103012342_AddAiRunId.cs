using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyProject.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddAiRunId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RunId",
                table: "TokenUsageLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RunId",
                table: "AiCallLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TokenUsageLog_RunId",
                table: "TokenUsageLog",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_AiCallLog_RunId",
                table: "AiCallLog",
                column: "RunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TokenUsageLog_RunId",
                table: "TokenUsageLog");

            migrationBuilder.DropIndex(
                name: "IX_AiCallLog_RunId",
                table: "AiCallLog");

            migrationBuilder.DropColumn(
                name: "RunId",
                table: "TokenUsageLog");

            migrationBuilder.DropColumn(
                name: "RunId",
                table: "AiCallLog");
        }
    }
}
