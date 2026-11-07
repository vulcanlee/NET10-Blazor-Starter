using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MyProject.AccessDatas;
using MyProject.Business.Helpers;

namespace MyProject.Tests;

/// <summary>
/// AddUserAccountRoleNameUniqueIndex migration 的資料清理（0.9.171 起）。
///
/// 每次啟動都會自動套用 migration：既有資料庫裡只要有一筆重複帳號或角色名稱，索引就建不起來、
/// 每次重啟都再失敗一次。所以這裡走真正的 MigrateAsync()，先塞入現行程式已經不可能產生的重複資料再升級。
/// </summary>
public sealed class AccountRoleUniqueIndexMigrationTests
{
    /// <summary>加入唯一索引的前一版 migration。</summary>
    private const string PreviousMigration = "20261107105639_AddEmailOutbox";

    [Fact]
    public async Task Migrate_WithDuplicateAccountsAndRoles_ShouldRenameDisableAuditAndCreateIndexes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options;

        await using (var context = new BackendDBContext(options))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO MyUser (Account, Name, Password, Salt, Status, IsAdmin, IsDeleted, CreateAt, UpdateAt,
                                    ConcurrencyStamp, SecurityStamp, AccessFailedCount, MustChangePassword, TwoFactorEnabled) VALUES
                    ('admin',   '甲', 'p', 's', 1, 1, 0, '2026-11-01', '2026-11-01', 'c', 'stamp-1', 0, 0, 0),
                    ('Admin ',  '乙', 'p', 's', 1, 0, 0, '2026-11-02', '2026-11-02', 'c', 'stamp-2', 0, 0, 0),
                    ('ADMIN',   '丙', 'p', 's', 1, 0, 1, '2026-11-03', '2026-11-03', 'c', 'stamp-3', 0, 0, 0),
                    ('bob',     '丁', 'p', 's', 1, 0, 0, '2026-11-04', '2026-11-04', 'c', 'stamp-4', 0, 0, 0),
                    ('　Bob',   '戊', 'p', 's', 1, 1, 0, '2026-11-05', '2026-11-05', 'c', 'stamp-5', 0, 0, 0),
                    ('carol',   '己', 'p', 's', 1, 0, 0, '2026-11-06', '2026-11-06', 'c', 'stamp-6', 0, 0, 0);
                """);
            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO RoleView (Name, TabViewJson, DefaultTeamsJson, RequireTwoFactor, IsDeleted, CreateAt, UpdateAt, ConcurrencyStamp) VALUES
                    ('Manager', '[]', '[]', 0, 0, '2026-11-01', '2026-11-01', 'c'),
                    ('manager', '[]', '[]', 0, 0, '2026-11-02', '2026-11-02', 'c'),
                    ('Viewer ', '[]', '[]', 0, 0, '2026-11-03', '2026-11-03', 'c');
                """);
        }

        // 升級到最新版：資料清理寫壞時這一步會直接拋例外。
        await using (var context = new BackendDBContext(options))
        {
            await context.Database.MigrateAsync();
        }

        await using (var context = new BackendDBContext(options))
        {
            var users = await context.MyUser.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.Account, x.Status, x.SecurityStamp }).ToListAsync();

            // 去頭尾空白（含全形空白）後才判重複；管理員優先保留（審查發現：support 不能被停用），其次 Id 最小的；
            // 其他改名並停用；已刪除的不動；沒重複的不動。
            Assert.Equal(
                ["admin", $"Admin (重複-{users[1].Id})", "ADMIN", $"bob (重複-{users[3].Id})", "Bob", "carol"],
                users.Select(x => x.Account).ToList());
            Assert.Equal([true, false, true, false, true, true], users.Select(x => x.Status).ToList());

            // 被改名的帳號換 SecurityStamp：已登入的連線會被登出。
            Assert.NotEqual("stamp-2", users[1].SecurityStamp);
            Assert.Equal("stamp-1", users[0].SecurityStamp);

            var roles = await context.RoleView.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.Name }).ToListAsync();
            Assert.Equal(["Manager", $"manager (重複-{roles[1].Id})", "Viewer"], roles.Select(x => x.Name).ToList());

            // 每一筆改名的帳號都留稽核，管理員查得到。
            var audits = await context.AuditLog.AsNoTracking()
                .Where(x => x.Action == AuditActions.User.DuplicateAccountRenamed)
                .OrderBy(x => x.Id).ToListAsync();
            Assert.Equal([users[1].Id.ToString(), users[3].Id.ToString()], audits.Select(x => x.TargetId).ToList());

            // 稽核時間一律是 UTC（AuditLogService 寫 DateTime.UtcNow）；審查發現 migration 原本寫成本地時間。
            Assert.All(audits, audit => Assert.InRange(audit.OccurredAt, DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(10)));
            Assert.Contains("Admin", audits[0].Detail, StringComparison.Ordinal);
            Assert.Contains("(重複-", audits[0].Detail, StringComparison.Ordinal);
            Assert.All(audits, audit => Assert.Equal("MyUser", audit.TargetType));
        }

        await using (var context = new BackendDBContext(options))
        {
            Assert.Contains("WHERE", await IndexSqlAsync(context, "IX_MyUser_Account"), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("WHERE", await IndexSqlAsync(context, "IX_RoleView_Name"), StringComparison.OrdinalIgnoreCase);

            // 索引是最後一道防線：未刪除的同名帳號擋下，已刪除的不受限制。
            await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlRawAsync("""
                INSERT INTO MyUser (Account, Name, Password, Status, IsAdmin, IsDeleted, CreateAt, UpdateAt,
                                    ConcurrencyStamp, SecurityStamp, AccessFailedCount, MustChangePassword, TwoFactorEnabled)
                VALUES ('Bob', '庚', 'p', 1, 0, 0, '2026-11-07', '2026-11-07', 'c', 's', 0, 0, 0);
                """));
            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO MyUser (Account, Name, Password, Status, IsAdmin, IsDeleted, CreateAt, UpdateAt,
                                    ConcurrencyStamp, SecurityStamp, AccessFailedCount, MustChangePassword, TwoFactorEnabled)
                VALUES ('bob', '辛', 'p', 1, 0, 1, '2026-11-07', '2026-11-07', 'c', 's', 0, 0, 0);
                """);
        }
    }

    /// <summary>並行寫入撞到索引時，服務層經 UniqueConstraintHelper 轉成看得懂的訊息，而不是「新增失敗」。</summary>
    [Fact]
    public async Task UniqueViolation_ShouldBeTranslatedToAFriendlyMessage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options;
        await using var context = new BackendDBContext(options);
        await context.Database.EnsureCreatedAsync();

        context.MyUser.Add(new MyProject.AccessDatas.Models.MyUser { Account = "pat", Name = "甲", Password = "p" });
        context.MyUser.Add(new MyProject.AccessDatas.Models.MyUser { Account = "pat", Name = "乙", Password = "p" });
        var userError = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();
        context.RoleView.Add(new MyProject.AccessDatas.Models.RoleView { Name = "稽核" });
        context.RoleView.Add(new MyProject.AccessDatas.Models.RoleView { Name = "稽核" });
        var roleError = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        Assert.True(UniqueConstraintHelper.TryGetFriendlyMessage(userError, out var userMessage));
        Assert.Contains("帳號已存在", userMessage, StringComparison.Ordinal);
        Assert.True(UniqueConstraintHelper.TryGetFriendlyMessage(roleError, out var roleMessage));
        Assert.Contains("角色名稱已存在", roleMessage, StringComparison.Ordinal);
    }

    private static async Task<string> IndexSqlAsync(BackendDBContext context, string indexName)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $name;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = indexName;
        command.Parameters.Add(parameter);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? string.Empty;
    }
}
