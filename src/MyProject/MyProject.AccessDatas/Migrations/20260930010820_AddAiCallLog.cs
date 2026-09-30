using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyProject.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddAiCallLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CallId",
                table: "TokenUsageLog",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiCallLog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CallId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Operation = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    Account = table.Column<string>(type: "TEXT", nullable: true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    FailureReason = table.Column<string>(type: "TEXT", nullable: true),
                    HttpStatus = table.Column<int>(type: "INTEGER", nullable: true),
                    FinishReason = table.Column<string>(type: "TEXT", nullable: true),
                    ElapsedMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestCharacters = table.Column<int>(type: "INTEGER", nullable: false),
                    ResponseCharacters = table.Column<int>(type: "INTEGER", nullable: false),
                    RelatedInfo = table.Column<string>(type: "TEXT", nullable: true),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ContentFile = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiCallLog", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TokenUsageLog_CallId",
                table: "TokenUsageLog",
                column: "CallId");

            migrationBuilder.CreateIndex(
                name: "IX_AiCallLog_CallId",
                table: "AiCallLog",
                column: "CallId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiCallLog_ConversationId",
                table: "AiCallLog",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_AiCallLog_OccurredAt",
                table: "AiCallLog",
                column: "OccurredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiCallLog");

            migrationBuilder.DropIndex(
                name: "IX_TokenUsageLog_CallId",
                table: "TokenUsageLog");

            migrationBuilder.DropColumn(
                name: "CallId",
                table: "TokenUsageLog");
        }
    }
}
