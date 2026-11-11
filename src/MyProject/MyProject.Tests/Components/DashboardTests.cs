using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Shell;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Client.Layout;
using MyProject.Web.Client.Views.Dashboard;
using MyProject.Web.Dashboard;
using MyProject.Web.Scheduling;

namespace MyProject.Tests.Components;

/// <summary>
/// 首頁儀表板（0.9.106 起；0.10.6 起在 WASM 執行）：登記與權限過濾（伺服器）、前端代號對應元件、一個小工具讀不到不影響其他、
/// 小工具的內容、上一次登入與「需要注意的排程作業」的判斷。API 本身見 <c>DashboardApiTests</c>。
/// </summary>
public sealed class DashboardTests : ComponentTestBase
{
    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly int userId;
    private readonly FakeDashboardHandler handler = new();

    public DashboardTests()
    {
        connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        connection.Open();
        factory = new TestDbContextFactory(connection);
        using (var context = factory.CreateDbContext())
        {
            context.Database.EnsureCreated();
            var user = new MyUser { Account = "alice", Name = "alice", Password = SecurePasswordHasher.HashPassword("Passw0rd-1"), Status = true };
            context.MyUser.Add(user);
            context.SaveChanges();
            userId = user.Id;
        }

        Services.AddLogging();
        Services.AddSingleton<IDbContextFactory<BackendDBContext>>(factory);
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton(new CurrentUserService { CurrentUser = { Id = userId, Account = "alice", IsAuthenticated = true } });
        Services.AddSingleton<IAuditLogService>(new RecordingAuditLogService());
        Services.AddSingleton(PasswordTestDefaults.Policy());
        var resolverContext = factory.CreateDbContext();
        Services.AddSingleton<IEffectiveTeamResolver>(new EffectiveTeamResolver(resolverContext, new ContextTeamTreeCache(resolverContext), NullLogger<EffectiveTeamResolver>.Instance));
        Services.AddSingleton<ProfileService>();

        // WASM 小工具：API 換成可控的假回應。
        Services.AddSingleton<ClientNotificationSignal>();
        Services.AddSingleton(sp =>
        {
            var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
            var navigation = sp.GetRequiredService<NavigationManager>();
            var session = new ClientSession(http, new TokenStore(sp.GetRequiredService<IJSRuntime>()), navigation, NullLogger<ClientSession>.Instance);
            return new ApiClient(http, session, navigation, NullLogger<ApiClient>.Instance);
        });
        Services.AddSingleton(sp => new DashboardApi(sp.GetRequiredService<ApiClient>()));
        Services.AddSingleton<ClientErrorReporter>();
    }

    // ---------- 登記與過濾（伺服器） ----------

    [Fact]
    public void Catalog_ShouldFilterByAdminAndPermission_AndKeepTheOrder()
    {
        var catalog = new DashboardWidgetCatalog(
        [
            new("projects", "專案", 30, MagicObjectHelper.角色_專案項目, false, "work"),
            new("admin", "管理", 20, null, true, "settings"),
            new("everyone", "大家", 10, null, false, "home"),
        ]);

        Assert.Equal(["everyone", "admin", "projects"], catalog.VisibleTo(true, _ => false).Select(x => x.Id));
        Assert.Equal(["everyone"], catalog.VisibleTo(false, _ => false).Select(x => x.Id));
        Assert.Equal(["everyone", "projects"], catalog.VisibleTo(false, key => key == MagicObjectHelper.角色_專案項目).Select(x => x.Id));
        Assert.Throws<InvalidOperationException>(() => new DashboardWidgetCatalog(
            [new("a", "A", 1, null, false, "x"), new("A", "A2", 2, null, false, "x")]));
    }

    /// <summary>伺服器登記的每個小工具，前端都要有對應的元件（反之亦然），排程作業只給管理員。</summary>
    [Fact]
    public void BuiltInWidgets_ShouldBeRegisteredOnBothSides_WithTheScheduledJobsOneAdminOnly()
    {
        var catalog = new ServiceCollection().AddDashboard().BuildServiceProvider().GetRequiredService<DashboardWidgetCatalog>();

        Assert.Equal(["notifications", "account", "scheduled-jobs"], catalog.All.Select(x => x.Id));
        Assert.Equal(catalog.All.Select(x => x.Id).Order(), DashboardWidgetComponents.All.Keys.Order());
        Assert.Equal(
            [typeof(MyNotificationsWidget), typeof(MyAccountWidget), typeof(ScheduledJobsWidget)],
            catalog.All.Select(x => DashboardWidgetComponents.Resolve(x.Id)));
        Assert.All(DashboardWidgetComponents.All.Values, type => Assert.True(typeof(DashboardWidgetBase).IsAssignableFrom(type)));
        Assert.Equal(["notifications", "account"], catalog.VisibleTo(false, _ => true).Select(x => x.Id));
    }

