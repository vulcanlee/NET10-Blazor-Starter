using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyProject.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddTeamParent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ⚠️ 不用 AddColumn＋AddForeignKey：SQLite 不能對既有資料表加外鍵，EF 會整張重建 Team
            // （暫時關閉外鍵、DROP、改名，分成好幾個交易），中途失敗會留下半套資料表。
            // SQLite 允許 ADD COLUMN 直接帶 REFERENCES（預設值為 NULL 時），效果相同而且在同一個交易內；
            // UserTeam 等參照 Team 的資料完全不動（TeamParentMigrationTests 守門）。
            migrationBuilder.Sql("ALTER TABLE \"Team\" ADD COLUMN \"ParentId\" INTEGER NULL REFERENCES \"Team\" (\"Id\") ON DELETE RESTRICT;");

            migrationBuilder.CreateIndex(
                name: "IX_Team_ParentId",
                table: "Team",
                column: "ParentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Team_Team_ParentId",
                table: "Team");

            migrationBuilder.DropIndex(
                name: "IX_Team_ParentId",
                table: "Team");

            migrationBuilder.DropColumn(
                name: "ParentId",
                table: "Team");
        }
    }
}
