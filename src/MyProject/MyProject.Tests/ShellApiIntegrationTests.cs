using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Shell;
using MyProject.Share.Helpers;
using MyProject.Web.Components.Layout;
using MyProject.Web.Controllers;

namespace MyProject.Tests;

/// <summary>
/// WASM 外框與個人資料頁用的 API（0.10.1 起，WASM 遷移 M2）：系統資訊、選單、操作說明、開頁稽核、錯誤回報、
/// 自己的通知與公告、個人資料。用真的主機與資料庫，以 API 登入取得的存取 Token 呼叫。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class ShellApiIntegrationTests : IClassFixture<ApiTestApplicationFactoryWithBrowsingAudit>
{
    private readonly ApiTestApplicationFactoryWithBrowsingAudit factory;

    public ShellApiIntegrationTests(ApiTestApplicationFactoryWithBrowsingAudit factory)
    {
        this.factory = factory;
    }

    [Theory]
    [InlineData("/api/v1/shell/info")]
    [InlineData("/api/v1/shell/menu")]
    [InlineData("/api/v1/shell/help?route=/Profile")]
    [InlineData("/api/v1/notifications/summary")]
    [InlineData("/api/v1/announcements/active")]
    [InlineData("/api/v1/profile")]
    public async Task Endpoints_WithoutAToken_ShouldReturnApiResult401(string url)
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<ApiResult<object>>())!.Success);
    }

    [Theory]
    [InlineData("POST", "/api/v1/shell/page-view")]
    [InlineData("POST", "/api/v1/shell/client-errors")]
    [InlineData("POST", "/api/v1/notifications/1/read")]
    [InlineData("POST", "/api/v1/notifications/read-all")]
    [InlineData("DELETE", "/api/v1/notifications/1")]
    [InlineData("DELETE", "/api/v1/notifications/read")]
    [InlineData("POST", "/api/v1/announcements/1/dismiss")]
    [InlineData("PUT", "/api/v1/profile/name")]
    public async Task WriteEndpoints_WithoutAToken_ShouldReturnApiResult401(string method, string url)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), url) { Content = JsonContent.Create(new { }) };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<ApiResult<object>>())!.Success);
    }

    [Fact]
    public async Task Info_ShouldIncludeTheVersionAndTheHelpTopics()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));

        var info = await GetAsync<ShellInfoDto>(client, "/api/v1/shell/info");

        Assert.False(string.IsNullOrWhiteSpace(info.SystemName));
        Assert.False(string.IsNullOrWhiteSpace(info.SystemVersion));
        Assert.Contains(info.HelpTopics, x => x.Route == "/Profile" && x.Title == "個人資料");
    }

    /// <summary>0.10.2 起：WASM 啟動前讀伺服器（zh-TW）的日期時間格式，不需登入。</summary>
    [Fact]
    public async Task Culture_ShouldReturnTheServerDateTimeFormat_WithoutSigningIn()
    {
        using var client = factory.CreateClient();

        var format = await GetAsync<DateTimeFormatDto>(client, "/api/v1/shell/culture");

        var server = new System.Globalization.CultureInfo("zh-TW").DateTimeFormat;
        Assert.Equal(server.ShortDatePattern, format.ShortDatePattern);
        Assert.Equal(server.LongTimePattern, format.LongTimePattern);
        Assert.Equal(server.AMDesignator, format.AMDesignator);
        Assert.Equal(server.PMDesignator, format.PMDesignator);
    }

    [Fact]
    public async Task Menu_ShouldBeFilteredByPermissions_LikeTheServerSidebar()
    {
        using var admin = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        using var plain = await SignedInClientAsync(await AddUserAsync(isAdmin: false));

        var adminMenu = await GetAsync<ShellMenuDto>(admin, "/api/v1/shell/menu");
        var plainMenu = await GetAsync<ShellMenuDto>(plain, "/api/v1/shell/menu");

        Assert.Contains(Flatten(adminMenu.Items), x => x.Url.Trim('/') == "myusers");
        Assert.DoesNotContain(Flatten(plainMenu.Items), x => x.Url.Trim('/') == "myusers");
        Assert.True(Flatten(adminMenu.Items).Count() > Flatten(plainMenu.Items).Count());
        Assert.Contains("myusers", adminMenu.AllMenuUrls, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(SidebarMenuService.MenuPermissionMap.Keys, id => Flatten(adminMenu.Items).Any(x => x.Id == id));
    }

    [Fact]
    public async Task Help_ShouldReturnRenderedSections_AndRejectUnknownRoutes()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));

        var help = await GetAsync<HelpDocumentDto>(client, "/api/v1/shell/help?route=/Profile");
        Assert.Equal("個人資料", help.Title);
        Assert.Equal(7, help.Sections.Count);
        Assert.All(help.Sections, x => Assert.DoesNotContain("<script", x.Html, StringComparison.OrdinalIgnoreCase));

        var missing = await client.GetAsync("/api/v1/shell/help?route=/no-such-page");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task PageView_ShouldWriteThePageViewAudit_WithoutTheQueryString()
    {
        var user = await AddUserAsync(isAdmin: false);
        using var client = await SignedInClientAsync(user);

        var response = await client.PostAsJsonAsync("/api/v1/shell/page-view", new PageViewRequestDto { Path = "/Profile?tab=1", From = "/App" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var context = await CreateContextAsync();
        Assert.True(await context.AuditLog.AnyAsync(x =>
            x.Action == AuditActions.Page.View && x.ActorUserId == user.Id && x.TargetId == "/Profile" && x.Detail == "from=/App"));
    }

    [Theory]
    [InlineData("/Auths/Google/Login")]
    [InlineData("/MyUser")]
    [InlineData("/no-such-page")]
    public async Task PageView_ShouldIgnorePagesThatAreNotWasmPages(string path)
    {
        // API 只收 WASM 頁面的路徑（0.10.9 起也只有 WASM 頁面），不讓人用 API 偽造任意路徑。
        var user = await AddUserAsync(isAdmin: false);
        using var client = await SignedInClientAsync(user);

        var response = await client.PostAsJsonAsync("/api/v1/shell/page-view", new PageViewRequestDto { Path = path, From = "/Profile" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var context = await CreateContextAsync();
        Assert.False(await context.AuditLog.AnyAsync(x => x.Action == AuditActions.Page.View && x.ActorUserId == user.Id));
    }

    [Fact]
    public async Task PageView_ShouldStopRecording_AfterThePerMinuteLimit()
    {
        var user = await AddUserAsync(isAdmin: false);
        using var client = await SignedInClientAsync(user);

        for (var i = 0; i < ShellController.PageViewsPerMinute + 5; i++)
        {
            await client.PostAsJsonAsync("/api/v1/shell/page-view", new PageViewRequestDto { Path = "/Profile", From = "/App" });
        }

        await using var context = await CreateContextAsync();
        Assert.Equal(ShellController.PageViewsPerMinute, await context.AuditLog.CountAsync(x => x.Action == AuditActions.Page.View && x.ActorUserId == user.Id));
    }

    [Fact]
    public async Task ClientErrors_ShouldBeAccepted()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: false));

        var response = await client.PostAsJsonAsync("/api/v1/shell/client-errors", new ClientErrorReportDto { Kind = "error", Message = "boom", Path = "/Profile" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ClientErrors_ShouldBeLimitedPerUser_AcrossRequests()
    {
        // 每個 API 請求是不同的 scope，上限必須跨請求以使用者計算（預設每分鐘 10 筆）。
        var me = await AddUserAsync(isAdmin: false);
        using var client = await SignedInClientAsync(me);
        var limit = factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<MyProject.Web.Configuration.ClientErrorReportingSettings>>().CurrentValue.MaxPerCircuitPerMinute;

        var accepted = new List<bool>();
        for (var i = 0; i < limit + 3; i++)
        {
            var response = await client.PostAsJsonAsync("/api/v1/shell/client-errors", new ClientErrorReportDto { Kind = "error", Message = $"boom {i}", Path = "/Profile" });
            accepted.Add((await response.Content.ReadFromJsonAsync<ApiResult<AcceptedDto>>())!.Data!.Accepted);
        }

        Assert.True(accepted.Take(limit).All(x => x));
        Assert.True(accepted.Skip(limit).All(x => !x));

        // 另一位使用者不受影響。
        using var other = await SignedInClientAsync(await AddUserAsync(isAdmin: false));
        var otherResponse = await other.PostAsJsonAsync("/api/v1/shell/client-errors", new ClientErrorReportDto { Kind = "error", Message = "boom", Path = "/Profile" });
        Assert.True((await otherResponse.Content.ReadFromJsonAsync<ApiResult<AcceptedDto>>())!.Data!.Accepted);
    }

    [Fact]
    public async Task Notifications_ShouldOnlyExposeMyOwn_AndDropUnsafeLinks()
    {
        var me = await AddUserAsync(isAdmin: false);
        var other = await AddUserAsync(isAdmin: false);
        int mine, unsafeLink;
        await using (var context = await CreateContextAsync())
        {
            var a = new Notification { RecipientUserId = me.Id, Category = "系統", Title = "給我的", Link = "/Profile", CreatedAtUtc = DateTime.UtcNow };
            var b = new Notification { RecipientUserId = me.Id, Category = "系統", Title = "外部連結", Link = "https://evil.example/", CreatedAtUtc = DateTime.UtcNow };
            context.Notification.AddRange(a, b, new Notification { RecipientUserId = other.Id, Category = "系統", Title = "別人的", CreatedAtUtc = DateTime.UtcNow });
            await context.SaveChangesAsync();
            mine = a.Id;
            unsafeLink = b.Id;
        }

        using var client = await SignedInClientAsync(me);
        var summary = await GetAsync<NotificationSummaryDto>(client, "/api/v1/notifications/summary");
        Assert.Equal(2, summary.UnreadCount);
        Assert.DoesNotContain(summary.Latest, x => x.Title == "別人的");
        Assert.Equal("/Profile", summary.Latest.Single(x => x.Id == mine).Link);
        Assert.Null(summary.Latest.Single(x => x.Id == unsafeLink).Link);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/notifications/{mine}/read", null)).StatusCode);
        Assert.Equal(1, (await GetAsync<NotificationSummaryDto>(client, "/api/v1/notifications/summary")).UnreadCount);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/api/v1/notifications/read")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/notifications/read-all", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/v1/notifications/{unsafeLink}")).StatusCode);
        Assert.Empty((await GetAsync<NotificationSummaryDto>(client, "/api/v1/notifications/summary")).Latest);
    }

    /// <summary>
    /// 0.9.136 的修正（0.10.9 起由 API 守門，Server 版鈴鐺的元件測試已隨 Server 外框刪除）：
    /// 最新 10 則全部未讀、但更舊的有已讀時，「清除已讀」仍要可以按 —— <c>HasRead</c> 看的是全部通知，不是只看 <c>Latest</c>。
    /// </summary>
    [Fact]
    public async Task NotificationSummary_HasRead_ShouldCountOlderReadNotifications_BeyondTheLatestTen()
    {
        var me = await AddUserAsync(isAdmin: false);
        var now = DateTime.UtcNow;
        await using (var context = await CreateContextAsync())
        {
            context.Notification.Add(new Notification { RecipientUserId = me.Id, Category = "系統", Title = "舊的已讀", CreatedAtUtc = now.AddHours(-1), ReadAtUtc = now.AddMinutes(-30) });
            for (var i = 0; i < 10; i++)
            {
                context.Notification.Add(new Notification { RecipientUserId = me.Id, Category = "系統", Title = $"未讀 {i}", CreatedAtUtc = now.AddMinutes(i) });
            }

            await context.SaveChangesAsync();
        }

        using var client = await SignedInClientAsync(me);
        var summary = await GetAsync<NotificationSummaryDto>(client, "/api/v1/notifications/summary");
        Assert.Equal(10, summary.Latest.Count);
        Assert.DoesNotContain(summary.Latest, x => x.Title == "舊的已讀");
        Assert.True(summary.HasRead);
    }

    [Fact]
    public async Task Notifications_OfAnotherUser_ShouldNotBeReadOrDeleted()
    {
        var me = await AddUserAsync(isAdmin: false);
        var other = await AddUserAsync(isAdmin: false);
        int theirs;
        await using (var context = await CreateContextAsync())
        {
            var n = new Notification { RecipientUserId = other.Id, Category = "系統", Title = "別人的", CreatedAtUtc = DateTime.UtcNow };
            context.Notification.Add(n);
            await context.SaveChangesAsync();
            theirs = n.Id;
        }

        using var client = await SignedInClientAsync(me);
        await client.PostAsync($"/api/v1/notifications/{theirs}/read", null);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/notifications/{theirs}")).StatusCode);

        await using var check = await CreateContextAsync();
        var row = await check.Notification.SingleAsync(x => x.Id == theirs);
        Assert.Null(row.ReadAtUtc);
    }

    [Fact]
    public async Task Announcements_ShouldShowActiveOnes_UntilDismissed()
    {
        var me = await AddUserAsync(isAdmin: false);
        int id;
        await using (var context = await CreateContextAsync())
        {
            var announcement = new Announcement
            {
                Title = $"公告 {Guid.NewGuid():N}",
                Content = "內容",
                StartAtUtc = DateTime.UtcNow.AddMinutes(-5),
                TargetKind = AnnouncementTargetKinds.All,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            };
            context.Announcement.Add(announcement);
            await context.SaveChangesAsync();
            id = announcement.Id;
        }

        factory.Services.GetRequiredService<AnnouncementCache>().Invalidate();
        using var client = await SignedInClientAsync(me);
        Assert.Contains(await GetAsync<List<ActiveAnnouncementDto>>(client, "/api/v1/announcements/active"), x => x.Id == id);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/v1/announcements/{id}/dismiss", null)).StatusCode);
        Assert.DoesNotContain(await GetAsync<List<ActiveAnnouncementDto>>(client, "/api/v1/announcements/active"), x => x.Id == id);
    }

    /// <summary>
    /// 通知鈴鐺與個人資料頁的本地時間不可帶時差（0.10.6 起，M7 發現 M2 留下的問題）：帶 +08:00 送出時，WASM 會換成瀏覽器的時區，
    /// 伺服器與瀏覽器時區不同時時間就錯。比照 M5 起的規則，一律以 Kind=Unspecified 送出。
    /// </summary>
    [Fact]
    public async Task NotificationsProfileAndShellInfo_ShouldSendLocalTimesWithoutAnOffset()
    {
        var me = await AddUserAsync(isAdmin: false);
        await using (var context = await CreateContextAsync())
        {
            context.Notification.Add(new Notification { RecipientUserId = me.Id, Category = "系統", Title = "時差", CreatedAtUtc = DateTime.UtcNow });
            await context.SaveChangesAsync();
        }

        using var client = await SignedInClientAsync(me);
        var texts = new[]
        {
            await (await client.GetAsync("/api/v1/notifications/summary")).Content.ReadAsStringAsync(),
            await (await client.GetAsync("/api/v1/profile")).Content.ReadAsStringAsync(),
            // 「關於」的啟動時間（M7 獨立審查補）。
            await (await client.GetAsync("/api/v1/shell/info")).Content.ReadAsStringAsync(),
        };

        var offset = new System.Text.RegularExpressions.Regex(@"T\d{2}:\d{2}:\d{2}(\.\d+)?[+-]\d{2}:\d{2}");
        var envelope = new System.Text.RegularExpressions.Regex("\"timestamp\":\"[^\"]*\"");
        Assert.All(texts, text => Assert.False(offset.IsMatch(envelope.Replace(text, string.Empty)), text));
    }

    [Fact]
    public async Task Profile_ShouldShowMyDataAndLogins_AndUpdateTheNameWithTheConcurrencyStamp()
    {
        var me = await AddUserAsync(isAdmin: false);
        using var client = await SignedInClientAsync(me);

        var profile = await GetAsync<ProfileDto>(client, "/api/v1/profile");
        Assert.Equal(me.Account, profile.Account);
        Assert.True(profile.HasLocalPassword);
        Assert.Contains(profile.RecentLogins, x => x.Action == "登入" && x.Success);

        var stale = await client.PutAsJsonAsync("/api/v1/profile/name", new UpdateProfileNameDto { Name = "新名字", ConcurrencyStamp = "stale" });
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);

        var updated = await client.PutAsJsonAsync("/api/v1/profile/name", new UpdateProfileNameDto { Name = "新名字", ConcurrencyStamp = profile.ConcurrencyStamp });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("新名字", (await GetAsync<ProfileDto>(client, "/api/v1/profile")).Name);
    }

    private sealed class AcceptedDto
    {
        public bool Accepted { get; set; }
    }

    private static IEnumerable<MenuItemDto> Flatten(IEnumerable<MenuItemDto> items)
        => items.SelectMany(x => new[] { x }.Concat(Flatten(x.SubMenu)));

    private static async Task<T> GetAsync<T>(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApiResult<T>>())!.Data!;
    }

    private async Task<HttpClient> SignedInClientAsync(MyUser user)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/Auth/login", new LoginRequestDto { Account = user.Account, Password = "Passw0rd-1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var tokens = (await login.Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())!.Data!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private async Task<MyUser> AddUserAsync(bool isAdmin)
    {
        await using var context = await CreateContextAsync();
        var role = await context.RoleView.FirstAsync(x => x.Name == MagicObjectHelper.預設角色);
        var account = $"shell-{Guid.NewGuid():N}";
        var user = new MyUser
        {
            Account = account,
            Name = "Shell " + account[^6..],
            Password = SecurePasswordHasher.HashPassword("Passw0rd-1"),
            Status = true,
            IsAdmin = isAdmin,
            RoleViewId = role.Id,
            PasswordChangedAtUtc = DateTime.UtcNow,
        };
        context.MyUser.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    private Task<BackendDBContext> CreateContextAsync()
        => factory.Services.GetRequiredService<IDbContextFactory<BackendDBContext>>().CreateDbContextAsync();
}

/// <summary>開啟瀏覽類稽核（換頁、搜尋、查看；預設關閉）的測試主機，用來驗證 WASM 頁面的開頁稽核。</summary>
public sealed class ApiTestApplicationFactoryWithBrowsingAudit : ApiTestApplicationFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // 註冊順序在 base 之後，因此覆寫得掉 base 加進去的設定。
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AuditLogSettings:RecordBrowsing"] = "true",
            });
        });
    }
}
