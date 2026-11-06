using Microsoft.Extensions.DependencyInjection;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Models.Others;
using MyProject.Web.Ai.Agents.AdminAssistant;
using MyProject.Web.Diagnostics;
using MyProject.Web.Scheduling;

namespace MyProject.Tests;

/// <summary>系統管理助理的唯讀工具（0.9.155 起）：範圍夾住、截斷、稽核比照頁面並註明來源。</summary>
public sealed class AdminAssistantToolsTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 10, 0, 0);
    private readonly RecordingAuditLogService audit = new();
    private readonly CurrentUserService currentUser = new() { CurrentUser = new CurrentUser { Id = 7, Account = "support" } };

    [Fact]
    public void ParseRange_Empty_UsesDefaultSpanWithoutNote()
    {
        var (start, end, note) = AdminAssistantToolText.ParseRange(null, null, Now, TimeSpan.FromHours(24), TimeSpan.FromDays(3));

        Assert.Equal(Now.AddHours(-24), start);
        Assert.Equal(Now, end);
        Assert.Null(note);
    }

    [Fact]
    public void ParseRange_Invalid_UsesDefaultAndNotes()
    {
        var (start, end, note) = AdminAssistantToolText.ParseRange("昨天晚上", null, Now, TimeSpan.FromHours(24), TimeSpan.FromDays(3));

        Assert.Equal(Now.AddHours(-24), start);
        Assert.Equal(Now, end);
        Assert.Contains("昨天晚上", note, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRange_AcceptsDateAndDateTime()
    {
        var (start, end, _) = AdminAssistantToolText.ParseRange("2026-11-05", "2026-11-05 23:30", Now, TimeSpan.FromHours(24), TimeSpan.FromDays(3));

        Assert.Equal(new DateTime(2026, 11, 5), start);
        Assert.Equal(new DateTime(2026, 11, 5, 23, 30, 0), end);
    }

    /// <summary>結束只給日期：當成那一天的結束（23:59:59），不能把整天漏掉；並在說明裡講清楚。</summary>
    [Fact]
    public void ParseRange_DateOnlyEnd_MeansEndOfThatDay()
    {
        var (start, end, note) = AdminAssistantToolText.ParseRange("2026-11-05", "2026-11-05", Now, TimeSpan.FromHours(24), TimeSpan.FromDays(3));

        Assert.Equal(new DateTime(2026, 11, 5), start);
        Assert.Equal(new DateTime(2026, 11, 6).AddTicks(-1), end);
        Assert.Contains("23:59:59", note, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRange_DateOnlyEndAlone_CoversThatDay()
    {
        var (start, end, _) = AdminAssistantToolText.ParseRange(null, "2026-11-05", Now, TimeSpan.FromHours(24), TimeSpan.FromDays(3));

        Assert.True(start <= new DateTime(2026, 11, 5));
        Assert.Equal(new DateTime(2026, 11, 6).AddTicks(-1), end);
    }

    [Fact]
    public void ParseRange_TooLong_ClampsStart()
    {
        var (start, end, note) = AdminAssistantToolText.ParseRange("2026-10-01", "2026-11-06 10:00", Now, TimeSpan.FromHours(24), TimeSpan.FromDays(3));

        Assert.Equal(end.AddDays(-3), start);
        Assert.NotNull(note);
    }

    /// <summary>筆數被夾住時要說明（0.9.157 起）；沒被夾住不加說明。</summary>
    [Fact]
    public void ClampWithNote_AddsNoteOnlyWhenClamped()
    {
        var notes = new List<string>();

        Assert.Equal(50, AdminAssistantToolText.ClampWithNote(50, 1, 200, "筆數", notes));
        Assert.Empty(notes);
        Assert.Equal(200, AdminAssistantToolText.ClampWithNote(999, 1, 200, "筆數", notes));
        var note = Assert.Single(notes);
        Assert.Contains("999", note, StringComparison.Ordinal);
        Assert.Contains("200", note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryLogs_UnknownLevelAndClampedTake_AreExplainedInNote()
    {
        var tools = CreateMonitoringTools();

        var json = await tools.QueryLogsAsync(null, null, "verbose", null, null, take: 999);

        Assert.Contains("verbose", json, StringComparison.Ordinal);
        Assert.Contains("200", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// 模型給的「選項值」（動作代碼、作業名稱）要先比對已知清單（0.9.157 起）：在清單內照頁面用 Of 記值；
    /// 不在清單內就當自由文字（稽核只記有沒有填），並說明可能查不到。
    /// </summary>
    [Fact]
    public void ChoiceCondition_KnownValueRecorded_UnknownTreatedAsFreeTextWithNote()
    {
        var notes = new List<string>();
        string[] known = ["Login.Failed", "User.Update"];

        var ok = AdminAssistantToolText.ChoiceCondition("action", "Login.Failed", known, notes);
        var empty = AdminAssistantToolText.ChoiceCondition("action", string.Empty, known, notes);
        Assert.False(ok.IsFreeText);
        Assert.False(empty.IsFreeText);
        Assert.Empty(notes);

        var unknown = AdminAssistantToolText.ChoiceCondition("action", "Login.Fail", known, notes);
        Assert.True(unknown.IsFreeText);
        Assert.Contains("Login.Fail", Assert.Single(notes), StringComparison.Ordinal);
    }

    /// <summary>巡檢要的是最近 24 小時：search_exceptions 接受 hours（0.9.157 起），起點＝結束往前 N 小時。</summary>
    [Fact]
    public async Task SearchExceptions_Hours_SetsRelativeRange()
    {
        await using var fixture = await ExceptionLogServiceTests.ExceptionLogFixture.CreateAsync();
        var tools = CreateMonitoringTools(exceptions: fixture.CreateService());

        var json = await tools.SearchExceptionsAsync(hours: 24);

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var start = DateTime.ParseExact(document.RootElement.GetProperty("appliedStart").GetString()!, "yyyy/MM/dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var end = DateTime.ParseExact(document.RootElement.GetProperty("appliedEnd").GetString()!, "yyyy/MM/dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(Math.Abs(((end - start) - TimeSpan.FromHours(24)).TotalSeconds) <= 2, $"range {end - start}");
    }

    /// <summary>資料庫用量不送出完整路徑（0.9.157 起），只給檔名。</summary>
    [Fact]
    public async Task GetDatabaseUsage_OmitsFullPath()
    {
        var tools = CreateMonitoringTools(databaseUsage: new FakeDatabaseUsageService(new DatabaseUsageReport
        {
            DatabaseFilePath = @"C:\secret-folder\MyProject.db",
            DatabaseFileName = "MyProject.db",
            MainDbBytes = 1024,
        }));

        var json = await tools.GetDatabaseUsageAsync();

        Assert.DoesNotContain("secret-folder", json, StringComparison.Ordinal);
        Assert.Contains("MyProject.db", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(50, 50)]
    [InlineData(999, 200)]
    public void Clamp_Bounds(int value, int expected) => Assert.Equal(expected, AdminAssistantToolText.Clamp(value, 1, 200));

    [Fact]
    public void Truncate_AddsEllipsis()
    {
        Assert.Equal("abc…", AdminAssistantToolText.Truncate("abcdef", 3));
        Assert.Equal("abc", AdminAssistantToolText.Truncate("abc", 3));
        Assert.Equal(string.Empty, AdminAssistantToolText.Truncate(null, 3));
    }

    [Fact]
    public async Task QueryLogs_ClampsTake_DefaultsRange_AndAuditsWithSource()
    {
        var logs = new RecordingLogQueryService();
        var tools = CreateMonitoringTools(logs: logs);

        var json = await tools.QueryLogsAsync(null, null, "warn", "逾時", null, take: 999);

        var request = Assert.Single(logs.Requests);
        Assert.Equal(200, request.Take);
        Assert.Equal(TimeSpan.FromHours(24), request.EndTime - request.StartTime);
        Assert.Equal(LogLevelRank.Warn, request.MinimumLevel);
        Assert.Equal("逾時", request.Keyword);
        Assert.Contains("一筆日誌", json, StringComparison.Ordinal);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.LogViewer.Search, entry.Action);
        Assert.Equal("support", entry.ActorAccount);
        Assert.Contains("via=admin-assistant", entry.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("逾時", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryLogs_TruncatesLongMessages()
    {
        var logs = new RecordingLogQueryService { Message = new string('長', 800) };
        var tools = CreateMonitoringTools(logs: logs);

        var json = await tools.QueryLogsAsync(null, null, null, null, null);

        Assert.DoesNotContain(new string('長', 501), json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetExceptionDetail_TruncatesStack_AndAuditsView()
    {
        await using var fixture = await ExceptionLogServiceTests.ExceptionLogFixture.CreateAsync();
        var service = fixture.CreateService();
        var outcome = await service.RecordAsync(ExceptionLogServiceTests.NewEntry(stackTrace: new string('S', 6000)));
        var tools = CreateMonitoringTools(exceptions: service);

        var json = await tools.GetExceptionDetailAsync(outcome!.Id);

        Assert.Contains("SSSS", json, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('S', 4001), json, StringComparison.Ordinal);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.ExceptionLog.View, entry.Action);
        Assert.Contains("via=admin-assistant", entry.Detail, StringComparison.Ordinal);
        Assert.Equal(outcome.Id.ToString(), audit.Contents[0].TargetId);
    }

    [Fact]
    public async Task GetExceptionDetail_Missing_ReturnsMessageWithoutAudit()
    {
        await using var fixture = await ExceptionLogServiceTests.ExceptionLogFixture.CreateAsync();
        var tools = CreateMonitoringTools(exceptions: fixture.CreateService());

        var text = await tools.GetExceptionDetailAsync(12345);

        Assert.Contains("找不到", text, StringComparison.Ordinal);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public void ResolveJob_ByNameOrDisplayName_IgnoringCase()
    {
        IReadOnlyList<ScheduledJobDescriptor> descriptors =
        [
            new("SystemBackup", "系統備份", "說明", "0 2 * * *", typeof(object)),
            new("AuditLogRetention", "稽核紀錄保留", "說明", "0 3 * * *", typeof(object)),
        ];

        Assert.Equal("SystemBackup", OpsAssistantTools.ResolveJob(descriptors, "systembackup")?.Name);
        Assert.Equal("SystemBackup", OpsAssistantTools.ResolveJob(descriptors, " 系統備份 ")?.Name);
        Assert.Null(OpsAssistantTools.ResolveJob(descriptors, "不存在"));
        Assert.Contains("SystemBackup（系統備份）", OpsAssistantTools.UnknownJobMessage(descriptors, "不存在"), StringComparison.Ordinal);
    }

    /// <summary>每個工具都建得出函式（參數 schema 在執行期才產生，這裡先擋下寫錯的參數型別），名稱與說明一致。</summary>
    [Fact]
    public void AllTools_CreateFunctions_WithDeclaredNameAndParameters()
    {
        var services = new ServiceCollection()
            .AddSingleton(CreateMonitoringTools())
            .AddSingleton(new OpsAssistantTools(null!, null!, null!, null!, null!, null!, audit, currentUser, null!, null!))
            .AddSingleton(new AccountsAssistantTools(null!, null!, null!, audit, currentUser, null!))
            .AddSingleton(new AiAssistantTools(null!, null!, null!, null!, audit, currentUser))
            .BuildServiceProvider();

        foreach (var tool in AdminAssistantAgent.Home.Tools)
        {
            var function = tool.Create(services);
            Assert.Equal(tool.Name, function.Name);
            Assert.Equal(tool.Description, function.Description);
        }

        var queryLogs = MonitoringAssistantTools.QueryLogsTool.Create(services);
        var schema = queryLogs.JsonSchema.GetRawText();
        Assert.Contains("\"minimumLevel\"", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("cancellationToken", schema, StringComparison.Ordinal);
    }

    private MonitoringAssistantTools CreateMonitoringTools(ILogQueryService? logs = null, MyProject.Business.Services.DataAccess.ExceptionLogService? exceptions = null, IDatabaseUsageService? databaseUsage = null)
        => new(health: null!, logs ?? new RecordingLogQueryService(), logLevel: null!, exceptions!, databaseUsage!, audit, currentUser, emailTest: null!, notificationTest: null!);

    private sealed class FakeDatabaseUsageService(DatabaseUsageReport report) : IDatabaseUsageService
    {
        public Task<DatabaseUsageReport> GetReportAsync(CancellationToken cancellationToken = default) => Task.FromResult(report);
    }

    private sealed class RecordingLogQueryService : ILogQueryService
    {
        public List<LogQueryRequest> Requests { get; } = [];

        public string Message { get; init; } = "一筆日誌";

        public Task<LogQueryResult> QueryAsync(LogQueryRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new LogQueryResult
            {
                Entries = [new LogEntry { Timestamp = Now, Level = "WARN", Rank = LogLevelRank.Warn, Logger = "Test", Message = Message }],
                AppliedStartTime = request.StartTime,
                AppliedEndTime = request.EndTime,
            });
        }
    }
}