    // ---------- WASM 小工具區 ----------

    /// <summary>⭐ 只建立收到的小工具；讀不到資料的那一張顯示錯誤與追蹤碼，其他照常。</summary>
    [Fact]
    public void Grid_ShouldOnlyRenderTheGivenWidgets_AndIsolateOneThatFailsToLoad()
    {
        handler.Respond("api/v1/dashboard/notifications", HttpStatusCode.InternalServerError, new ApiResult<object> { Success = false, StatusCode = 500, Message = "壞了", TraceId = "trace-123" });
        handler.Respond("api/v1/dashboard/account", HttpStatusCode.OK, ApiResult<DashboardAccountDto>.SuccessResult(new DashboardAccountDto { HasLocalPassword = true }));

        var grid = Render<DashboardGrid>(p => p.Add(x => x.Widgets,
        [
            new DashboardWidgetDto { Id = "notifications", Title = "我的通知", Icon = "notifications" },
            new DashboardWidgetDto { Id = "account", Title = "我的帳號", Icon = "account_circle" },
        ]));

        grid.WaitForAssertion(() => Assert.Contains("這個區塊暫時無法顯示，其他區塊不受影響（錯誤追蹤碼：trace-123）。", grid.Find("[data-widget='notifications']").TextContent, StringComparison.Ordinal));
        grid.WaitForAssertion(() => Assert.Contains("不會到期", grid.Find("[data-widget='account']").TextContent, StringComparison.Ordinal));
        Assert.Equal(2, grid.FindAll(".dashboard-card").Count);
        Assert.Empty(grid.FindAll("[data-widget='scheduled-jobs']"));
        Assert.DoesNotContain(handler.Paths, x => x.Contains("scheduled-jobs", StringComparison.Ordinal));
    }

    [Fact]
    public void Notifications_ShouldShowTheUnreadCountAndItems_AndRefreshWhenAsked()
    {
        handler.Respond("api/v1/dashboard/notifications", HttpStatusCode.OK, ApiResult<DashboardNotificationsDto>.SuccessResult(Notifications(2)));

        var widget = Render<MyNotificationsWidget>();
        widget.WaitForAssertion(() => Assert.Equal("2", widget.Find(".dashboard-stat-number").TextContent));
        Assert.Equal(2, widget.FindAll(".dashboard-list-item").Count);
        Assert.Equal(2, widget.FindAll(".dashboard-unread").Count);
        Assert.Contains("11-10 09:05", widget.Markup, StringComparison.Ordinal);

        handler.Respond("api/v1/dashboard/notifications", HttpStatusCode.OK, ApiResult<DashboardNotificationsDto>.SuccessResult(Notifications(3)));
        Services.GetRequiredService<ClientNotificationSignal>().RequestRefresh();
        widget.WaitForAssertion(() => Assert.Equal("3", widget.Find(".dashboard-stat-number").TextContent));
    }

    [Fact]
    public void Account_ShouldShowWhatTheServerComputed()
    {
        handler.Respond("api/v1/dashboard/account", HttpStatusCode.OK, ApiResult<DashboardAccountDto>.SuccessResult(new DashboardAccountDto
        {
            PreviousLoginLocal = new DateTime(2026, 11, 9, 8, 30, 0),
            PreviousLoginMethod = "Google 登入",
            PreviousLoginClientIp = "198.51.100.7",
            HasLocalPassword = true,
            PasswordExpiresAtLocal = new DateTime(2026, 11, 12, 10, 0, 0),
            PasswordExpiresSoon = true,
        }));

        var widget = Render<MyAccountWidget>();

        widget.WaitForAssertion(() => Assert.Contains("Google 登入", widget.Markup, StringComparison.Ordinal));
        Assert.Contains("2026-11-09 08:30", widget.Markup, StringComparison.Ordinal);
        Assert.Contains("198.51.100.7", widget.Markup, StringComparison.Ordinal);
        Assert.Contains("2026-11-12 10:00", widget.Markup, StringComparison.Ordinal);
        Assert.Contains("快到期", widget.Markup, StringComparison.Ordinal);
        Assert.Contains("未啟用", widget.Markup, StringComparison.Ordinal);
    }

