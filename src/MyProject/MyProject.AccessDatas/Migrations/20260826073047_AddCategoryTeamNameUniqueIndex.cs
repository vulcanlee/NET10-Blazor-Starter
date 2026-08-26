using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyProject.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddCategoryTeamNameUniqueIndex : Migration
    {
        /// <summary>
        /// SQLite 的 trim(X) 只會移除半形空格，必須用第二參數才能涵蓋 tab／換行／全形空白 U+3000，
        /// 盡量貼近應用層 NameNormalizer 使用的 .NET String.Trim() 語意。
        /// </summary>
        private const string Whitespace = "' ' || char(9) || char(10) || char(13) || char(12288)";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ⚠️ 建立唯一索引之前必須先清理既有資料。
            // Program.cs 啟動時無條件呼叫 Database.Migrate()，只要資料表裡有一筆重複，
            // 索引就建不起來、migration 回滾、__EFMigrationsHistory 不會寫入 ——
            // 結果是每次啟動都重試、每次都失敗，服務永遠起不來。

            // (1) 團隊代號：空白一律歸一成 NULL。必須排在去重之前 ——
            //     SQLite 的唯一索引視 NULL 互不相等，但空字串彼此相同，
            //     若放著 "" 不管，第二筆「未填代號」的團隊就會撞到索引。
            migrationBuilder.Sql($"UPDATE Team SET Code = NULL WHERE Code IS NOT NULL AND trim(Code, {Whitespace}) = '';");

            // (2) 去除名稱／代號的前後空白。這正是先前「檢查時 Trim、寫入時不 Trim」
            //     所留下的髒資料（例如「技術文件 」與「技術文件」並存）。
            migrationBuilder.Sql($"UPDATE Category SET Name = trim(Name, {Whitespace}) WHERE Name <> trim(Name, {Whitespace});");
            migrationBuilder.Sql($"UPDATE Team SET Name = trim(Name, {Whitespace}) WHERE Name <> trim(Name, {Whitespace});");
            migrationBuilder.Sql($"UPDATE Team SET Code = trim(Code, {Whitespace}) WHERE Code IS NOT NULL AND Code <> trim(Code, {Whitespace});");

            // (3) 去重：同名者只有 Id 最小的保留原名，其餘加上可追溯的尾碼。
            //     尾碼用 Id 而非流水號 —— Id 唯一，所以改名結果保證不會再撞。
            //     比對刻意用 lower()（不分大小寫），比索引的 BINARY 定序更嚴格：
            //     只滿足索引的話，「Report」與「report」會同時留下，但服務層視為重複，
            //     管理員之後連編輯都會被擋。資料必須是「應用層也認為合法」的狀態。
            //     注意：這段對「同名中 Id 最小的那筆永不改名」成立，
            //     因此不受 SQLite 逐列更新可見性的影響。
            migrationBuilder.Sql("""
                UPDATE Category SET Name = Name || ' (重複-' || Id || ')'
                WHERE EXISTS (
                    SELECT 1 FROM Category dup
                    WHERE lower(dup.Name) = lower(Category.Name) AND dup.Id < Category.Id);
                """);

            migrationBuilder.Sql("""
                UPDATE Team SET Name = Name || ' (重複-' || Id || ')'
                WHERE EXISTS (
                    SELECT 1 FROM Team dup
                    WHERE lower(dup.Name) = lower(Team.Name) AND dup.Id < Team.Id);
                """);

            migrationBuilder.Sql("""
                UPDATE Team SET Code = Code || ' (重複-' || Id || ')'
                WHERE Code IS NOT NULL AND EXISTS (
                    SELECT 1 FROM Team dup
                    WHERE dup.Code IS NOT NULL AND lower(dup.Code) = lower(Team.Code) AND dup.Id < Team.Id);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Team_Code",
                table: "Team",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Team_Name",
                table: "Team",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Category_Name",
                table: "Category",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        /// <remarks>
        /// 只移除索引。Up() 中的資料清理（去空白、空代號歸 NULL、重複改名）**不可逆**，
        /// 降版不會把名稱改回去。
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Team_Code",
                table: "Team");

            migrationBuilder.DropIndex(
                name: "IX_Team_Name",
                table: "Team");

            migrationBuilder.DropIndex(
                name: "IX_Category_Name",
                table: "Category");
        }
    }
}
