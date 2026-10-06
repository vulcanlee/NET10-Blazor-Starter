using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MyProject.AccessDatas;

namespace MyProject.Tests;

/// <summary>
/// 部門樹（0.9.105 起）的 migration 不動既有資料。
///
/// 驗證的是腳手架自己的升級路徑（寫死前一版 migration 名稱），所以獨立成一個檔案：
/// <c>scripts/New-StarterProject.ps1</c> 會清空 migration 重建單一 <c>Init</c>，並一併刪除本檔，
/// 否則衍生專案的這支測試必定失敗（0.9.116 起；原本放在 <c>TeamTreeTests</c>）。
/// </summary>
public sealed class TeamTreeMigrationTests
{
    /// <summary>⭐ 升級時 Team 與參照它的 UserTeam 資料都不動（EF 預設會整張重建 Team）；外鍵確實生效。</summary>
    [Fact]
    public async Task Migration_ShouldKeepExistingTeamsAndMemberships()
    {
        await using var migrationConnection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await migrationConnection.OpenAsync();
        var options = new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(migrationConnection).Options;

        await using (var context = new BackendDBContext(options))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreviousMigration(context));
            // 成員關係只需要存在：暫時關掉外鍵檢查，不必湊齊那一版 MyUser 的所有欄位。
            await context.Database.ExecuteSqlRawAsync("""
                PRAGMA foreign_keys = OFF;
                INSERT INTO Team (Name, IsEnabled, CreatedAt, UpdatedAt, ConcurrencyStamp, IsDeleted) VALUES ('研發部', 1, '2026-10-01', '2026-10-01', 'x', 0);
                INSERT INTO UserTeam (MyUserId, TeamId) VALUES (1, 1);
                PRAGMA foreign_keys = ON;
                """);
        }

        await using (var context = new BackendDBContext(options))
        {
            await context.Database.MigrateAsync();
            Assert.Equal(1, await context.UserTeam.CountAsync());
            Assert.Null((await context.Team.AsNoTracking().SingleAsync()).ParentId);
            await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlRawAsync(
                "INSERT INTO Team (Name, IsEnabled, CreatedAt, UpdatedAt, ConcurrencyStamp, IsDeleted, ParentId) VALUES ('孤兒', 1, '2026-10-01', '2026-10-01', 'y', 0, 999);"));
        }
    }

    private static string PreviousMigration(BackendDBContext context)
        => context.Database.GetMigrations().Single(x => x.EndsWith("_AddTwoFactorBackupCodes", StringComparison.Ordinal));
}
