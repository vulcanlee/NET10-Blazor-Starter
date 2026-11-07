using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Others;
using MyProject.Models.Systems;

namespace MyProject.Tests;

/// <summary>
/// 定時巡檢報告的存放（0.9.161 起）。
///
/// 重點：
/// 1. 內容寫進 <c>{AiAgentConversationPath}/AdminPatrol/{yyyyMM}/{ReportId:N}.md</c>，資料表只放固定用語的中繼資料。
/// 2. 寫資料列失敗就刪掉剛寫的檔，不留孤兒檔。
/// 3. 清單不分使用者（全體管理員都看得到），最新的在前、最多 100 筆。
/// 4. 「下次會清掉幾筆」等於實際清掉的筆數；清理不會動到 Agent 對話，Agent 對話的清理也不會動到巡檢報告。
/// </summary>
public sealed class AdminPatrolReportServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 7, 30, 0);

    [Fact]
    public async Task Save_WritesContentFileAndRow_ThenGetReadsBack()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var draft = Draft(Now, status: AdminPatrolStatuses.Abnormal, attention: "排程與備份", markdown: "## 總結\n有問題。");

        var saved = await service.SaveAsync(draft);

        Assert.NotNull(saved);
        Assert.Equal(draft.ReportId, saved.ReportId);
        var row = Assert.Single(await fixture.ListRowsAsync());
        Assert.Equal($"202611/{draft.ReportId:N}.md", row.ContentFile);
        Assert.Equal(AdminPatrolStatuses.Abnormal, row.Status);
        Assert.Equal("排程與備份", row.AttentionSections);
        Assert.Equal(JobRunTriggers.Manual, row.Trigger);
        Assert.Equal("support", row.TriggeredByAccount);
        Assert.Equal(42, row.JobRunId);
        Assert.True(File.Exists(Path.Combine(fixture.RootPath, "AdminPatrol", "202611", $"{draft.ReportId:N}.md")));

        var detail = await service.GetAsync(draft.ReportId);
        Assert.NotNull(detail);
        Assert.Equal("## 總結\n有問題。", detail.Markdown);
        Assert.Equal(AdminPatrolStatuses.Abnormal, detail.Summary.Status);
        Assert.Equal("gpt-test", detail.Summary.ModelName);
    }

    [Fact]
    public async Task Save_RowFails_DeletesWrittenFile_AndReturnsNull()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("DROP TABLE AdminPatrolReport");
        var draft = Draft(Now);

        var saved = await fixture.CreateService().SaveAsync(draft);

        Assert.Null(saved);
        Assert.False(File.Exists(Path.Combine(fixture.RootPath, "AdminPatrol", "202611", $"{draft.ReportId:N}.md")));
    }

    [Fact]
    public async Task Save_FileFails_ReturnsNull_AndWritesNoRow()
    {
        await using var fixture = await Fixture.CreateAsync();

        var saved = await fixture.CreateService(rootPath: string.Empty).SaveAsync(Draft(Now));

        Assert.Null(saved);
        Assert.Empty(await fixture.ListRowsAsync());
    }

    [Fact]
    public async Task List_NewestFirst_AtMost100_ForAnyone()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        for (var i = 0; i < AdminPatrolReportService.MaxListCount + 2; i++)
        {
            await service.SaveAsync(Draft(Now.AddDays(-i)));
        }

        var list = await service.ListAsync();

        Assert.Equal(AdminPatrolReportService.MaxListCount, list.Count);
        Assert.Equal(Now, list[0].CreatedAt);
        Assert.True(list.Zip(list.Skip(1)).All(pair => pair.First.CreatedAt > pair.Second.CreatedAt));
    }

    [Fact]
    public async Task Get_Missing_ReturnsNull_AndMissingFile_ReturnsNullMarkdown()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService();
        var draft = Draft(Now);
        await service.SaveAsync(draft);
        File.Delete(Path.Combine(fixture.RootPath, "AdminPatrol", "202611", $"{draft.ReportId:N}.md"));

        Assert.Null(await service.GetAsync(Guid.NewGuid()));
        var detail = await service.GetAsync(draft.ReportId);
        Assert.NotNull(detail);
        Assert.Null(detail.Markdown);
    }

    [Fact]
    public async Task Purge_RemovesExpiredRowsAndFiles_AndStatsMatch()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateService(retentionDays: 30);
        var old = Draft(new DateTime(2026, 9, 1, 7, 30, 0));
        var edge = Draft(Now.Date.AddDays(-30));
        var recent = Draft(Now.AddDays(-1));
        foreach (var draft in new[] { old, edge, recent })
        {
            await service.SaveAsync(draft);
        }

        var stats = await service.GetRetentionStatsAsync(Now);
        var removed = await service.PurgeExpiredAsync(Now);

        Assert.Equal(3, stats.Total);
        Assert.Equal(old.CreatedAt, stats.Oldest);
        Assert.Equal(1, stats.Expired);
        Assert.Equal(stats.Expired, removed);
        Assert.Equal(
            new[] { edge.ReportId, recent.ReportId }.Order(),
            (await fixture.ListRowsAsync()).Select(row => row.ReportId).Order());
        Assert.False(Directory.Exists(Path.Combine(fixture.RootPath, "AdminPatrol", "202609")));
        Assert.True(File.Exists(Path.Combine(fixture.RootPath, "AdminPatrol", "202611", $"{recent.ReportId:N}.md")));
    }

    [Fact]
    public async Task AgentConversationPurge_DoesNotTouchPatrolReports()
    {
        await using var fixture = await Fixture.CreateAsync();
        var draft = Draft(new DateTime(2026, 1, 1, 7, 30, 0));
        await fixture.CreateService().SaveAsync(draft);
        var conversations = new AiAgentConversationService(
            new TestDbContextFactory(fixture.Connection),
            new AiAgentConversationFileStore(Options.Create(fixture.Settings()), fixture.Logger<AiAgentConversationFileStore>()),
            new CurrentUserService { CurrentUser = new CurrentUser() },
            new RecordingAuditLogService(),
            new StaticOptionsMonitor<AiAgentConversationSettings>(new AiAgentConversationSettings { RetentionDays = 1 }),
            fixture.Logger<AiAgentConversationService>());

        await conversations.PurgeExpiredAsync(Now);

        Assert.True(File.Exists(Path.Combine(fixture.RootPath, "AdminPatrol", "202601", $"{draft.ReportId:N}.md")));
    }

    [Theory]
    [InlineData("../outside.md")]
    [InlineData("..\\outside.md")]
    public async Task FileStore_RejectsPathsOutsideRoot(string relativePath)
    {
        await using var fixture = await Fixture.CreateAsync();
        var outside = Path.Combine(fixture.RootPath, "outside.md");
        await File.WriteAllTextAsync(outside, "機密");
        var store = fixture.CreateFileStore();

        Assert.Null(await store.ReadAsync(relativePath));
        store.Delete(relativePath);
        Assert.True(File.Exists(outside));
        Assert.Null(await store.ReadAsync(outside));
    }

    private static AdminPatrolReportDraft Draft(
        DateTime createdAt, string status = AdminPatrolStatuses.Normal, string attention = "", string markdown = "## 總結\n正常。")
        => new(Guid.NewGuid(), createdAt, status, attention, JobRunTriggers.Manual, "support", 42, "gpt-test", false, markdown);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(_ => { });

        private Fixture(SqliteConnection connection, string rootPath)
        {
            Connection = connection;
            RootPath = rootPath;
        }

        public SqliteConnection Connection { get; }

        public string RootPath { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using (var context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options))
            {
                await context.Database.EnsureCreatedAsync();
            }

            var path = Path.Combine(Path.GetTempPath(), "MyProjectTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new Fixture(connection, path);
        }

        public ILogger<T> Logger<T>() => loggerFactory.CreateLogger<T>();

        public SystemSettings Settings(string? rootPath = null)
        {
            var settings = new SystemSettings();
            settings.ExternalFileSystem.AiAgentConversationPath = rootPath ?? RootPath;
            return settings;
        }

        public AdminPatrolReportFileStore CreateFileStore(string? rootPath = null)
            => new(Options.Create(Settings(rootPath)), Logger<AdminPatrolReportFileStore>());

        public AdminPatrolReportService CreateService(int retentionDays = 90, string? rootPath = null)
            => new(
                new TestDbContextFactory(Connection),
                CreateFileStore(rootPath),
                new StaticOptionsMonitor<AdminPatrolSettings>(new AdminPatrolSettings { RetentionDays = retentionDays }),
                Logger<AdminPatrolReportService>());

        public async Task<List<AdminPatrolReport>> ListRowsAsync()
        {
            await using var context = new TestDbContextFactory(Connection).CreateDbContext();
            return await context.AdminPatrolReport.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var command = Connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            loggerFactory.Dispose();
            try
            {
                Directory.Delete(RootPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
