using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MyProject.AccessDatas;

namespace MyProject.Tests;

/// <summary>
/// RemoveAgentPagesData migration（0.10.22 起）：「Agent 測試台」與「Agent 工作流程」移除後，
/// 既有資料庫裡只為這兩頁存在的資料一次清乾淨——權限、角色對應與角色的權限清單、提示詞版本、系統參數。
/// 其他資料一筆都不能動。走真正的 MigrateAsync()：先升級到前一版、塞入舊資料，再升級到最新版。
/// </summary>
public sealed class AgentPagesDataCleanupMigrationTests
{
    /// <summary>清理 migration 的前一版。</summary>
    private const string PreviousMigration = "20261110142313_AddRefreshSession";

    [Fact]
    public async Task Migrate_ShouldRemoveOnlyTheDataOfTheRemovedAgentPages()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options;

        await using (var context = new BackendDBContext(options))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO Permission (Id, Key, DisplayName, GroupName, SortOrder) VALUES
                    (1, '專案項目', '專案項目', '專案管理', 1),
                    (2, 'AI 助理', 'AI 助理', 'AI 助理', 2),
                    (3, 'Agent 測試台', 'Agent 測試台', 'AI 助理', 3),
                    (4, 'Agent 工作流程', 'Agent 工作流程', 'AI 助理', 4);
                INSERT INTO RoleView (Id, Name, TabViewJson, DefaultTeamsJson, RequireTwoFactor, IsDeleted, CreateAt, UpdateAt, ConcurrencyStamp) VALUES
                    (1, '預設角色', '["專案項目","AI 助理","Agent 測試台","Agent 工作流程","登出"]', '[]', 0, 0, '2026-11-01', '2026-11-01', 'c'),
                    (2, '檢視者', '["專案項目"]', '[]', 0, 0, '2026-11-01', '2026-11-01', 'c'),
                    (3, '壞資料', 'not-json', '[]', 0, 0, '2026-11-01', '2026-11-01', 'c');
                INSERT INTO RolePermissionMap (RoleViewId, PermissionId) VALUES (1, 1), (1, 2), (1, 3), (1, 4), (2, 1);
                INSERT INTO PromptTemplate (TemplateKey, Version, Content, IsActive, CreatedAtUtc) VALUES
                    ('LogAnalysis', 1, '日誌', 1, '2026-11-01'),
                    ('AdminAssistantRouter', 1, '分派', 1, '2026-11-01'),
                    ('AgentPlayground', 1, '測試台', 1, '2026-11-01'),
                    ('ProjectReportGatherer', 1, '蒐集', 1, '2026-11-01'),
                    ('ProjectReportWriter', 1, '撰寫', 0, '2026-11-01'),
                    ('ProjectReportFinalizer', 1, '收尾', 1, '2026-11-01'),
                    ('ReviewRequirements', 1, '需求', 1, '2026-11-01'),
                    ('ReviewTechnical', 1, '技術', 1, '2026-11-01'),
                    ('ReviewUsability', 1, '體驗', 1, '2026-11-01'),
                    ('ReviewAggregator', 1, '彙整', 1, '2026-11-01');
                INSERT INTO SystemParameter (ParameterKey, Value, ConcurrencyStamp, CreatedAtUtc, UpdatedAtUtc) VALUES
                    ('AiModelSettings:LogAnalysis', 'm', 'c', '2026-11-01', '2026-11-01'),
                    ('AiReasoningSettings:AdminAssistant', 'low', 'c', '2026-11-01', '2026-11-01'),
                    ('AiModelSettings:AgentPlayground', 'm', 'c', '2026-11-01', '2026-11-01'),
                    ('AiModelSettings:ProjectStatusReport', 'm', 'c', '2026-11-01', '2026-11-01'),
                    ('AiModelSettings:MultiPerspectiveReview', 'm', 'c', '2026-11-01', '2026-11-01'),
                    ('AiReasoningSettings:AgentPlayground', 'low', 'c', '2026-11-01', '2026-11-01'),
                    ('AiReasoningSettings:ProjectStatusReport', 'low', 'c', '2026-11-01', '2026-11-01'),
                    ('AiReasoningSettings:MultiPerspectiveReview', 'low', 'c', '2026-11-01', '2026-11-01');
                """);
        }

        // 升級到最新版：清理寫壞時這一步會直接拋例外（例如角色的權限清單不是合法的 JSON）。
        await using (var context = new BackendDBContext(options))
        {
            await context.Database.MigrateAsync();
        }

        await using (var context = new BackendDBContext(options))
        {
            Assert.Equal(["專案項目"], await context.Permission.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Key).ToListAsync());
            Assert.Equal(
                [(1, 1), (2, 1)],
                (await context.RolePermissionMap.AsNoTracking().OrderBy(x => x.RoleViewId).Select(x => new { x.RoleViewId, x.PermissionId }).ToListAsync())
                    .Select(x => (x.RoleViewId, x.PermissionId)));

            // 角色的權限清單只拿掉這三個，其他照原本的順序；不是 JSON 的不動（交給 RBAC 正規化處理）。
            var roles = await context.RoleView.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id).Select(x => x.TabViewJson).ToListAsync();
            Assert.Equal(["[\"專案項目\",\"登出\"]", "[\"專案項目\"]", "not-json"], roles);

            Assert.Equal(
                ["AdminAssistantRouter", "LogAnalysis"],
                await context.PromptTemplate.AsNoTracking().OrderBy(x => x.TemplateKey).Select(x => x.TemplateKey).ToListAsync());
            Assert.Equal(
                ["AiModelSettings:LogAnalysis", "AiReasoningSettings:AdminAssistant"],
                await context.SystemParameter.AsNoTracking().OrderBy(x => x.ParameterKey).Select(x => x.ParameterKey).ToListAsync());
        }
    }
}
