using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Scheduling;
using MyProject.Web.Scheduling.Jobs;

namespace MyProject.Tests;

/// <summary>
/// 系統巡檢報告的 Web API 與日報作業（0.10.39 起），在真的網站主機裡跑：只限管理員；立即產生只放進佇列並寫稽核；
/// 日報作業從頭到尾跑一次（收集、規則、存檔），報告內容不帶任何路徑；舊版巡檢報告搬到這裡讀。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class SystemReportsApiTests : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "Passw0rd-1";
    private const string BaseUrl = "/api/v1/system-reports";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ApiTestApplicationFactory factory;

    public SystemReportsApiTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task NonAdmin_IsForbidden()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: false));

        var response = await client.PostAsJsonAsync($"{BaseUrl}/search", new SystemReportSearchRequestDto());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Generate_RejectsTodayAndFuture_AndUnknownFrequencies()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var today = DateTime.Today;

        async Task<(HttpStatusCode Status, string Body)> Generate(string frequency, DateTime date)
        {
            var response = await client.PostAsJsonAsync($"{BaseUrl}/generate", new SystemReportGenerateRequestDto { Frequency = frequency, Date = date });
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        var todayResponse = await Generate(SystemReportFrequencies.Daily, today);
        var thisWeek = await Generate(SystemReportFrequencies.Weekly, today);
        var thisMonth = await Generate(SystemReportFrequencies.Monthly, today);
        var custom = await Generate(SystemReportFrequencies.Custom, today.AddDays(-3));
        var tooOldWeekly = await Generate(SystemReportFrequencies.Weekly, today.AddDays(-400));

        Assert.Equal(HttpStatusCode.BadRequest, todayResponse.Status);
        Assert.Equal(HttpStatusCode.BadRequest, thisWeek.Status);
        Assert.Contains("這一週還沒過完", thisWeek.Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, thisMonth.Status);
        Assert.Contains("這個月還沒過完", thisMonth.Body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, custom.Status);
        Assert.Equal(HttpStatusCode.BadRequest, tooOldWeekly.Status);
        Assert.Contains("365 天內的週報", tooOldWeekly.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_LastWeek_QueuesTheWeeklyJob_AndAudits()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var date = DateTime.Today.AddDays(-7);

        var response = await client.PostAsJsonAsync($"{BaseUrl}/generate", new SystemReportGenerateRequestDto { Frequency = SystemReportFrequencies.Weekly, Date = date });
        var list = await SendAsync<SystemReportListDto>(client, HttpMethod.Post, $"{BaseUrl}/search", new SystemReportSearchRequestDto());

        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        Assert.Equal((365, 1095), (list.WeeklyRetentionDays, list.MonthlyRetentionDays));
        if (response.StatusCode == HttpStatusCode.OK)
        {
            await using var context = await CreateContextAsync();
            var audit = await context.AuditLog.SingleAsync(x => x.Action == AuditActions.Job.Trigger && x.ActorUserId == admin.Id);
            Assert.Equal(("ScheduledJob", SystemReportWeeklyJob.JobName), (audit.TargetType, audit.TargetId));
            Assert.Equal($"job=SystemReportWeekly; date={date:yyyy-MM-dd}; send=False; via=system-reports", audit.Detail);
        }
    }

    [Fact]
    public async Task MonthlyJob_AtQuarterEnd_HasEverySection_AndScheduledWeeklyAlwaysNotifiesAdmins()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        using (var scope = factory.Services.CreateScope())
        {
            var monthly = await scope.ServiceProvider.GetRequiredService<SystemReportMonthlyJob>().ExecuteAsync(
                new ScheduledJobContext(0, JobRunTriggers.Manual, null, admin.Account, admin.Id, new SystemReportJobArguments(new DateTime(2004, 12, 15), SendToAdmins: false).ToJson()),
                CancellationToken.None);
            Assert.True(monthly.Succeeded, monthly.Message);
            Assert.StartsWith("2004/12/01～2004/12/31 的月報：", monthly.Message, StringComparison.Ordinal);

            // 排程的週報：時段 2005/03/07（星期一）→ 2005/02/28～2005/03/06，不論燈號都通知管理員。
            var slotUtc = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2005, 3, 7, 8, 0, 0), TimeZoneInfo.Local);
            var weekly = await scope.ServiceProvider.GetRequiredService<SystemReportWeeklyJob>().ExecuteAsync(
                new ScheduledJobContext(0, JobRunTriggers.Schedule, slotUtc, null), CancellationToken.None);
            Assert.True(weekly.Succeeded, weekly.Message);
            Assert.StartsWith("2005/02/28～2005/03/06 的週報：", weekly.Message, StringComparison.Ordinal);
            Assert.EndsWith("已通知管理員並寄信。", weekly.Message, StringComparison.Ordinal);
        }

        await using var db = await CreateContextAsync();
        var monthlyReport = await db.SystemReport.SingleAsync(x => x.Frequency == SystemReportFrequencies.Monthly && x.PeriodStart == new DateTime(2004, 12, 1));
        var weeklyReport = await db.SystemReport.SingleAsync(x => x.Frequency == SystemReportFrequencies.Weekly && x.PeriodStart == new DateTime(2005, 2, 28));
        Assert.Equal(new DateTime(2005, 1, 1), monthlyReport.PeriodEnd);
        Assert.Equal(SystemReportDeliveryStatuses.Sent, weeklyReport.DeliveryStatus);
        Assert.True(await db.Notification.AnyAsync(x => x.RecipientUserId == admin.Id && x.Link!.Contains(weeklyReport.ReportId.ToString("N"))));

        var text = await (await client.GetAsync($"{BaseUrl}/{monthlyReport.ReportId}")).Content.ReadAsStringAsync();
        var content = JsonSerializer.Deserialize<ApiResult<SystemReportDetailDto>>(text, Json)!.Data!.Content!;
        Assert.Equal(
            [SystemReportSectionKeys.Trend, SystemReportSectionKeys.Growth, SystemReportSectionKeys.BackupVerify, SystemReportSectionKeys.AiCost, SystemReportSectionKeys.AccountReview],
            content.Sections.Select(x => x.Key));
        Assert.Equal("帳號與權限清查（2004/10～2004/12）", content.Sections[^1].Title);
        Assert.Contains(content.Sections[^1].Tables[0].Rows, row => row[0] == admin.Account);
    }

    [Fact]
    public async Task Generate_QueuesTheDailyJob_WithArguments_AndAudits()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var date = DateTime.Today.AddDays(-1);

        var response = await client.PostAsJsonAsync($"{BaseUrl}/generate", new SystemReportGenerateRequestDto { Frequency = SystemReportFrequencies.Daily, Date = date, SendToAdmins = true });

        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        if (response.StatusCode == HttpStatusCode.OK)
        {
            await using var context = await CreateContextAsync();
            var audit = await context.AuditLog.SingleAsync(x => x.Action == AuditActions.Job.Trigger && x.ActorUserId == admin.Id);
            Assert.Equal(("ScheduledJob", SystemReportDailyJob.JobName), (audit.TargetType, audit.TargetId));
            Assert.Equal($"job=SystemReportDaily; date={date:yyyy-MM-dd}; send=True; via=system-reports", audit.Detail);
        }
    }

    [Fact]
    public async Task DailyJob_GeneratesAReport_WithoutPaths_AndOnlyTellsTheRequester()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var date = new DateTime(2001, 2, 3);

        using (var scope = factory.Services.CreateScope())
        {
            var job = scope.ServiceProvider.GetRequiredService<SystemReportDailyJob>();
            var result = await job.ExecuteAsync(
                new ScheduledJobContext(0, JobRunTriggers.Manual, null, admin.Account, admin.Id, new SystemReportJobArguments(date, SendToAdmins: false).ToJson()),
                CancellationToken.None);
            Assert.True(result.Succeeded, result.Message);
            Assert.StartsWith("2001/02/03 的日報：", result.Message, StringComparison.Ordinal);
        }

        var list = await SendAsync<SystemReportListDto>(client, HttpMethod.Post, $"{BaseUrl}/search",
            new SystemReportSearchRequestDto { Frequency = SystemReportFrequencies.Daily, PageSize = 200 });
        var item = Assert.Single(list.Items, x => x.PeriodStart == date && x.TriggeredByAccount == admin.Account);
        Assert.Equal((date.AddDays(1), SystemReportTriggers.Manual, SystemReportDeliveryStatuses.NotRequired), (item.PeriodEnd, item.Trigger, item.DeliveryStatus));
        Assert.True(list.DailyRetentionDays > 0);

        var text = await (await client.GetAsync($"{BaseUrl}/{item.ReportId}")).Content.ReadAsStringAsync();
        var detail = JsonSerializer.Deserialize<ApiResult<SystemReportDetailDto>>(text, Json)!.Data!;
        var content = Assert.IsType<SystemReportContentDto>(detail.Content);
        Assert.Equal(item.Level, content.Level);
        Assert.False(string.IsNullOrWhiteSpace(content.Verdict));
        Assert.NotEmpty(content.Predictions);
        Assert.Equal(SystemReportNarrativeStatuses.Skipped, content.NarrativeStatus);

        // 報告會寄出系統外：內容不可以出現任何設定的路徑（只有磁碟根目錄與用途名稱）。
        var paths = factory.Services.GetRequiredService<IOptions<SystemSettings>>().Value.ExternalFileSystem;
        foreach (var path in new[] { paths.BackupPath, paths.ProjectFilePath, paths.AiAgentConversationPath, paths.DatabasePath }.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            Assert.DoesNotContain(path.Replace("\\", "\\\\"), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(path, text, StringComparison.OrdinalIgnoreCase);
        }

        await using var context = await CreateContextAsync();
        var notifications = await context.Notification.Where(x => x.Category == NotificationCategories.SystemReport && x.Link!.Contains(item.ReportId.ToString("N"))).ToListAsync();
        var notification = Assert.Single(notifications);
        Assert.Equal(admin.Id, notification.RecipientUserId);
        Assert.StartsWith("報告已產生：系統巡檢日報 2001/02/03", notification.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScheduledDailyJob_RunsOncePerDay_AndNotifiesAdminsWhenOrangeOrWorse()
    {
        var admin = await AddUserAsync(isAdmin: true);
        // 時段 2002/04/06 07:30（本地）→ 日報是 2002/04/05。
        var slotUtc = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2002, 4, 6, 7, 30, 0), TimeZoneInfo.Local);
        var context = new ScheduledJobContext(0, JobRunTriggers.Schedule, slotUtc, null);

        string? first;
        string? second;
        using (var scope = factory.Services.CreateScope())
        {
            first = (await scope.ServiceProvider.GetRequiredService<SystemReportDailyJob>().ExecuteAsync(context, CancellationToken.None)).Message;
        }

        using (var scope = factory.Services.CreateScope())
        {
            second = (await scope.ServiceProvider.GetRequiredService<SystemReportDailyJob>().ExecuteAsync(context, CancellationToken.None)).Message;
        }

        Assert.StartsWith("2002/04/05 的日報：", first, StringComparison.Ordinal);
        Assert.Equal("2002/04/05 的日報已經產生過，這次略過。", second);
        await using var db = await CreateContextAsync();
        var report = await db.SystemReport.SingleAsync(x => x.PeriodStart == new DateTime(2002, 4, 5));
        var notified = await db.Notification.AnyAsync(x => x.Category == NotificationCategories.SystemReport && x.RecipientUserId == admin.Id && x.Link!.Contains(report.ReportId.ToString("N")));
        var shouldNotify = report.Level is SystemReportLevels.Red or SystemReportLevels.Orange;
        Assert.Equal(shouldNotify, notified);
        Assert.Equal(shouldNotify ? SystemReportDeliveryStatuses.Sent : SystemReportDeliveryStatuses.NotRequired, report.DeliveryStatus);
    }

    /// <summary>
    /// 系統管理助理的巡檢工具（0.10.42 起，M4）：沒講日期＝昨天到現在；存成 Custom／Assistant、不請 AI 寫說明、不通知；
    /// 寫 SystemReport.Generate 稽核，並把報告交給對話（回覆下方的按鈕）；區間不合法時不產生、回錯誤給模型。
    /// </summary>
    [Fact]
    public async Task AssistantTool_GeneratesACustomReport_WithoutAiOrNotifications_AndAudits()
    {
        var admin = await AddUserAsync(isAdmin: true);
        string json;
        string error;
        IReadOnlyList<MyProject.Web.Ai.Agents.AiAgentReportOutput> handedOver;
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentUserService>().CurrentUser = new MyProject.Models.Others.CurrentUser { Id = admin.Id, Account = admin.Account };
            var tools = scope.ServiceProvider.GetRequiredService<MyProject.Web.Ai.Agents.AdminAssistant.ReportAssistantTools>();
            json = await tools.RunSystemReportAsync();
            error = await tools.RunSystemReportAsync("2026-01-01", "2026-03-31");
            handedOver = scope.ServiceProvider.GetRequiredService<MyProject.Web.Ai.Agents.AiAgentReportOutputs>().Drain();
        }

        await using var db = await CreateContextAsync();
        var report = await db.SystemReport.SingleAsync(x => x.TriggeredByAccount == admin.Account);
        Assert.Equal((SystemReportFrequencies.Custom, SystemReportTriggers.Assistant, SystemReportNarrativeStatuses.Skipped, SystemReportDeliveryStatuses.NotRequired),
            (report.Frequency, report.Trigger, report.NarrativeStatus, report.DeliveryStatus));
        Assert.Equal((DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1)), (report.PeriodStart, report.PeriodEnd));
        Assert.Contains("\"saved\":true", json, StringComparison.Ordinal);
        Assert.Contains($"\"period\":\"{DateTime.Today.AddDays(-1):yyyy/MM/dd} 00:00～{DateTime.Today:yyyy/MM/dd} ", json, StringComparison.Ordinal);
        Assert.Contains("\"level\":", json, StringComparison.Ordinal);
        Assert.Contains("\"error\":", error, StringComparison.Ordinal);
        var output = Assert.Single(handedOver);
        Assert.Equal(report.ReportId, output.ReportId);
        Assert.StartsWith("系統巡檢指定區間 ", output.Title, StringComparison.Ordinal);

        var reportService = factory.Services.GetRequiredService<SystemReportService>();
        var content = JsonSerializer.Deserialize<SystemReportContentDto>((await reportService.GetAsync(report.ReportId))!.ContentJson!, MyProject.Web.SystemReports.SystemReportGenerator.ContentJsonOptions)!;
        Assert.Null(content.Narrative);
        Assert.Equal(MyProject.Web.SystemReports.SystemReportAssistantService.NarrativeNote, content.NarrativeNote);
        Assert.Empty(content.Sections);

        Assert.False(await db.Notification.AnyAsync(x => x.Link!.Contains(report.ReportId.ToString("N"))));
        var audit = await db.AuditLog.SingleAsync(x => x.Action == AuditActions.SystemReport.Generate && x.ActorUserId == admin.Id);
        Assert.Equal(("SystemReport", report.ReportId.ToString("N")), (audit.TargetType, audit.TargetId));
        Assert.Equal($"frequency=Custom; start={DateTime.Today.AddDays(-1):yyyy-MM-dd}; end={DateTime.Today:yyyy-MM-dd}; via=admin-assistant", audit.Detail);
    }

    [Fact]
    public async Task CapacitySnapshotJob_RecordsTodaysSnapshot()
    {
        using var scope = factory.Services.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<CapacitySnapshotJob>().ExecuteAsync(
            new ScheduledJobContext(0, JobRunTriggers.Manual, null, "support"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.StartsWith("記錄 ", result.Message, StringComparison.Ordinal);
        Assert.True(await scope.ServiceProvider.GetRequiredService<CapacitySnapshotService>().HasDayAsync(DateTime.Today));
        await using var db = await CreateContextAsync();
        Assert.Contains(await db.CapacitySnapshot.Where(x => x.SnapshotDate == DateTime.Today).Select(x => x.Key).ToListAsync(), key => key == "Database.Main");
    }

    [Fact]
    public async Task Pdf_DownloadsTheReport_AndAudits()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var date = new DateTime(2003, 5, 6);
        using (var scope = factory.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<SystemReportDailyJob>().ExecuteAsync(
                new ScheduledJobContext(0, JobRunTriggers.Manual, null, admin.Account, admin.Id, new SystemReportJobArguments(date, SendToAdmins: false).ToJson()),
                CancellationToken.None);
            Assert.True(result.Succeeded, result.Message);
        }

        Guid reportId;
        await using (var db = await CreateContextAsync())
        {
            reportId = (await db.SystemReport.SingleAsync(x => x.PeriodStart == date)).ReportId;
        }

        var response = await client.PostAsync($"{BaseUrl}/{reportId}/pdf", null);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var missing = await client.PostAsync($"{BaseUrl}/{Guid.NewGuid()}/pdf", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("system-report-daily-20030506.pdf", response.Content.Headers.ContentDisposition!.FileNameStar ?? response.Content.Headers.ContentDisposition.FileName);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await using var check = await CreateContextAsync();
        var audit = await check.AuditLog.SingleAsync(x => x.Action == AuditActions.SystemReport.Download && x.ActorUserId == admin.Id);
        Assert.Equal(("SystemReport", reportId.ToString("N"), "format=pdf; frequency=Daily"), (audit.TargetType, audit.TargetId, audit.Detail));
    }

    [Fact]
    public async Task Get_Unknown_Is404()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));

        var response = await client.GetAsync($"{BaseUrl}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(MyProject.Web.Controllers.SystemReportsController.NotFoundMessage, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyReports_ListAndDetail_MovedHere()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var id = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            await LegacyPatrolReports.SeedAsync(
                scope.ServiceProvider.GetRequiredService<IDbContextFactory<BackendDBContext>>(),
                scope.ServiceProvider.GetRequiredService<IOptions<SystemSettings>>().Value.ExternalFileSystem.AiAgentConversationPath,
                new LegacyPatrolReport(id, DateTime.Now.AddDays(-1), AdminPatrolStatuses.Abnormal, "排程與備份", JobRunTriggers.Manual, "support", 1, "gpt-test", false,
                    "## 總結\n**備份失敗**"));
        }

        var list = await SendAsync<AdminPatrolReportListDto>(client, HttpMethod.Get, $"{BaseUrl}/legacy");
        var detail = await SendAsync<AdminPatrolReportDetailDto>(client, HttpMethod.Get, $"{BaseUrl}/legacy/{id}");
        var missing = await client.GetAsync($"{BaseUrl}/legacy/{Guid.NewGuid()}");
        var oldRoute = await client.GetAsync($"/api/v1/admin-assistant/patrol-reports/{id}");

        Assert.Contains(list.Items, x => x.ReportId == id && x.Status == "異常");
        Assert.Equal("## 總結\n**備份失敗**", detail.Markdown);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, oldRoute.StatusCode);
    }

    private static async Task<T> SendAsync<T>(HttpClient client, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{method} {url} → {(int)response.StatusCode} {text}");
        return JsonSerializer.Deserialize<ApiResult<T>>(text, Json)!.Data!;
    }

    private async Task<HttpClient> SignedInClientAsync(MyUser user)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/Auth/login", new LoginRequestDto { Account = user.Account, Password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var tokens = (await login.Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())!.Data!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private async Task<MyUser> AddUserAsync(bool isAdmin)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var writer = scope.ServiceProvider.GetRequiredService<IRbacWriteService>();
        var role = await context.RoleView.FirstAsync(x => x.Name == MagicObjectHelper.預設角色);
        var account = $"sr-{Guid.NewGuid():N}"[..13];
        var user = new MyUser
        {
            Account = account,
            Name = account,
            Password = SecurePasswordHasher.HashPassword(Password),
            Status = true,
            IsAdmin = isAdmin,
            RoleViewId = role.Id,
            PasswordChangedAtUtc = DateTime.UtcNow,
        };
        context.MyUser.Add(user);
        await context.SaveChangesAsync();
        await writer.SyncUserRolesAsync(user.Id, [role.Id]);
        return user;
    }

    private Task<BackendDBContext> CreateContextAsync()
        => factory.Services.GetRequiredService<IDbContextFactory<BackendDBContext>>().CreateDbContextAsync();
}
