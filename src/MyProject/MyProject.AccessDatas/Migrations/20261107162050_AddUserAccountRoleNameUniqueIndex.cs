using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyProject.AccessDatas.Migrations
{
    /// <inheritdoc />
    public partial class AddUserAccountRoleNameUniqueIndex : Migration
    {
        /// <summary>同 AddCategoryTeamNameUniqueIndex：SQLite 的 trim(X) 只移除半形空格，第二參數涵蓋 tab／換行／全形空白。</summary>
        private const string Whitespace = "' ' || char(9) || char(10) || char(13) || char(12288)";

        /// <summary>
        /// 未刪除、且有「更該保留」的不分大小寫同名帳號：管理員優先（support 不能被停用，否則所有人都進不去），其次 Id 最小的。
        /// </summary>
        private const string DuplicateAccount = """
            "IsDeleted" = 0 AND EXISTS (SELECT 1 FROM MyUser dup
                WHERE dup."IsDeleted" = 0 AND lower(dup.Account) = lower(MyUser.Account) AND dup.Id <> MyUser.Id
                  AND (dup.IsAdmin > MyUser.IsAdmin OR (dup.IsAdmin = MyUser.IsAdmin AND dup.Id < MyUser.Id)))
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ⚠️ 建立唯一索引之前必須先清理既有資料（0.9.171 起）：每次啟動都會自動套用 migration，
            // 只要有一筆重複，索引就建不起來、migration 回滾，每次重啟都再失敗一次（速查表 §4.2）。
            // 服務層的檢查是去頭尾空白、不分大小寫，所以這裡也用 trim 與 lower() 判定重複。

            // 1) 去頭尾空白（含已刪除的資料：還原時才比得到）。
            migrationBuilder.Sql($"UPDATE MyUser SET Account = trim(Account, {Whitespace}) WHERE Account <> trim(Account, {Whitespace});");
            migrationBuilder.Sql($"UPDATE RoleView SET Name = trim(Name, {Whitespace}) WHERE Name <> trim(Name, {Whitespace});");

            // 2) 重複的帳號：先寫稽核（要記下原本的帳號；稽核時間一律 UTC，同 AuditLogService），再改名、停用並換 SecurityStamp（已登入的連線會被登出）。
            //    管理員優先保留，其次最早建立（Id 最小）的；不能自動合併，否則會登入到別人的帳號。管理員在稽核紀錄查得到每一筆。
            migrationBuilder.Sql($"""
                INSERT INTO AuditLog (Action, Success, OccurredAt, TargetType, TargetId, Detail)
                SELECT 'User.DuplicateAccountRenamed', 1, strftime('%Y-%m-%d %H:%M:%f', 'now'), 'MyUser', CAST(Id AS TEXT),
                       '升級時發現不分大小寫重複的帳號：「' || Account || '」改為「' || Account || ' (重複-' || Id || ')」並停用'
                FROM MyUser WHERE {DuplicateAccount};
                """);
            migrationBuilder.Sql($"""
                UPDATE MyUser
                SET Account = Account || ' (重複-' || Id || ')', Status = 0, SecurityStamp = lower(hex(randomblob(16)))
                WHERE {DuplicateAccount};
                """);

            // 3) 重複的角色名稱：照分類的做法，後建的改名。
            migrationBuilder.Sql("""
                UPDATE RoleView SET Name = Name || ' (重複-' || Id || ')'
                WHERE "IsDeleted" = 0 AND EXISTS (SELECT 1 FROM RoleView dup
                    WHERE dup."IsDeleted" = 0 AND lower(dup.Name) = lower(RoleView.Name) AND dup.Id < RoleView.Id);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_RoleView_Name",
                table: "RoleView",
                column: "Name",
                unique: true,
                filter: "\"IsDeleted\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_MyUser_Account",
                table: "MyUser",
                column: "Account",
                unique: true,
                filter: "\"IsDeleted\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RoleView_Name",
                table: "RoleView");

            migrationBuilder.DropIndex(
                name: "IX_MyUser_Account",
                table: "MyUser");
        }
    }
}
