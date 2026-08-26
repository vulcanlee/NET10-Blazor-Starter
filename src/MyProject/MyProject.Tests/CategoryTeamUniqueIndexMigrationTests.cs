using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MyProject.AccessDatas;

namespace MyProject.Tests;

/// <summary>
/// AddCategoryTeamNameUniqueIndex migration 的資料清理。
///
/// 這是整個唯一性補強中最危險的一段：Program.cs 啟動時無條件呼叫 Database.Migrate()，
/// 只要既有資料庫裡有一筆重複名稱，索引就建不起來、migration 回滾、
/// __EFMigrationsHistory 不會寫入 —— 每次重啟都再失敗一次，服務永遠起不來。
///
/// 其他測試 fixture 都用 EnsureCreatedAsync()（依 model 建表、完全跳過 migration），
/// 測不到這件事，因此這裡刻意走真正的 MigrateAsync()。
/// </summary>
public sealed class CategoryTeamUniqueIndexMigrationTests
{
    /// <summary>加入唯一索引的前一版 migration。</summary>
    private const string PreviousMigration = "20260826061236_AddCategoryTeams";

    [Fact]
    public async Task Migrate_WithDuplicateAndUntrimmedData_ShouldCleanUpAndCreateIndexes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options;

        // 1) 先建到「加索引之前」的狀態，才有機會塞入現行程式碼已經不可能產生的髒資料。
        await using (var context = new BackendDBContext(options))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO Category (Name, IsEnabled, CreatedAt, UpdatedAt) VALUES
                    ('技術文件', 1, '2026-08-26', '2026-08-26'),
                    ('技術文件 ', 1, '2026-08-26', '2026-08-26'),
                    ('技術文件　', 1, '2026-08-26', '2026-08-26'),
                    ('Report', 1, '2026-08-26', '2026-08-26'),
                    ('report', 1, '2026-08-26', '2026-08-26'),
                    ('會議紀錄', 1, '2026-08-26', '2026-08-26');
                """);

            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO Team (Name, Code, IsEnabled, CreatedAt, UpdatedAt) VALUES
                    ('研發部', '', 1, '2026-08-26', '2026-08-26'),
                    ('業務部', '   ', 1, '2026-08-26', '2026-08-26'),
                    ('管理部', NULL, 1, '2026-08-26', '2026-08-26'),
                    ('客服部', 'CS', 1, '2026-08-26', '2026-08-26'),
                    ('客服部 ', 'cs ', 1, '2026-08-26', '2026-08-26');
                """);
        }

        // 2) 升級到最新版：這一步在資料清理寫壞時會直接拋例外。
        await using (var context = new BackendDBContext(options))
        {
            await context.Database.MigrateAsync();
        }

        await using (var context = new BackendDBContext(options))
        {
            var categories = await context.Category.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Name).ToListAsync();

            // 同名者中 Id 最小的保留原名，其餘加上以 Id 為準的尾碼；沒重複的完全不動。
            // 被改名者維持自己原本的大小寫（'report' 不會被寫成 'Report'）——
            // 去重只判定重複，不應順手竄改使用者輸入的字樣。
            Assert.Equal(
                ["技術文件", "技術文件 (重複-2)", "技術文件 (重複-3)", "Report", "report (重複-5)", "會議紀錄"],
                categories);

            var teams = await context.Team.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new { x.Name, x.Code }).ToListAsync();

            // 空字串與純空白的代號都歸一成 NULL，三筆「未填代號」共存不觸發唯一索引。
            Assert.Null(teams[0].Code);
            Assert.Null(teams[1].Code);
            Assert.Null(teams[2].Code);

            Assert.Equal("客服部", teams[3].Name);
            Assert.Equal("CS", teams[3].Code);

            // 尾隨空白先被去除，去除後才變成重複，因此接著被改名。
            Assert.Equal("客服部 (重複-5)", teams[4].Name);
            Assert.Equal("cs (重複-5)", teams[4].Code);
        }

        // 3) 索引確實建立起來了。
        await using (var context = new BackendDBContext(options))
        {
            Assert.True(await IndexExistsAsync(context, "IX_Category_Name"));
            Assert.True(await IndexExistsAsync(context, "IX_Team_Name"));
            Assert.True(await IndexExistsAsync(context, "IX_Team_Code"));
        }
    }

    private static async Task<bool> IndexExistsAsync(BackendDBContext context, string indexName)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name;";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = indexName;
        command.Parameters.Add(parameter);

        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }
}
