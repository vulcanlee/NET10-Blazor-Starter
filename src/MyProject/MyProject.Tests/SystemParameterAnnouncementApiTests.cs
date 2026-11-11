using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Shell;
using MyProject.Share.Helpers;
using MyProject.Web.Configuration;
using MyProject.Web.Configuration.Parameters;

namespace MyProject.Tests;

/// <summary>
/// 系統參數與公告管理的 Web API（0.10.6 起，WASM 遷移 M7）：只限管理員（<c>[AdminOnly]</c>），驗證在伺服器、存檔後立即生效，
/// 稽核寫法與 Server 版頁面相同（每個動作一筆）；本地時間不帶時差送出。公告外框橫幅的 active／dismiss 仍是登入即可。
/// 自己一個主機（class fixture）：改系統參數會影響整個主機，只用不在測試設定裡、也不影響其他測試的鍵。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class SystemParameterAnnouncementApiTests : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "Passw0rd-1";
    private const string ExternalCallKey = "SlowOperationSettings:ExternalCallMs";
    private const string AiCallKey = "SlowOperationSettings:AiCallMs";
    private readonly ApiTestApplicationFactory factory;

    public SystemParameterAnnouncementApiTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    // ───────────────────────────── 系統參數 ─────────────────────────────

    [Fact]
    public async Task Overview_ShouldListTheWholeCatalogWithDefinitions()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));

        var overview = await SendAsync<SystemParameterOverviewDto>(client, HttpMethod.Get, "/api/v1/system-parameters");

        Assert.Equal(SystemParameterCatalog.All.Select(x => x.Key), overview.Items.Select(x => x.Definition.Key));
        var name = overview.Items.Single(x => x.Definition.Key == "SystemSettings:SystemInformation:SystemName");
        Assert.Equal("系統名稱", name.Definition.Label);
        Assert.Equal(SystemParameterCatalog.GroupIdentity, name.Definition.Group);
        Assert.Equal(SystemParameterKind.String, name.Definition.Kind);
        Assert.True(name.Definition.Required);
        Assert.Equal(50, name.Definition.MaxLength);
        var audit = overview.Items.Single(x => x.Definition.Key == "LogRetentionSettings:AuditLogDays");
        Assert.True(audit.Definition.IsRetention);
        Assert.Equal("天", audit.Definition.Unit);
        Assert.Equal("最多 50 個字，不可留空", SystemParameterValueCodec.RangeText(name.Definition));
        Assert.All(overview.Items.Where(x => x.Definition.AllowedValues is not null), x => Assert.NotNull(x.Definition.BlankMeaning));
        Assert.Contains(overview.Items, x => x.Definition.AllowedValues is { Count: > 0 });
        Assert.Empty(overview.BaseInvalid);
    }

    [Fact]
    public async Task Save_ShouldTakeEffectImmediately_ShowWhoChangedIt_AndAuditOnce()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var item = await ItemAsync(client, ExternalCallKey);
        var target = item.EffectiveValue == "12345" ? "12346" : "12345";

        var saved = await SendForResultAsync<object>(client, HttpMethod.Post, "/api/v1/system-parameters/save",
            new SystemParameterSaveRequestDto { Key = ExternalCallKey, Value = target, ExpectedStamp = item.Override?.ConcurrencyStamp });

        Assert.Equal($"已更新「慢寄信門檻」為 {int.Parse(target, CultureInfo.InvariantCulture):N0} 毫秒。", saved.Message);
        Assert.Equal(int.Parse(target, CultureInfo.InvariantCulture),
            factory.Services.GetRequiredService<IOptionsMonitor<SlowOperationSettings>>().CurrentValue.ExternalCallMs);
        var after = await ItemAsync(client, ExternalCallKey);
        Assert.Equal(SystemParameterSource.Override, after.Source);
        Assert.Equal(target, after.EffectiveValue);
        Assert.Equal(admin.Account, after.Override!.UpdatedBy);
        Assert.InRange(after.Override.UpdatedAt, DateTime.Now.AddMinutes(-2), DateTime.Now.AddMinutes(1));
        var audit = await SingleAuditAsync(AuditActions.SystemParameter.Update, admin.Id);
        Assert.Equal("SystemParameter", audit.TargetType);
        Assert.Equal(ExternalCallKey, audit.TargetId);
        Assert.Equal($"key={ExternalCallKey}; old={item.EffectiveValue}({(item.Source == SystemParameterSource.Override ? "系統參數" : "設定檔")}); new={target}(系統參數)", audit.Detail);
    }

    [Theory]
    [InlineData(ExternalCallKey, "abc", "請輸入整數。")]
    [InlineData(ExternalCallKey, "-1", "必須介於 0 到 3,600,000 之間。")]
    [InlineData("SystemSettings:SystemInformation:SystemName", "   ", "不可留空。")]
    [InlineData("SystemSettings:SystemInformation:SystemName", "第一行\n第二行", "不可包含換行或控制字元。")]
    [InlineData("AiReasoningSettings:AgentPlayground", "ultra", null)]
    public async Task Save_InvalidValues_ShouldBeRejectedByTheServer_AndNothingSavedOrAudited(string key, string value, string? message)
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var before = await ItemAsync(client, key);

        var response = await client.PostAsJsonAsync("/api/v1/system-parameters/save",
            new SystemParameterSaveRequestDto { Key = key, Value = value, ExpectedStamp = before.Override?.ConcurrencyStamp });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<ApiResult<object>>())!;
        if (message is not null)
        {
            Assert.Equal(message, body.ErrorMessage);
        }

        Assert.Equal(before.EffectiveValue, (await ItemAsync(client, key)).EffectiveValue);
        Assert.Equal(0, await CountAuditAsync(AuditActions.SystemParameter.Update, admin.Id));
    }

    [Fact]
    public async Task Save_UnknownOrSecretKeys_ShouldBeNotFound_AndAMissingKeyABadRequest()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));

        var secret = await client.PostAsJsonAsync("/api/v1/system-parameters/save", new SystemParameterSaveRequestDto { Key = "JwtSettings:SigningKey", Value = "x" });
        var missing = await client.PostAsJsonAsync("/api/v1/system-parameters/save", new { Value = "1" });
        var resetMissing = await client.PostAsJsonAsync("/api/v1/system-parameters/reset", new SystemParameterResetRequestDto { Key = "Nobody:Here", ExpectedStamp = "stale" });

        Assert.Equal(HttpStatusCode.NotFound, secret.StatusCode);
        Assert.Equal("這個參數不能在這裡修改。", (await secret.Content.ReadFromJsonAsync<ApiResult<object>>())!.ErrorMessage);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, resetMissing.StatusCode);
    }

    [Fact]
    public async Task StaleStamps_ShouldConflict()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var item = await ItemAsync(client, AiCallKey);
        var first = item.EffectiveValue == "70001" ? "70002" : "70001";
        await SendForResultAsync<object>(client, HttpMethod.Post, "/api/v1/system-parameters/save",
            new SystemParameterSaveRequestDto { Key = AiCallKey, Value = first, ExpectedStamp = item.Override?.ConcurrencyStamp });

        // 開窗時還沒有覆寫（stamp 為 null），別人已先新增；或拿舊的 stamp 再存、再還原。
        var created = await client.PostAsJsonAsync("/api/v1/system-parameters/save",
            new SystemParameterSaveRequestDto { Key = AiCallKey, Value = "70003", ExpectedStamp = null });
        var stale = await client.PostAsJsonAsync("/api/v1/system-parameters/save",
            new SystemParameterSaveRequestDto { Key = AiCallKey, Value = "70004", ExpectedStamp = "stale-stamp" });
        var staleReset = await client.PostAsJsonAsync("/api/v1/system-parameters/reset",
            new SystemParameterResetRequestDto { Key = AiCallKey, ExpectedStamp = "stale-stamp" });

        Assert.Equal(HttpStatusCode.Conflict, created.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(ConcurrencyStampHelper.ConflictMessage, (await stale.Content.ReadFromJsonAsync<ApiResult<object>>())!.ErrorMessage);
        Assert.Equal(HttpStatusCode.Conflict, staleReset.StatusCode);
        Assert.Equal(first, (await ItemAsync(client, AiCallKey)).EffectiveValue);
    }

    [Fact]
    public async Task Reset_ShouldReturnToTheConfigurationFileValue_AndAuditOnce()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        const string key = "SlowOperationSettings:HttpRequestMs";
        var item = await ItemAsync(client, key);
        var target = item.BaseValue == "4444" ? "4445" : "4444";
        await SendForResultAsync<object>(client, HttpMethod.Post, "/api/v1/system-parameters/save",
            new SystemParameterSaveRequestDto { Key = key, Value = target, ExpectedStamp = item.Override?.ConcurrencyStamp });
        var overridden = await ItemAsync(client, key);

        var reset = await SendForResultAsync<object>(client, HttpMethod.Post, "/api/v1/system-parameters/reset",
            new SystemParameterResetRequestDto { Key = key, ExpectedStamp = overridden.Override!.ConcurrencyStamp });

        Assert.StartsWith("已將「慢請求門檻」還原為設定檔的值 ", reset.Message);
        var after = await ItemAsync(client, key);
        Assert.Equal(SystemParameterSource.File, after.Source);
        Assert.Null(after.Override);
        Assert.Equal(after.BaseValue, after.EffectiveValue);
        var audit = await SingleAuditAsync(AuditActions.SystemParameter.Reset, admin.Id);
        Assert.Equal(key, audit.TargetId);
        Assert.Equal($"key={key}; old={target}(系統參數); new={after.BaseValue}(設定檔)", audit.Detail);
    }

    [Fact]
    public async Task UnknownKeysInTheDatabase_ShouldBeListedAsOrphans_AndRemovable()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var key = $"Legacy:{Unique("k")}";
        await using (var context = await CreateContextAsync())
        {
            context.SystemParameter.Add(new SystemParameter
            {
                ParameterKey = key,
                Value = "1",
                ConcurrencyStamp = ConcurrencyStampHelper.New(),
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        var orphan = (await SendAsync<SystemParameterOverviewDto>(client, HttpMethod.Get, "/api/v1/system-parameters")).Orphans.Single(x => x.ParameterKey == key);
        var removed = await SendForResultAsync<object>(client, HttpMethod.Post, "/api/v1/system-parameters/reset",
            new SystemParameterResetRequestDto { Key = key, ExpectedStamp = orphan.ConcurrencyStamp });

        Assert.Equal($"已移除「{key}」。", removed.Message);
        Assert.DoesNotContain((await SendAsync<SystemParameterOverviewDto>(client, HttpMethod.Get, "/api/v1/system-parameters")).Orphans, x => x.ParameterKey == key);
        var audit = await SingleAuditAsync(AuditActions.SystemParameter.Reset, admin.Id);
        Assert.Equal(key, audit.TargetId);
        Assert.Equal($"key={key}; old=（無）(系統參數); new=（無）(設定檔)", audit.Detail);
    }

    // ───────────────────────────── 公告 ─────────────────────────────

    [Fact]
    public async Task Announcements_CreateUpdateDelete_ShouldWorkLikeTheScreen_AndAuditEachAction()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var list = await SendAsync<AnnouncementListDto>(client, HttpMethod.Get, "/api/v1/announcements");
        Assert.InRange(list.ServerNow, DateTime.Now.AddMinutes(-1), DateTime.Now.AddMinutes(1));
        Assert.Equal(DateTimeKind.Unspecified, list.ServerNow.Kind);
        var start = Minute(list.ServerNow.AddMinutes(-5));
        var title = Unique("公告");

        var id = await SendAsync<int>(client, HttpMethod.Post, "/api/v1/announcements",
            new AnnouncementDto { Title = title, Content = "今晚維護", StartAt = start, TargetKind = AnnouncementTargetKinds.All, CreatedByAccount = "冒名" });

        var created = (await SendAsync<AnnouncementListDto>(client, HttpMethod.Get, "/api/v1/announcements")).Items.Single(x => x.Id == id);
        Assert.Equal(admin.Account, created.CreatedByAccount);
        Assert.Equal(start, created.StartAt);
        Assert.Null(created.EndAt);
        Assert.Equal("全體", created.TargetName);
        var createAudit = await SingleAuditAsync(AuditActions.Announcement.Create, admin.Id);
        Assert.Equal("Announcement", createAudit.TargetType);
        Assert.Equal(id.ToString(CultureInfo.InvariantCulture), createAudit.TargetId);
        Assert.Equal($"title={title}; target=All:", createAudit.Detail);

        var role = await DefaultRoleIdAsync();
        var end = start.AddDays(1);
        var updated = await SendForResultAsync<object>(client, HttpMethod.Put, $"/api/v1/announcements/{id}",
            new AnnouncementDto { Id = id, Title = title + "改", Content = "改期", StartAt = start, EndAt = end, TargetKind = AnnouncementTargetKinds.Role, TargetId = role, ConcurrencyStamp = created.ConcurrencyStamp });
        Assert.Equal("已更新公告。", updated.Message);
        var stale = await client.PutAsJsonAsync($"/api/v1/announcements/{id}",
            new AnnouncementDto { Id = id, Title = "晚一步", Content = "x", StartAt = start, TargetKind = AnnouncementTargetKinds.All, ConcurrencyStamp = created.ConcurrencyStamp });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(ConcurrencyStampHelper.ConflictMessage, (await stale.Content.ReadFromJsonAsync<ApiResult<object>>())!.ErrorMessage);
        var after = (await SendAsync<AnnouncementListDto>(client, HttpMethod.Get, "/api/v1/announcements")).Items.Single(x => x.Id == id);
        Assert.Equal(end, after.EndAt);
        Assert.Equal(MagicObjectHelper.預設角色, after.TargetName);
        var updateAudit = await SingleAuditAsync(AuditActions.Announcement.Update, admin.Id);
        Assert.Equal($"title={title}改; target=Role:{role}", updateAudit.Detail);

        var staleDelete = await client.DeleteAsync($"/api/v1/announcements/{id}?concurrencyStamp={created.ConcurrencyStamp}");
        Assert.Equal(HttpStatusCode.Conflict, staleDelete.StatusCode);
        var deleted = await SendForResultAsync<object>(client, HttpMethod.Delete, $"/api/v1/announcements/{id}?concurrencyStamp={after.ConcurrencyStamp}");
        Assert.Equal($"已刪除「{title}改」。", deleted.Message);
        var deleteAudit = await SingleAuditAsync(AuditActions.Announcement.Delete, admin.Id);
        Assert.Equal(id.ToString(CultureInfo.InvariantCulture), deleteAudit.TargetId);
        Assert.Equal($"title={title}改", deleteAudit.Detail);
        Assert.DoesNotContain((await SendAsync<AnnouncementListDto>(client, HttpMethod.Get, "/api/v1/announcements")).Items, x => x.Id == id);
    }

    [Fact]
    public async Task Announcements_MissingIds_ShouldBeNotFound_AndMismatchedIdsABadRequest()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var dto = new AnnouncementDto { Id = 999999, Title = "t", Content = "c", StartAt = DateTime.Today, TargetKind = AnnouncementTargetKinds.All, ConcurrencyStamp = "x" };

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/v1/announcements/999999", dto)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/v1/announcements/999999?concurrencyStamp=x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/announcements/1", dto)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync("/api/v1/announcements/999999")).StatusCode);
    }

    [Theory]
    [InlineData("", "內容", 0, null, "標題不可留空，最多 100 個字。")]
    [InlineData("標題", " ", 0, null, "內容不可留空，最多 2000 個字。")]
    [InlineData("標題", "內容", -1, null, "結束時間必須晚於開始時間。")]
    [InlineData("標題", "內容", 0, AnnouncementTargetKinds.Role, "請選擇對象角色。")]
    [InlineData("標題", "內容", 0, "Everyone", "對象種類不正確。")]
    public async Task Announcements_InvalidInput_ShouldBeRejected_AndNotAudited(string title, string content, int endOffsetHours, string? targetKind, string message)
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var start = Minute(DateTime.Now);

        var response = await client.PostAsJsonAsync("/api/v1/announcements", new AnnouncementDto
        {
            Title = title,
            Content = content,
            StartAt = start,
            EndAt = endOffsetHours == 0 ? null : start.AddHours(endOffsetHours),
            TargetKind = targetKind ?? AnnouncementTargetKinds.All,
            TargetId = targetKind == AnnouncementTargetKinds.Role ? 999999 : null,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(message, (await response.Content.ReadFromJsonAsync<ApiResult<object>>())!.ErrorMessage);
        Assert.Equal(0, await CountAuditAsync(AuditActions.Announcement.Create, admin.Id));
    }

    /// <summary>「公告在另一個分頁出現」的伺服器端：存檔後公告快取立即失效，其他人下一次取 active 就看得到；刪除後立即消失。</summary>
    [Fact]
    public async Task Announcements_ShouldReachOtherUsersBannerOnTheNextPoll()
    {
        using var adminClient = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        using var userClient = await SignedInClientAsync(await AddUserAsync(isAdmin: false));
        var before = await SendAsync<List<ActiveAnnouncementDto>>(userClient, HttpMethod.Get, "/api/v1/announcements/active");
        var now = (await SendAsync<AnnouncementListDto>(adminClient, HttpMethod.Get, "/api/v1/announcements")).ServerNow;
        var title = Unique("橫幅");

        var id = await SendAsync<int>(adminClient, HttpMethod.Post, "/api/v1/announcements",
            new AnnouncementDto { Title = title, Content = "內容", StartAt = Minute(now.AddMinutes(-1)), TargetKind = AnnouncementTargetKinds.All });

        Assert.DoesNotContain(before, x => x.Id == id);
        Assert.Contains(await SendAsync<List<ActiveAnnouncementDto>>(userClient, HttpMethod.Get, "/api/v1/announcements/active"), x => x.Id == id && x.Title == title);
        var stamp = (await SendAsync<AnnouncementListDto>(adminClient, HttpMethod.Get, "/api/v1/announcements")).Items.Single(x => x.Id == id).ConcurrencyStamp;
        await SendForResultAsync<object>(adminClient, HttpMethod.Delete, $"/api/v1/announcements/{id}?concurrencyStamp={stamp}");
        Assert.DoesNotContain(await SendAsync<List<ActiveAnnouncementDto>>(userClient, HttpMethod.Get, "/api/v1/announcements/active"), x => x.Id == id);
    }

    // ───────────────────────────── 共通 ─────────────────────────────

    /// <summary>本地時間以「不帶時差」送出：帶了 +08:00，WASM 會換成瀏覽器的時區。</summary>
    [Fact]
    public async Task LocalTimes_ShouldBeSentWithoutAnOffset()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var item = await ItemAsync(client, "SlowOperationSettings:DbCommandMs");
        var target = item.EffectiveValue == "5555" ? "5556" : "5555";
        await SendForResultAsync<object>(client, HttpMethod.Post, "/api/v1/system-parameters/save",
            new SystemParameterSaveRequestDto { Key = "SlowOperationSettings:DbCommandMs", Value = target, ExpectedStamp = item.Override?.ConcurrencyStamp });
        await SendAsync<int>(client, HttpMethod.Post, "/api/v1/announcements",
            new AnnouncementDto { Title = Unique("時差"), Content = "c", StartAt = Minute(DateTime.Now), EndAt = Minute(DateTime.Now).AddDays(1), TargetKind = AnnouncementTargetKinds.All });

        var texts = new[]
        {
            await (await client.GetAsync("/api/v1/system-parameters")).Content.ReadAsStringAsync(),
            await (await client.GetAsync("/api/v1/announcements")).Content.ReadAsStringAsync(),
        };

        var offset = new Regex(@"T\d{2}:\d{2}:\d{2}(\.\d+)?([+-]\d{2}:\d{2}|Z)");
        var envelope = new Regex("\"timestamp\":\"[^\"]*\"");
        Assert.All(texts, text => Assert.False(offset.IsMatch(envelope.Replace(text, string.Empty)), text));
        Assert.Contains("\"updatedAt\":\"", texts[0]);
        Assert.Contains("\"startAt\":\"", texts[1]);
        Assert.Contains("\"serverNow\":\"", texts[1]);
    }

    [Fact]
    public async Task NonAdmins_ShouldBeForbiddenFromEveryManagementEndpoint_AndTheDenialAudited_ButStillSeeTheBanner()
    {
        var user = await AddUserAsync(isAdmin: false);
        using var client = await SignedInClientAsync(user);

        var calls = new (HttpMethod Method, string Url, object? Body)[]
        {
            (HttpMethod.Get, "/api/v1/system-parameters", null),
            (HttpMethod.Post, "/api/v1/system-parameters/save", new SystemParameterSaveRequestDto { Key = ExternalCallKey, Value = "1" }),
            (HttpMethod.Post, "/api/v1/system-parameters/reset", new SystemParameterResetRequestDto { Key = ExternalCallKey, ExpectedStamp = "x" }),
            (HttpMethod.Get, "/api/v1/announcements", null),
            (HttpMethod.Post, "/api/v1/announcements", new AnnouncementDto { Title = "t", Content = "c", StartAt = DateTime.Today }),
            (HttpMethod.Put, "/api/v1/announcements/1", new AnnouncementDto { Id = 1, Title = "t", Content = "c", StartAt = DateTime.Today }),
            (HttpMethod.Delete, "/api/v1/announcements/1?concurrencyStamp=x", null),
        };

        foreach (var (method, url, body) in calls)
        {
            using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body, body.GetType()) };
            var response = await client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {url} → {(int)response.StatusCode}");
        }

        await using (var context = await CreateContextAsync())
        {
            Assert.Equal(calls.Length, await context.AuditLog.CountAsync(x => x.Action == AuditActions.Permission.Denied && x.ActorUserId == user.Id));
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/announcements/active")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/announcements/999999/dismiss", null)).StatusCode);
    }

    // ───────────────────────────── 工具 ─────────────────────────────

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..(prefix.Length + 10)];

    private static DateTime Minute(DateTime value)
        => new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, DateTimeKind.Unspecified);

    private static async Task<SystemParameterItemDto> ItemAsync(HttpClient client, string key)
        => (await SendAsync<SystemParameterOverviewDto>(client, HttpMethod.Get, "/api/v1/system-parameters")).Items.Single(x => x.Definition.Key == key);

    private async Task<int> DefaultRoleIdAsync()
    {
        await using var context = await CreateContextAsync();
        return (await context.RoleView.FirstAsync(x => x.Name == MagicObjectHelper.預設角色)).Id;
    }

    private async Task<int> CountAuditAsync(string action, int actorId)
    {
        await using var context = await CreateContextAsync();
        return await context.AuditLog.CountAsync(x => x.Action == action && x.ActorUserId == actorId);
    }

    private async Task<AuditLog> SingleAuditAsync(string action, int actorId)
    {
        await using var context = await CreateContextAsync();
        return await context.AuditLog.SingleAsync(x => x.Action == action && x.ActorUserId == actorId);
    }

    private static async Task<T> SendAsync<T>(HttpClient client, HttpMethod method, string url, object? body = null)
        => (await SendForResultAsync<T>(client, method, url, body)).Data!;

    /// <summary>送出並要求 200，回傳整個信封（要看成功訊息時用）。</summary>
    private static async Task<ApiResult<T>> SendForResultAsync<T>(HttpClient client, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{method} {url} → {(int)response.StatusCode} {text}");
        return JsonSerializer.Deserialize<ApiResult<T>>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
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
        var account = Unique("m7-");
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
