using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyProject.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class addmytask : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MyTas",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    StartDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EndDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Priority = table.Column<string>(type: "TEXT", nullable: false),
                    CompletionPercentage = table.Column<int>(type: "INTEGER", nullable: false),
                    Owner = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MyTas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MyTas_Project_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Project",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MyTas_ProjectId",
                table: "MyTas",
                column: "ProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MyTas");
        }
    }
}
