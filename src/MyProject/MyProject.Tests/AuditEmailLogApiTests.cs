using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Share.Helpers;

namespace MyProject.Tests;

/// <summary>
/// 稽核紀錄與寄信紀錄頁的 Web API（0.10.4 起，WASM 遷移 M5）：只限管理員（<c>[AdminOnly]</c>），
/// 查詢、明細、匯出、清除、清空、完整性驗證，稽核寫法與 Server 版頁面相同；寄信紀錄唯讀、不寫稽核。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class AuditEmailLogApiTests : IClassFixture<ApiTestApplicationFactoryWithBrowsingAudit>
{
    private const string Password = "Passw0rd-1";
    private readonly ApiTestApplicationFactoryWithBrowsingAudit factory;

    public AuditEmailLogApiTests(ApiTestApplicationFactoryWithBrowsingAudit factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Search_ShouldFilterLikeTheScreen_AndAuditOnlyWhenTheUserSearches()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var tag = Unique("稽核標記");
        await SeedAuditAsync(tag, "User.Update", success: true, minutesAgo: 3);
        await SeedAuditAsync(tag, "Auth.Login", success: false, minutesAgo: 2);

        var all = await SendAsync<PagedResult<AuditLogItemDto>>(client, HttpMethod.Post, "/api/v1/audit-logs/search", new AuditLogSearchRequestDto { Keyword = tag, PageSize = 8 });
        var failed = await SendAsync<PagedResult<AuditLogItemDto>>(client, HttpMethod.Post, "/api/v1/audit-logs/search", new AuditLogSearchRequestDto { Keyword = tag, Success = false, RecordSearch = true });

        Assert.Equal(["Auth.Login", "User.Update"], all.Items.Select(x => x.Action));
        Assert.Equal("Auth.Login", Assert.Single(failed.Items).Action);
        var first = all.Items[0];
        Assert.Equal(first.OccurredAt.ToUniversalTime(), first.OccurredAtUtc);
        Assert.Equal("升級前的紀錄，未納入完整性驗證", first.Signature);
        Assert.Equal(1, await CountAuditAsync(AuditActions.Audit.Search, admin.Id));
    }

    /// <summary>
    /// 審查修正（高）：本地時間要以「不帶時差」送出。帶了 +08:00 的話，WASM 會換成瀏覽器的時區，
    /// 伺服器與瀏覽器時區不同時整頁時間都錯（清單、明細、異動歷程、寄信紀錄）。UTC 欄位以 Z 結尾。
    /// </summary>
    [Fact]
    public async Task LocalTimes_ShouldBeSentWithoutAnOffset()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var tag = Unique("時差");
        var id = await SeedAuditAsync(tag, "User.Update", success: true, minutesAgo: 1, targetType: "Project");
        await using (var context = await CreateContextAsync())
        {
            context.EmailOutbox.Add(new EmailOutbox { CreatedAtUtc = DateTime.UtcNow, Kind = "Test", To = "t@e2e.test", Subject = tag, Status = EmailOutboxStatus.Sent, Attempts = 1, SentAtUtc = DateTime.UtcNow });
            await context.SaveChangesAsync();
        }

        var offset = new System.Text.RegularExpressions.Regex(@"T\d{2}:\d{2}:\d{2}(\.\d+)?[+-]\d{2}:\d{2}");
        var texts = new[]
        {
            await (await client.PostAsJsonAsync("/api/v1/audit-logs/search", new AuditLogSearchRequestDto { Keyword = tag })).Content.ReadAsStringAsync(),
            await (await client.GetAsync($"/api/v1/audit-logs/{id}")).Content.ReadAsStringAsync(),
            await (await client.GetAsync("/api/v1/audit-logs/history?targetType=Project&targetId=1")).Content.ReadAsStringAsync(),
            await (await client.PostAsJsonAsync("/api/v1/email-logs/search", new EmailLogSearchRequestDto { Keyword = tag })).Content.ReadAsStringAsync(),
        };

        // ApiResult 外層的 timestamp 是回應時間、不顯示在畫面上，不在檢查範圍。
        var envelope = new System.Text.RegularExpressions.Regex("\"timestamp\":\"[^\"]*\"");
        Assert.All(texts, text => Assert.False(offset.IsMatch(envelope.Replace(text, string.Empty)), text));
        Assert.Contains("Z\"", texts[0]);
    }

    [Fact]
    public async Task Search_WithAnInvalidPageSize_ShouldReturn400()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/audit-logs/search", new AuditLogSearchRequestDto { PageSize = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/email-logs/search", new EmailLogSearchRequestDto { PageSize = -1 })).StatusCode);
    }

    [Fact]
    public async Task Options_ShouldListActionsTargetTypesAndRetentionDays()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        await SeedAuditAsync(Unique("選項"), "Role.Create", success: true, minutesAgo: 1, targetType: "RoleView");

        var options = await SendAsync<AuditLogOptionsDto>(client, HttpMethod.Get, "/api/v1/audit-logs/options");

        Assert.Contains("Role.Create", options.Actions);
        Assert.Contains("RoleView", options.TargetTypes);
        Assert.True(options.PurgeDays > 0);
        Assert.True(options.BrowsingPurgeDays > 0);
    }

    [Fact]
    public async Task Detail_ShouldReturnTheRowAndItsContent_AndWriteOneViewAudit()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var tag = Unique("明細");
        var id = await SeedAuditAsync(tag, "Role.Create", success: true, minutesAgo: 1);

        var detail = await SendAsync<AuditLogDetailDto>(client, HttpMethod.Get, $"/api/v1/audit-logs/{id}");
        var listed = (await SendAsync<PagedResult<AuditLogItemDto>>(client, HttpMethod.Post, "/api/v1/audit-logs/search", new AuditLogSearchRequestDto { Keyword = tag })).Items.Single();

        Assert.Equal(id, detail.Id);
        // 明細與清單的時間一致（實機發現：單筆查詢回的是 UTC，曾被當成本地再換算一次，差了 8 小時）。
        Assert.Equal(listed.OccurredAt, detail.OccurredAt);
        Assert.Equal(listed.OccurredAtUtc, detail.OccurredAtUtc);
        Assert.Contains(tag, detail.Detail);
        Assert.Null(detail.Content);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/audit-logs/999999999")).StatusCode);
        Assert.Equal(1, await CountAuditAsync(AuditActions.Audit.View, admin.Id));
    }

    [Fact]
    public async Task Export_ShouldReturnACsvWithBom_AndWriteAnExportAudit()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var tag = Unique("匯出");
        await SeedAuditAsync(tag, "User.Update", success: true, minutesAgo: 1);

        var response = await client.PostAsJsonAsync("/api/v1/audit-logs/export", new AuditLogSearchRequestDto { Keyword = tag });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("發生時間（本地）", lines[0]);
        Assert.Equal(1, await CountAuditAsync(AuditActions.Audit.Export, admin.Id));
    }

    [Fact]
    public async Task PurgeClearAndVerify_ShouldWorkLikeTheScreen_AndAuditThemselves()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var tag = Unique("清除");
        await SeedAuditAsync(tag, "User.Update", success: true, minutesAgo: 60 * 24 * 400);

        var purge = await SendAsync<AuditLogPurgeResultDto>(client, HttpMethod.Post, "/api/v1/audit-logs/purge");
        Assert.True(purge.Succeeded);
        Assert.True(purge.Changed);
        Assert.StartsWith("異動與安全紀錄：", purge.Message);
        Assert.Equal(0, await CountByTagAsync(tag));
        Assert.Equal(2, await CountAuditAsync(AuditActions.Audit.Purge, admin.Id));

        var report = await SendAsync<AuditIntegrityReportDto>(client, HttpMethod.Post, "/api/v1/audit-logs/verify");
        Assert.False(string.IsNullOrWhiteSpace(report.Summary));
        Assert.Equal(1, await CountAuditAsync(AuditActions.Audit.Verify, admin.Id));

        await SeedAuditAsync(Unique("清空"), "User.Update", success: true, minutesAgo: 1);
        await SendAsync<object>(client, HttpMethod.Post, "/api/v1/audit-logs/clear-all");
        await using var context = await CreateContextAsync();
        Assert.Equal([AuditActions.Audit.ClearAll], await context.AuditLog.Select(x => x.Action).ToListAsync());
    }

    [Fact]
    public async Task EmailLogs_ShouldFilterByStatusAndKeyword_NewestFirst_WithoutAuditing()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var tag = Unique("寄信");
        await using (var context = await CreateContextAsync())
        {
            context.EmailOutbox.AddRange(
                new EmailOutbox { CreatedAtUtc = DateTime.UtcNow.AddMinutes(-3), Kind = "Test", To = "a@e2e.test", Subject = $"{tag} 一", Status = EmailOutboxStatus.Sent, Attempts = 1 },
                new EmailOutbox { CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2), Kind = "Test", To = "b@e2e.test", Subject = $"{tag} 二", Status = EmailOutboxStatus.Failed, Attempts = 5, LastError = "550" },
                new EmailOutbox { CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1), Kind = "Notification", To = "c@e2e.test", Subject = $"{tag} 三", Status = EmailOutboxStatus.Sent, Attempts = 1 });
            await context.SaveChangesAsync();
        }

        var sent = await SendAsync<PagedResult<EmailLogItemDto>>(client, HttpMethod.Post, "/api/v1/email-logs/search", new EmailLogSearchRequestDto { Keyword = tag, Status = "Sent", PageSize = 8 });
        var test = await SendAsync<PagedResult<EmailLogItemDto>>(client, HttpMethod.Post, "/api/v1/email-logs/search", new EmailLogSearchRequestDto { Keyword = tag, Kind = "Test" });

        Assert.Equal([$"{tag} 三", $"{tag} 一"], sent.Items.Select(x => x.Subject));
        Assert.Equal(2, sent.TotalCount);
        Assert.Equal(2, test.TotalCount);
        Assert.Equal(0, await CountAnyAuditAsync(admin.Id, "Email"));
    }

    [Fact]
    public async Task NonAdmins_ShouldBeForbiddenFromEveryEndpoint_AndTheDenialAudited()
    {
        var user = await AddUserAsync(isAdmin: false);
        using var client = await SignedInClientAsync(user);

        var calls = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Post, "/api/v1/audit-logs/search", new AuditLogSearchRequestDto()),
            (HttpMethod.Get, "/api/v1/audit-logs/options", null),
            (HttpMethod.Get, "/api/v1/audit-logs/1", null),
            (HttpMethod.Post, "/api/v1/audit-logs/export", new AuditLogSearchRequestDto()),
            (HttpMethod.Post, "/api/v1/audit-logs/purge", null),
            (HttpMethod.Post, "/api/v1/audit-logs/clear-all", null),
            (HttpMethod.Post, "/api/v1/audit-logs/verify", null),
            (HttpMethod.Post, "/api/v1/email-logs/search", new EmailLogSearchRequestDto()),
            (HttpMethod.Get, "/api/v1/audit-logs/history?targetType=Project&targetId=1", null),
        };

        foreach (var (method, url, body) in calls)
        {
            using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body, body.GetType()) };
            var response = await client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {url} → {(int)response.StatusCode}");
        }

        await using var context = await CreateContextAsync();
        Assert.Equal(calls.Length, await context.AuditLog.CountAsync(x => x.Action == AuditActions.Permission.Denied && x.ActorUserId == user.Id));
    }

    // ───────────────────────────── 工具 ─────────────────────────────

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..(prefix.Length + 10)];

    private async Task<int> SeedAuditAsync(string tag, string action, bool success, int minutesAgo, string targetType = "MyUser")
    {
        await using var context = await CreateContextAsync();
        var row = new AuditLog
        {
            OccurredAt = DateTime.UtcNow.AddMinutes(-minutesAgo),
            ActorUserId = 1,
            ActorAccount = "support",
            Action = action,
            TargetType = targetType,
            TargetId = "1",
            Detail = $"{tag} 測試",
            Success = success,
            ClientIp = "10.0.0.8",
        };
        context.AuditLog.Add(row);
        await context.SaveChangesAsync();
        return row.Id;
    }

    private async Task<int> CountByTagAsync(string tag)
    {
        await using var context = await CreateContextAsync();
        return await context.AuditLog.CountAsync(x => x.Detail != null && x.Detail.Contains(tag));
    }

    private async Task<int> CountAuditAsync(string action, int actorId)
    {
        await using var context = await CreateContextAsync();
        return await context.AuditLog.CountAsync(x => x.Action == action && x.ActorUserId == actorId);
    }

    private async Task<int> CountAnyAuditAsync(int actorId, string actionPrefix)
    {
        await using var context = await CreateContextAsync();
        return await context.AuditLog.CountAsync(x => x.ActorUserId == actorId && x.Action.StartsWith(actionPrefix));
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
        return JsonSerializer.Deserialize<ApiResult<T>>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Data!;
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
        var account = Unique("m5-");
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
