using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyProject.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddAiAgentConversation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiAgentConversation",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Account = table.Column<string>(type: "TEXT", nullable: false),
                    AgentName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    HasPendingApproval = table.Column<bool>(type: "INTEGER", nullable: false),
                    ContentFile = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiAgentConversation", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiAgentConversation_ConversationId",
                table: "AiAgentConversation",
                column: "ConversationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiAgentConversation_UpdatedAt",
                table: "AiAgentConversation",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AiAgentConversation_UserId_AgentName_UpdatedAt",
                table: "AiAgentConversation",
                columns: new[] { "UserId", "AgentName", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiAgentConversation");
        }
    }
}
