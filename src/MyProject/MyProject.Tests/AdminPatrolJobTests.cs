using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Web.Ai;
using MyProject.Web.Ai.Agents.AdminAssistant;
using MyProject.Web.Scheduling;
using MyProject.Web.Scheduling.Jobs;

namespace MyProject.Tests;

/// <summary>
/// 排程作業「系統管理助理巡檢」（0.9.161 起）：跑巡檢 → 判讀 → 存報告 → 需要注意才通知全體管理員。
/// </summary>
public sealed class AdminPatrolJobTests : IAsyncLifetime
{
    private const string NormalReport = """
        ## 總結
        一切正常。

        ## 健康狀態
        狀態：正常
        - 正常。

        ## 例外與錯誤
        狀態：正常

        ## 排程與備份
        狀態：正常

        ## 資源用量
        狀態：正常

        ## 帳號與安全
        狀態：正常
        """;

    private const string AbnormalReport = """
        ## 總結
        備份失敗。

        ## 排程與備份
        狀態：異常
        - 系統備份失敗。
        """;

    private static readonly ScheduledJobContext Manual = new(42, JobRunTriggers.Manual, null, "support", 7);

    private readonly FakePatrolRunner runner = new();
    private readonly RecordingNotificationSender notifications = new();
    private readonly ManualTimeProvider time = new(new DateTimeOffset(2026, 11, 7, 23, 30, 0, TimeSpan.Zero));
    private readonly RecordingAuditLogService audit = new();
    private readonly string rootPath = Path.Combine(Path.GetTempPath(), "MyProjectTests", Guid.NewGuid().ToString("N"));
    private SqliteConnection connection = null!;

