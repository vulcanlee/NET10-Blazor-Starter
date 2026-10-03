using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyProject.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddConcurrencyStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConcurrencyStamp",
                table: "Team",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ConcurrencyStamp",
                table: "RoleView",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ConcurrencyStamp",
                table: "Project",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ConcurrencyStamp",
                table: "MyUser",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ConcurrencyStamp",
                table: "Category",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            // 既有的列各自給一個隨機版本號（32 個十六進位字元，與 Guid.ToString("N") 同格式）。
            // 不填也能運作（空字串一樣能比對），但每列不同的值才符合「版本號」的語意，除錯時也看得出差別。
            foreach (var table in new[] { "Team", "RoleView", "Project", "MyUser", "Category" })
            {
                migrationBuilder.Sql($"UPDATE \"{table}\" SET \"ConcurrencyStamp\" = lower(hex(randomblob(16)));");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConcurrencyStamp",
                table: "Team");

            migrationBuilder.DropColumn(
                name: "ConcurrencyStamp",
                table: "RoleView");

            migrationBuilder.DropColumn(
                name: "ConcurrencyStamp",
                table: "Project");

            migrationBuilder.DropColumn(
                name: "ConcurrencyStamp",
                table: "MyUser");

            migrationBuilder.DropColumn(
                name: "ConcurrencyStamp",
                table: "Category");
        }
    }
}
