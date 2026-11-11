using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Shell;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Scheduling;

namespace MyProject.Tests;

/// <summary>
/// 排程作業頁的 Web API（0.10.6 起，WASM 遷移 M7）：只限管理員；總覽、立即執行、啟用切換、執行紀錄，稽核與 Server 版頁面相同。
/// 排程器是伺服器的單例狀態，API 只讀與觸發；「執行中」的進度靠畫面輪詢總覽。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class ScheduledJobApiTests : IClassFixture<ApiTestApplicationFactoryWithGatedJob>
{
    private readonly ApiTestApplicationFactoryWithGatedJob factory;
    private readonly OpsApiTestUsers users;

    public ScheduledJobApiTests(ApiTestApplicationFactoryWithGatedJob factory)
    {
        this.factory = factory;
        users = new OpsApiTestUsers(factory);
    }

    [Fact]
    public async Task Overview_ShouldListEveryJob_WithLocalTimesSentWithoutAnOffset()
    {
        using var client = await users.SignedInClientAsync(await users.AddUserAsync(isAdmin: true));

        var response = await client.GetAsync("/api/v1/scheduled-jobs");
        var text = await response.Content.ReadAsStringAsync();
        var overview = OpsApiTestUsers.Read<ScheduledJobOverviewDto>(response.StatusCode, text);

        var descriptors = factory.Services.GetServices<ScheduledJobDescriptor>().ToList();
        Assert.Equal(descriptors.Select(x => x.Name), overview.Items.Select(x => x.Name));
        var gated = overview.Items.Single(x => x.Name == GatedJob.Name);
        Assert.Equal(GatedJob.DisplayName, gated.DisplayName);
        Assert.Equal("0 4 * * *", gated.Cron);
        Assert.True(gated.IsEnabled);
        Assert.NotNull(gated.NextRunLocal);
        Assert.Null(overview.SettingsError);
        OpsApiTestUsers.AssertNoOffset(text);
    }

    /// <summary>⭐ 立即執行：放進佇列、總覽看得到執行中（畫面據此輪詢）、執行中再按回 409 不寫稽核、完成後看得到結果與執行紀錄。</summary>
    [Fact]
    public async Task Trigger_ShouldRunTheJob_ShowItAsRunning_RejectASecondTrigger_AndAuditOnce()
    {
        var admin = await users.AddUserAsync(isAdmin: true);
        using var client = await users.SignedInClientAsync(admin);
        GatedJob.Reset();

        try
        {
            var first = await client.PostAsync($"/api/v1/scheduled-jobs/{GatedJob.Name}/trigger", null);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

            var running = await WaitForAsync(client, x => x.LastRun?.Status == JobRunStatuses.Running);
            Assert.True(running.IsQueued);
            Assert.Equal(DateTimeKind.Unspecified, running.LastRun!.StartedAtLocal.Kind);

            var second = await client.PostAsync($"/api/v1/scheduled-jobs/{GatedJob.Name}/trigger", null);
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            var conflict = await second.Content.ReadFromJsonAsync<ApiResult<object>>();
            Assert.Equal("這個作業已經在執行中，請等它完成。", conflict!.ErrorMessage);
        }
        finally
        {
            GatedJob.Release();
        }

        var finished = await WaitForAsync(client, x => x.LastRun?.Status == JobRunStatuses.Succeeded && !x.IsQueued);
        Assert.Equal("閘門已開", finished.LastRun!.Message);

        var runs = await OpsApiTestUsers.SendAsync<List<ScheduledJobRunDto>>(client, HttpMethod.Get, $"/api/v1/scheduled-jobs/{GatedJob.Name}/runs");
        var run = runs.First();
        Assert.Equal(JobRunTriggers.Manual, run.Trigger);
        Assert.Equal(admin.Account, run.TriggeredByAccount);
        Assert.Equal(JobRunStatuses.Succeeded, run.Status);

        var audit = Assert.Single(await users.AuditsAsync(AuditActions.Job.Trigger, admin.Id));
        Assert.Equal("ScheduledJob", audit.TargetType);
        Assert.Equal(GatedJob.Name, audit.TargetId);
        Assert.Equal($"job={GatedJob.Name}", audit.Detail);
        Assert.True(audit.Success);
    }

    [Fact]
    public async Task SetEnabled_ShouldToggleTheJob_AndAuditEnableOrDisable()
    {
        var admin = await users.AddUserAsync(isAdmin: true);
        using var client = await users.SignedInClientAsync(admin);
        const string job = "NotificationRetention";

        var disabled = await client.PutAsJsonAsync($"/api/v1/scheduled-jobs/{job}/enabled", new ScheduledJobEnabledRequestDto { Enabled = false });
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        var item = (await OpsApiTestUsers.SendAsync<ScheduledJobOverviewDto>(client, HttpMethod.Get, "/api/v1/scheduled-jobs")).Items.Single(x => x.Name == job);
        Assert.False(item.IsEnabled);
        Assert.Null(item.NextRunLocal);

        // 網址的大小寫不同也是同一個作業；稽核記穩定的名稱。
        var enabled = await client.PutAsJsonAsync($"/api/v1/scheduled-jobs/{job.ToLowerInvariant()}/enabled", new ScheduledJobEnabledRequestDto { Enabled = true });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.True((await OpsApiTestUsers.SendAsync<ScheduledJobOverviewDto>(client, HttpMethod.Get, "/api/v1/scheduled-jobs")).Items.Single(x => x.Name == job).IsEnabled);

        var disableAudit = Assert.Single(await users.AuditsAsync(AuditActions.Job.Disable, admin.Id));
        Assert.Equal(("ScheduledJob", job, $"job={job}"), (disableAudit.TargetType, disableAudit.TargetId, disableAudit.Detail));
        var enableAudit = Assert.Single(await users.AuditsAsync(AuditActions.Job.Enable, admin.Id));
        Assert.Equal(("ScheduledJob", job, $"job={job}"), (enableAudit.TargetType, enableAudit.TargetId, enableAudit.Detail));
    }

    [Fact]
    public async Task UnknownJob_ShouldReturn404_WithoutAnAudit()
    {
        var admin = await users.AddUserAsync(isAdmin: true);
        using var client = await users.SignedInClientAsync(admin);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/v1/scheduled-jobs/NoSuchJob/trigger", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/v1/scheduled-jobs/NoSuchJob/enabled", new ScheduledJobEnabledRequestDto { Enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/scheduled-jobs/NoSuchJob/runs")).StatusCode);
        Assert.Empty(await users.AuditsAsync(prefix: "Job.", actorId: admin.Id));
    }

    [Fact]
    public async Task NonAdmins_ShouldBeForbiddenFromEveryEndpoint_AndTheDenialAudited()
    {
        var user = await users.AddUserAsync(isAdmin: false);
        using var client = await users.SignedInClientAsync(user);

        await users.AssertForbiddenAsync(client, user,
        [
            (HttpMethod.Get, "/api/v1/scheduled-jobs", null),
            (HttpMethod.Post, $"/api/v1/scheduled-jobs/{GatedJob.Name}/trigger", null),
            (HttpMethod.Put, $"/api/v1/scheduled-jobs/{GatedJob.Name}/enabled", new ScheduledJobEnabledRequestDto { Enabled = false }),
            (HttpMethod.Get, $"/api/v1/scheduled-jobs/{GatedJob.Name}/runs", null),
        ]);
        Assert.Empty(await users.AuditsAsync(prefix: "Job.", actorId: user.Id));
    }

    private static async Task<ScheduledJobItemDto> WaitForAsync(HttpClient client, Func<ScheduledJobItemDto, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var item = (await OpsApiTestUsers.SendAsync<ScheduledJobOverviewDto>(client, HttpMethod.Get, "/api/v1/scheduled-jobs"))
                .Items.Single(x => x.Name == GatedJob.Name);
            if (condition(item) || DateTime.UtcNow > deadline)
            {
                Assert.True(condition(item), $"等不到預期的狀態：{item.LastRun?.Status}, IsQueued={item.IsQueued}");
                return item;
            }

            await Task.Delay(100);
        }
    }
}