    // ---------- 伺服器的判斷 ----------

    [Fact]
    public async Task PreviousLogin_ShouldSkipTheLatestSuccess_AndIgnoreOthers()
    {
        var service = Services.GetRequiredService<ProfileService>();
        Assert.Null(await service.GetPreviousLoginAsync(userId));

        AddAudit(AuditActions.Login.Success, DateTime.UtcNow.AddDays(-2));
        Assert.Null(await service.GetPreviousLoginAsync(userId));

        AddAudit(AuditActions.Login.Success, DateTime.UtcNow.AddMinutes(-1));
        AddAudit(AuditActions.Login.Failed, DateTime.UtcNow, success: false);
        AddAudit(AuditActions.Login.Success, DateTime.UtcNow, actor: userId + 100);
        var previous = await service.GetPreviousLoginAsync(userId);
        Assert.NotNull(previous);
        Assert.True(previous!.OccurredAtUtc < DateTime.UtcNow.AddDays(-1));
    }

    [Fact]
    public void ScheduledJobAttention_ShouldListFailedInterruptedOverdueAndDisabledJobs()
    {
        static ScheduledJobOverviewItem Item(string name, bool enabled = true, string? lastStatus = null, bool overdue = false)
            => new(name, name, string.Empty, "0 3 * * *", enabled, null, null,
                lastStatus is null ? null : new JobRunAdapterModel { JobName = name, Status = lastStatus },
                false, overdue ? DateTime.UtcNow.AddHours(-2) : null);

        var overview = new ScheduledJobOverview(true, null,
        [
            Item("正常", lastStatus: JobRunStatuses.Succeeded),
            Item("失敗", lastStatus: JobRunStatuses.Failed),
            Item("中斷", lastStatus: JobRunStatuses.Interrupted),
            Item("逾期", overdue: true),
            Item("停用", enabled: false, lastStatus: JobRunStatuses.Failed),
        ]);

        Assert.Equal(
            [("失敗", ScheduledJobAttention.FailedReason), ("中斷", ScheduledJobAttention.InterruptedReason), ("逾期", ScheduledJobAttention.OverdueReason), ("停用", ScheduledJobAttention.DisabledReason)],
            ScheduledJobAttention.From(overview).Select(x => (x.DisplayName, x.Reason)));
    }

    // ---------- helpers ----------

    private static DashboardNotificationsDto Notifications(int count) => new()
    {
        UnreadCount = count,
        Items = Enumerable.Range(1, count).Select(i => new DashboardNotificationItemDto
        {
            Id = i,
            Title = $"通知{i}",
            Link = "/projects",
            CreatedAtLocal = new DateTime(2026, 11, 10, 9, 4 + i, 0),
        }).ToList(),
    };

    private void AddAudit(string action, DateTime occurredAtUtc, bool success = true, int? actor = null, string? clientIp = null)
    {
        using var context = factory.CreateDbContext();
        context.AuditLog.Add(new AuditLog { Action = action, OccurredAt = occurredAtUtc, Success = success, ActorUserId = actor ?? userId, ActorAccount = "alice", ClientIp = clientIp });
        context.SaveChanges();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            connection.Dispose();
        }
    }

    /// <summary>依路徑回固定的 JSON；記下呼叫過的路徑。</summary>
    private sealed class FakeDashboardHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, object Body)> responses = new(StringComparer.Ordinal);

        public List<string> Paths { get; } = [];

        public void Respond(string path, HttpStatusCode status, object body)
        {
            lock (responses)
            {
                responses[path] = (status, body);
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            (HttpStatusCode Status, object Body) response;
            lock (responses)
            {
                Paths.Add(path);
                response = responses.TryGetValue(path, out var found) ? found : (HttpStatusCode.NotFound, ApiResult<object>.NotFoundResult());
            }

            return Task.FromResult(new HttpResponseMessage(response.Status)
            {
                Content = JsonContent.Create(response.Body, response.Body.GetType(), options: JsonSerializerOptions.Web),
            });
        }
    }
}