    public async Task InitializeAsync()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new BackendDBContext(new DbContextOptionsBuilder<BackendDBContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await connection.DisposeAsync();
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task AiUnavailable_SkipsWithoutSavingOrNotifying()
    {
        runner.UnavailableReason = "尚未設定 AI 金鑰。";

        var result = await CreateJob().ExecuteAsync(Manual, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains("略過", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, runner.Calls);
        Assert.Empty(await CreateService().ListAsync());
        Assert.Empty(notifications.Requests);
    }

    [Fact]
    public async Task AiFails_ReturnsFailure_WithoutSaving()
    {
        runner.Result = AiAnalysisResult.Failure(AiAnalysisFailureReason.Timeout, "AI 回應逾時。");

        var result = await CreateJob().ExecuteAsync(Manual, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("AI 回應逾時。", result.Message, StringComparison.Ordinal);
        Assert.Empty(await CreateService().ListAsync());
        Assert.Empty(notifications.Requests);
    }

    [Fact]
    public async Task EmptyReport_ReturnsFailure()
    {
        runner.Result = new AiAnalysisResult { Success = true, Markdown = "  " };

        var result = await CreateJob().ExecuteAsync(Manual, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(await CreateService().ListAsync());
    }

    [Fact]
    public async Task AllNormal_SavesReport_WithoutNotifying()
    {
        runner.Result = Success(NormalReport);

        var result = await CreateJob().ExecuteAsync(Manual, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains("正常", result.Message, StringComparison.Ordinal);
        var report = Assert.Single(await CreateService().ListAsync());
        Assert.Equal(runner.LastReportId, report.ReportId);
        Assert.Equal(AdminPatrolStatuses.Normal, report.Status);
        Assert.Equal(new DateTime(2026, 11, 8, 7, 30, 0), report.CreatedAt);
        Assert.Equal(JobRunTriggers.Manual, report.Trigger);
        Assert.Equal("support", report.TriggeredByAccount);
        Assert.Equal("gpt-test", report.ModelName);
        Assert.Empty(notifications.Requests);
    }

    [Fact]
    public async Task Abnormal_SavesReport_AndNotifiesAdminsOnce()
    {
        runner.Result = Success(AbnormalReport);

        var result = await CreateJob().ExecuteAsync(Manual, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains("已通知管理員", result.Message, StringComparison.Ordinal);
        var report = Assert.Single(await CreateService().ListAsync());
        Assert.Equal(AdminPatrolStatuses.Abnormal, report.Status);
        Assert.Equal("排程與備份", report.AttentionSections);
        var request = Assert.Single(notifications.Requests);
        Assert.Equal(NotificationCategories.AdminPatrol, request.Category);
        Assert.Equal(AdminPatrolNotification.LinkOf(report.ReportId), request.Link);
        Assert.Equal($"AdminPatrol:{report.ReportId:N}", request.SourceKey);
        Assert.True(request.AlsoEmail);
        Assert.True(request.Target.Admins);
    }

    /// <summary>審查發現：通知發送失敗（NotificationSender 不丟例外、回 Failed）時不能回報成功，要讓排程框架通知管理員。</summary>
    [Fact]
    public async Task NotificationFails_ReturnsFailure_ButKeepsReport()
    {
        runner.Result = Success(AbnormalReport);
        var job = new AdminPatrolJob(runner, CreateService(), new FailingNotificationSender(), time, NullLogger<AdminPatrolJob>.Instance);

        var result = await job.ExecuteAsync(Manual, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("通知發送失敗", result.Message, StringComparison.Ordinal);
        Assert.Single(await CreateService().ListAsync());
    }

    [Fact]
    public async Task Unreadable_SavesReport_AndNotifies()
    {
        runner.Result = Success("看起來都很好。");

        await CreateJob().ExecuteAsync(Manual, CancellationToken.None);

        Assert.Equal(AdminPatrolStatuses.Unreadable, Assert.Single(await CreateService().ListAsync()).Status);
        Assert.Single(notifications.Requests);
    }

    [Fact]
    public async Task SaveFails_ReturnsFailure_WithoutNotifying()
    {
        runner.Result = Success(AbnormalReport);

        var result = await CreateJob(CreateService(rootPath: string.Empty)).ExecuteAsync(Manual, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(notifications.Requests);
    }

    [Fact]
    public async Task ScheduledRun_HasNoTriggeredBy()
    {
        runner.Result = Success(NormalReport);

        await CreateJob().ExecuteAsync(new ScheduledJobContext(43, JobRunTriggers.Schedule, DateTime.UtcNow, null), CancellationToken.None);

        var report = Assert.Single(await CreateService().ListAsync());
        Assert.Equal(JobRunTriggers.Schedule, report.Trigger);
        Assert.Null(report.TriggeredByAccount);
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        runner.Throw = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() => CreateJob().ExecuteAsync(Manual, new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task RetentionJob_PurgesExpired_AndAuditsOnlyWhenRemoved()
    {
        var service = CreateService(retentionDays: 30);
        await service.SaveAsync(new AdminPatrolReportDraft(
            Guid.NewGuid(), new DateTime(2026, 9, 1), AdminPatrolStatuses.Normal, string.Empty, JobRunTriggers.Schedule, null, 1, null, false, "## 總結\n舊的。"));
        var job = new AdminPatrolReportRetentionJob(service, audit, time, NullLogger<AdminPatrolReportRetentionJob>.Instance);

        var first = await job.ExecuteAsync(Manual, CancellationToken.None);
        var second = await job.ExecuteAsync(Manual, CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.AdminPatrol.AutoPurge, entry.Action);
        Assert.Contains("rows=1", entry.Detail, StringComparison.Ordinal);
        Assert.Empty(await service.ListAsync());
    }

    [Fact]
    public async Task RetentionJob_ServiceFails_ReturnsFailure()
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE AdminPatrolReport";
            await command.ExecuteNonQueryAsync();
        }

        var job = new AdminPatrolReportRetentionJob(CreateService(), audit, time, NullLogger<AdminPatrolReportRetentionJob>.Instance);

        Assert.False((await job.ExecuteAsync(Manual, CancellationToken.None)).Succeeded);
    }

    private static AiAnalysisResult Success(string markdown) => new() { Success = true, Markdown = markdown, ModelName = "gpt-test" };

    private AdminPatrolReportService CreateService(int retentionDays = 90, string? rootPath = null)
    {
        var settings = new SystemSettings();
        settings.ExternalFileSystem.AiAgentConversationPath = rootPath ?? this.rootPath;
        return new AdminPatrolReportService(
            new TestDbContextFactory(connection),
            new AdminPatrolReportFileStore(Options.Create(settings), NullLogger<AdminPatrolReportFileStore>.Instance),
            new StaticOptionsMonitor<AdminPatrolSettings>(new AdminPatrolSettings { RetentionDays = retentionDays }),
            NullLogger<AdminPatrolReportService>.Instance);
    }

    private AdminPatrolJob CreateJob(AdminPatrolReportService? service = null)
        => new(runner, service ?? CreateService(), notifications, time, NullLogger<AdminPatrolJob>.Instance);

    private sealed class FailingNotificationSender : INotificationSender
    {
        public Task<NotificationSendResult> SendAsync(NotificationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new NotificationSendResult(0, 0, Failed: true));
    }

    private sealed class FakePatrolRunner : IAdminPatrolRunner
    {
        public string? UnavailableReason { get; set; }

        public AiAnalysisResult Result { get; set; } = new() { Success = true, Markdown = NormalReport };

        public Exception? Throw { get; set; }

        public int Calls { get; private set; }

        public Guid LastReportId { get; private set; }

        public Task<AiAnalysisResult> RunAsync(Guid reportId, CancellationToken cancellationToken)
        {
            Calls++;
            LastReportId = reportId;
            return Throw is null ? Task.FromResult(Result) : Task.FromException<AiAnalysisResult>(Throw);
        }
    }
}
