using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyProject.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityStamp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SecurityStamp",
                table: "MyUser",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // 既有帳號各給一個隨機的工作階段版本。資料庫預設值留空字串：重疊回收時舊版程式新增的列會是空字串，
            // 程式把它當成「尚未設定」，登入時補上（比對時一律不符）。
            migrationBuilder.Sql("UPDATE \"MyUser\" SET \"SecurityStamp\" = lower(hex(randomblob(16))) WHERE \"SecurityStamp\" = '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SecurityStamp",
                table: "MyUser");
        }
    }
}
