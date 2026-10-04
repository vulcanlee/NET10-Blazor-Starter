using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyProject.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddPromptTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PromptTemplate",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TemplateKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Content = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedByAccount = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromptTemplate", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PromptTemplate_TemplateKey",
                table: "PromptTemplate",
                column: "TemplateKey",
                unique: true,
                filter: "\"IsActive\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_PromptTemplate_TemplateKey_Version",
                table: "PromptTemplate",
                columns: new[] { "TemplateKey", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PromptTemplate");
        }
    }
}
