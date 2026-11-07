using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Web.Components.Views.Admins;

namespace MyProject.Tests.Components;

/// <summary>
/// 系統管理助理頁的「巡檢報告」視窗（0.9.161 起）：清單、打開一份報告（AI 產出經安全管線）、深連結找不到報告。
/// </summary>
public sealed class AdminPatrolReportsModalTests : ComponentTestBase
{
    private readonly string rootPath = Path.Combine(Path.GetTempPath(), "MyProjectTests", Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection connection;
    private readonly AdminPatrolReportService service;

    public AdminPatrolReportsModalTests()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options))
        {
            context.Database.EnsureCreated();
        }

        var settings = new SystemSettings();
        settings.ExternalFileSystem.AiAgentConversationPath = rootPath;
        service = new AdminPatrolReportService(
            new TestDbContextFactory(connection),
            new AdminPatrolReportFileStore(Options.Create(settings), NullLogger<AdminPatrolReportFileStore>.Instance),
            new StaticOptionsMonitor<AdminPatrolSettings>(new AdminPatrolSettings { RetentionDays = 45 }),
            NullLogger<AdminPatrolReportService>.Instance);
        Services.AddLogging();
        Services.AddSingleton(service);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            connection.Dispose();
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }

        base.Dispose(disposing);
    }

    [Fact]
    public async Task Open_ShowsListAndLatestReport_RenderedSafely()
    {
        await SaveAsync(new DateTime(2026, 11, 7, 7, 30, 0), AdminPatrolStatuses.Normal, "", "## 總結\n昨天正常。");
        var latest = await SaveAsync(
            new DateTime(2026, 11, 8, 7, 30, 0), AdminPatrolStatuses.Abnormal, "排程與備份",
            "## 總結\n**備份失敗**<script>alert(1)</script>");
        var cut = Render<AdminPatrolReportsModal>();

        await cut.InvokeAsync(() => cut.Instance.OpenAsync());

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("2026/11/08 07:30", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("2026/11/07 07:30", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("排程與備份", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("<strong>備份失敗</strong>", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("<script>", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("保存 45 天", cut.Markup, StringComparison.Ordinal);
        });
        Assert.Equal(latest, cut.Instance.SelectedReportId);
    }

    [Fact]
    public async Task OpenSpecificReport_SelectsIt()
    {
        var older = await SaveAsync(new DateTime(2026, 11, 7, 7, 30, 0), AdminPatrolStatuses.Attention, "帳號與安全", "## 總結\n較舊的報告。");
        await SaveAsync(new DateTime(2026, 11, 8, 7, 30, 0), AdminPatrolStatuses.Normal, "", "## 總結\n最新的報告。");
        var cut = Render<AdminPatrolReportsModal>();

        var found = await cut.InvokeAsync(() => cut.Instance.OpenAsync(older));

        Assert.True(found);
        cut.WaitForAssertion(() => Assert.Contains("較舊的報告。", cut.Markup, StringComparison.Ordinal));
        Assert.Equal(older, cut.Instance.SelectedReportId);
    }

    [Fact]
    public async Task OpenMissingReport_ReturnsFalse_AndStillShowsList()
    {
        await SaveAsync(new DateTime(2026, 11, 8, 7, 30, 0), AdminPatrolStatuses.Normal, "", "## 總結\n最新的報告。");
        var cut = Render<AdminPatrolReportsModal>();

        var found = await cut.InvokeAsync(() => cut.Instance.OpenAsync(Guid.NewGuid()));

        Assert.False(found);
        cut.WaitForAssertion(() => Assert.Contains("最新的報告。", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoReports_ShowsEmptyHint()
    {
        var cut = Render<AdminPatrolReportsModal>();

        await cut.InvokeAsync(() => cut.Instance.OpenAsync());

        cut.WaitForAssertion(() => Assert.Contains("還沒有巡檢報告", cut.Markup, StringComparison.Ordinal));
    }

    private async Task<Guid> SaveAsync(DateTime createdAt, string status, string attention, string markdown)
    {
        var id = Guid.NewGuid();
        var saved = await service.SaveAsync(new AdminPatrolReportDraft(
            id, createdAt, status, attention, JobRunTriggers.Schedule, null, 1, "gpt-test", false, markdown));
        Assert.NotNull(saved);
        return id;
    }
}