/// <summary>系統健康監控頁的 Web API（0.10.6 起）：只限管理員；健康報告、24 小時警告日誌、寄出測試信、發送測試通知。</summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class SystemHealthApiTests : IClassFixture<ApiTestApplicationFactoryWithPickupEmail>
{
    private readonly OpsApiTestUsers users;

    public SystemHealthApiTests(ApiTestApplicationFactoryWithPickupEmail factory)
    {
        users = new OpsApiTestUsers(factory);
    }

    [Fact]
    public async Task Report_ShouldReturnEveryCheck_AndTheEmailSettings()
    {
        var admin = await users.AddUserAsync(isAdmin: true, email: "ops-admin@e2e.test");
        using var client = await users.SignedInClientAsync(admin);

        var response = await client.GetAsync("/api/v1/system-health");
        var text = await response.Content.ReadAsStringAsync();
        var report = OpsApiTestUsers.Read<SystemHealthReportDto>(response.StatusCode, text);

        Assert.Contains(report.Items, x => x.Name == "資料庫" && x.Category == "Database" && x.Weight == 25);
        Assert.Contains(report.Items, x => x.Name == "LLM API");
        Assert.InRange(report.Score, 0, 100);
        Assert.Contains(report.Status, new[] { "Healthy", "Degraded", "Unhealthy" });
        Assert.Contains(report.Light, new[] { "Green", "Yellow", "Red" });
        Assert.True(report.IsEmailEnabled);
        Assert.Equal("Pickup", report.EmailProviderText);
        Assert.Equal("ops-admin@e2e.test", report.DefaultTestEmailTo);
        Assert.Equal(DateTimeKind.Unspecified, report.CheckedAtLocal.Kind);
        OpsApiTestUsers.AssertNoOffset(text);
    }

    [Fact]
    public async Task WarnLogs_ShouldDescribeTheLast24Hours_WithoutAnOffset()
    {
        using var client = await users.SignedInClientAsync(await users.AddUserAsync(isAdmin: true));

        var response = await client.GetAsync("/api/v1/system-health/warn-logs");
        var text = await response.Content.ReadAsStringAsync();
        var logs = OpsApiTestUsers.Read<SystemHealthWarnLogsDto>(response.StatusCode, text);

        Assert.False(string.IsNullOrWhiteSpace(logs.Message) && string.IsNullOrWhiteSpace(logs.EmptyText));
        Assert.All(logs.Entries, x => Assert.Contains(x.Level, new[] { "WARN", "ERROR", "FATAL" }));
        Assert.Equal(logs.Entries.OrderByDescending(x => x.Sequence).Select(x => x.Sequence), logs.Entries.Select(x => x.Sequence));
        OpsApiTestUsers.AssertNoOffset(text);
    }

    [Fact]
    public async Task TestEmail_ShouldWriteToPickup_AndAuditOnce_ButRejectAnInvalidAddressWithoutAnAudit()
    {
        var admin = await users.AddUserAsync(isAdmin: true);
        using var client = await users.SignedInClientAsync(admin);

        var invalid = await OpsApiTestUsers.SendAsync<SystemHealthTestResultDto>(client, HttpMethod.Post, "/api/v1/system-health/test-email", new SystemHealthTestEmailRequestDto { To = "not-an-email" });
        Assert.False(invalid.Success);
        Assert.Equal("請輸入有效的收件者 Email。", invalid.Message);
        Assert.Empty(await users.AuditsAsync(AuditActions.Email.Test, admin.Id));

        var sent = await OpsApiTestUsers.SendAsync<SystemHealthTestResultDto>(client, HttpMethod.Post, "/api/v1/system-health/test-email", new SystemHealthTestEmailRequestDto { To = "ops@e2e.test" });
        Assert.True(sent.Success);
        Assert.Equal("測試信已寫入 Pickup 資料夾（不會真正寄出）。", sent.Message);
        var audit = Assert.Single(await users.AuditsAsync(AuditActions.Email.Test, admin.Id));
        Assert.Equal("provider=Pickup", audit.Detail);
        Assert.Equal(admin.Account, audit.ActorAccount);
    }

    [Fact]
    public async Task TestEmail_WithAnOverlongAddress_ShouldReturn400()
    {
        using var client = await users.SignedInClientAsync(await users.AddUserAsync(isAdmin: true));

        var response = await client.PostAsJsonAsync("/api/v1/system-health/test-email", new SystemHealthTestEmailRequestDto { To = new string('a', 400) + "@e2e.test" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TestNotification_ShouldNotifyEveryAdmin_AndAuditOnce()
    {
        var admin = await users.AddUserAsync(isAdmin: true);
        using var client = await users.SignedInClientAsync(admin);

        var result = await OpsApiTestUsers.SendAsync<SystemHealthTestResultDto>(client, HttpMethod.Post, "/api/v1/system-health/test-notification");

        Assert.True(result.Success);
        Assert.Matches(@"^已發送給 \d+ 位管理員，請看右上角的鈴鐺。$", result.Message);
        var audit = Assert.Single(await users.AuditsAsync(AuditActions.Notification.Test, admin.Id));
        Assert.StartsWith("recipients=", audit.Detail);
        await using var context = await users.CreateContextAsync();
        Assert.True(await context.Notification.AnyAsync(x => x.RecipientUserId == admin.Id && x.Title == "測試通知"));
    }

    [Fact]
    public async Task NonAdmins_ShouldBeForbiddenFromEveryEndpoint_AndTheDenialAudited()
    {
        var user = await users.AddUserAsync(isAdmin: false);
        using var client = await users.SignedInClientAsync(user);

        await users.AssertForbiddenAsync(client, user,
        [
            (HttpMethod.Get, "/api/v1/system-health", null),
            (HttpMethod.Get, "/api/v1/system-health/warn-logs", null),
            (HttpMethod.Post, "/api/v1/system-health/test-email", new SystemHealthTestEmailRequestDto { To = "x@e2e.test" }),
            (HttpMethod.Post, "/api/v1/system-health/test-notification", null),
        ]);
        Assert.Empty(await users.AuditsAsync(AuditActions.Email.Test, user.Id));
        Assert.Empty(await users.AuditsAsync(AuditActions.Notification.Test, user.Id));
    }
}

/// <summary>
/// 登入後首頁的 Web API（0.10.6 起）：伺服器依權限決定快速入口與看得到的小工具，每個小工具的資料各有端點；
/// 「排程作業」小工具只限管理員。時間都是伺服器本地時間、不帶時差。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class DashboardApiTests : IClassFixture<ApiTestApplicationFactory>
{
    private readonly OpsApiTestUsers users;

    public DashboardApiTests(ApiTestApplicationFactory factory)
    {
        users = new OpsApiTestUsers(factory);
    }

    [Fact]
    public async Task Dashboard_ForAnAdmin_ShouldShowEveryWidgetAndQuickLink()
    {
        using var client = await users.SignedInClientAsync(await users.AddUserAsync(isAdmin: true));

        var dashboard = await OpsApiTestUsers.SendAsync<DashboardDto>(client, HttpMethod.Get, "/api/v1/dashboard");

        Assert.Equal(["notifications", "account", "scheduled-jobs"], dashboard.Widgets.Select(x => x.Id));
        Assert.Equal(["我的通知", "我的帳號", "排程作業"], dashboard.Widgets.Select(x => x.Title));
        Assert.Equal(["notifications", "account_circle", "schedule"], dashboard.Widgets.Select(x => x.Icon));
        Assert.Equal(["/projects", "/categories", "/teams"], dashboard.QuickLinks.Select(x => x.Url));
        Assert.False(string.IsNullOrWhiteSpace(dashboard.SystemName));
        Assert.False(string.IsNullOrWhiteSpace(dashboard.SystemVersion));
        Assert.Equal("Testing", dashboard.EnvironmentName);
    }

    [Fact]
    public async Task Dashboard_ForAUserWithHomeAndProjectsOnly_ShouldHideTheAdminWidgetAndOtherLinks()
    {
        var user = await users.AddUserAsync(isAdmin: false, permissions: [MagicObjectHelper.角色_首頁, MagicObjectHelper.角色_專案項目]);
        using var client = await users.SignedInClientAsync(user);

        var dashboard = await OpsApiTestUsers.SendAsync<DashboardDto>(client, HttpMethod.Get, "/api/v1/dashboard");

        Assert.Equal(["notifications", "account"], dashboard.Widgets.Select(x => x.Id));
        Assert.Equal(["/projects"], dashboard.QuickLinks.Select(x => x.Url));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/dashboard/scheduled-jobs")).StatusCode);
    }

    [Fact]
    public async Task Dashboard_WithoutTheHomePermission_ShouldBeForbidden()
    {
        var user = await users.AddUserAsync(isAdmin: false, permissions: [MagicObjectHelper.角色_專案項目]);
        using var client = await users.SignedInClientAsync(user);

        await users.AssertForbiddenAsync(client, user,
        [
            (HttpMethod.Get, "/api/v1/dashboard", null),
            (HttpMethod.Get, "/api/v1/dashboard/account", null),
            (HttpMethod.Get, "/api/v1/dashboard/notifications", null),
            (HttpMethod.Get, "/api/v1/dashboard/scheduled-jobs", null),
        ]);
    }

    [Fact]
    public async Task Account_ShouldShowThePreviousLogin_NotThisOne()
    {
        var user = await users.AddUserAsync(isAdmin: false, permissions: [MagicObjectHelper.角色_首頁]);
        using var client = await users.SignedInClientAsync(user);
        // 用未來的時間，確保「這一次」與「上一次」不受 API 登入本身寫的稽核影響。
        var previousUtc = DateTime.UtcNow.AddMinutes(10);
        await users.AddLoginAuditAsync(user, AuditActions.Login.SsoSuccess, previousUtc, "198.51.100.7");
        await users.AddLoginAuditAsync(user, AuditActions.Login.Success, previousUtc.AddMinutes(5), "203.0.113.9");

        var response = await client.GetAsync("/api/v1/dashboard/account");
        var text = await response.Content.ReadAsStringAsync();
        var account = OpsApiTestUsers.Read<DashboardAccountDto>(response.StatusCode, text);

        Assert.Equal("Google 登入", account.PreviousLoginMethod);
        Assert.Equal("198.51.100.7", account.PreviousLoginClientIp);
        Assert.Equal(DateTimeKind.Unspecified, account.PreviousLoginLocal!.Value.Kind);
        Assert.True(Math.Abs((account.PreviousLoginLocal.Value - TimeZoneInfo.ConvertTimeFromUtc(previousUtc, TimeZoneInfo.Local)).TotalSeconds) < 1);
        Assert.True(account.HasLocalPassword);
        Assert.False(account.TwoFactorEnabled);
        OpsApiTestUsers.AssertNoOffset(text);
    }

    [Fact]
    public async Task Notifications_ShouldShowTheUnreadCountAndTheLatestFive_WithOnlySafeLinks()
    {
        var user = await users.AddUserAsync(isAdmin: false, permissions: [MagicObjectHelper.角色_首頁]);
        using var client = await users.SignedInClientAsync(user);
        await using (var context = await users.CreateContextAsync())
        {
            for (var i = 1; i <= 6; i++)
            {
                context.Notification.Add(new Notification
                {
                    RecipientUserId = user.Id,
                    Category = "Test",
                    Title = $"通知{i}",
                    Link = i == 6 ? "https://evil.example/x" : "/projects",
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(i - 10),
                    ReadAtUtc = i == 1 ? DateTime.UtcNow : null,
                });
            }

            await context.SaveChangesAsync();
        }

        var response = await client.GetAsync("/api/v1/dashboard/notifications");
        var text = await response.Content.ReadAsStringAsync();
        var notifications = OpsApiTestUsers.Read<DashboardNotificationsDto>(response.StatusCode, text);

        Assert.Equal(5, notifications.UnreadCount);
        Assert.Equal(["通知6", "通知5", "通知4", "通知3", "通知2"], notifications.Items.Select(x => x.Title));
        Assert.Null(notifications.Items[0].Link);
        Assert.Equal("/projects", notifications.Items[1].Link);
        Assert.All(notifications.Items, x => Assert.False(x.IsRead));
        OpsApiTestUsers.AssertNoOffset(text);
    }

    [Fact]
    public async Task ScheduledJobsWidget_ShouldListTheJobsNeedingAttention()
    {
        var admin = await users.AddUserAsync(isAdmin: true);
        using var client = await users.SignedInClientAsync(admin);
        const string job = "EmailLogRetention";
        await client.PutAsJsonAsync($"/api/v1/scheduled-jobs/{job}/enabled", new ScheduledJobEnabledRequestDto { Enabled = false });

        try
        {
            var widget = await OpsApiTestUsers.SendAsync<DashboardScheduledJobsDto>(client, HttpMethod.Get, "/api/v1/dashboard/scheduled-jobs");
            var overview = await OpsApiTestUsers.SendAsync<ScheduledJobOverviewDto>(client, HttpMethod.Get, "/api/v1/scheduled-jobs");

            Assert.True(widget.SchedulingEnabled);
            Assert.Equal(overview.Items.Count, widget.JobCount);
            var disabled = Assert.Single(widget.Attention, x => x.DisplayName == overview.Items.Single(i => i.Name == job).DisplayName);
            Assert.Equal("已停用", disabled.Reason);
            Assert.True(disabled.IsDisabled);
        }
        finally
        {
            await client.PutAsJsonAsync($"/api/v1/scheduled-jobs/{job}/enabled", new ScheduledJobEnabledRequestDto { Enabled = true });
        }
    }
}

/// <summary>M7 維運頁 API 測試共用的工具：建立使用者、以 API 登入、讀稽核。</summary>
public sealed class OpsApiTestUsers
{
    private const string Password = "Passw0rd-1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ApiTestApplicationFactory factory;

    public OpsApiTestUsers(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    public static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..(prefix.Length + 10)];

    /// <summary>本地時間以不帶時差送出（WASM 才不會換成瀏覽器時區）；ApiResult 外層的回應時間不在範圍內。</summary>
    public static void AssertNoOffset(string text)
    {
        var offset = new Regex(@"T\d{2}:\d{2}:\d{2}(\.\d+)?([+-]\d{2}:\d{2}|Z)""");
        var envelope = new Regex("\"timestamp\":\"[^\"]*\"");
        Assert.False(offset.IsMatch(envelope.Replace(text, string.Empty)), text);
    }

    public static T Read<T>(HttpStatusCode status, string text)
    {
        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {text}");
        return JsonSerializer.Deserialize<ApiResult<T>>(text, JsonOptions)!.Data!;
    }

    public static async Task<T> SendAsync<T>(HttpClient client, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        var response = await client.SendAsync(request);
        return Read<T>(response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public async Task AssertForbiddenAsync(HttpClient client, MyUser user, (HttpMethod Method, string Url, object? Body)[] calls)
    {
        await using var before = await CreateContextAsync();
        var deniedBefore = await before.AuditLog.CountAsync(x => x.Action == AuditActions.Permission.Denied && x.ActorUserId == user.Id);

        foreach (var (method, url, body) in calls)
        {
            using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body, body.GetType()) };
            var response = await client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {url} → {(int)response.StatusCode}");
        }

        await using var after = await CreateContextAsync();
        Assert.Equal(deniedBefore + calls.Length, await after.AuditLog.CountAsync(x => x.Action == AuditActions.Permission.Denied && x.ActorUserId == user.Id));
    }

    public async Task<List<AuditLog>> AuditsAsync(string? action = null, int? actorId = null, string? prefix = null)
    {
        await using var context = await CreateContextAsync();
        return await context.AuditLog.AsNoTracking()
            .Where(x => x.ActorUserId == actorId)
            .Where(x => action == null || x.Action == action)
            .Where(x => prefix == null || x.Action.StartsWith(prefix))
            .ToListAsync();
    }

    public async Task AddLoginAuditAsync(MyUser user, string action, DateTime occurredAtUtc, string clientIp)
    {
        await using var context = await CreateContextAsync();
        context.AuditLog.Add(new AuditLog { Action = action, OccurredAt = occurredAtUtc, Success = true, ActorUserId = user.Id, ActorAccount = user.Account, ClientIp = clientIp });
        await context.SaveChangesAsync();
    }

    public async Task<HttpClient> SignedInClientAsync(MyUser user)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/Auth/login", new LoginRequestDto { Account = user.Account, Password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var tokens = (await login.Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())!.Data!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    /// <summary>新增使用者；給了 <paramref name="permissions"/> 就另建一個只有這些權限鍵的角色，否則用預設角色。</summary>
    public async Task<MyUser> AddUserAsync(bool isAdmin, string[]? permissions = null, string? email = null)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var writer = scope.ServiceProvider.GetRequiredService<IRbacWriteService>();

        RoleView role;
        if (permissions is null)
        {
            role = await context.RoleView.FirstAsync(x => x.Name == MagicObjectHelper.預設角色);
        }
        else
        {
            role = new RoleView { Name = Unique("角色"), TabViewJson = JsonSerializer.Serialize(permissions) };
            context.RoleView.Add(role);
            await context.SaveChangesAsync();
            await writer.SyncRolePermissionsAsync(role.Id, permissions);
        }

        var account = Unique("m7-");
        var user = new MyUser
        {
            Account = account,
            Name = account,
            Email = email,
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

    public Task<BackendDBContext> CreateContextAsync()
        => factory.Services.GetRequiredService<IDbContextFactory<BackendDBContext>>().CreateDbContextAsync();
}

/// <summary>多一個「等閘門打開才結束」的排程作業，讓「執行中」與「執行中再按一次」可以穩定測到。</summary>
public sealed class ApiTestApplicationFactoryWithGatedJob : ApiTestApplicationFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.AddScheduledJob<GatedJob>(GatedJob.Name, GatedJob.DisplayName, "測試用：等閘門打開才結束。", "0 4 * * *"));
    }
}

public sealed class GatedJob : IScheduledJob
{
    public const string Name = "ApiGateTest";
    public const string DisplayName = "閘門測試作業";
    private static TaskCompletionSource gate = NewGate();

    public static void Reset() => gate = NewGate();

    public static void Release() => gate.TrySetResult();

    public async Task<ScheduledJobResult> ExecuteAsync(ScheduledJobContext context, CancellationToken cancellationToken)
    {
        await gate.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        return ScheduledJobResult.Success("閘門已開");
    }

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
