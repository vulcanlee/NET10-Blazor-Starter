using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
/// 角色的 Web API（0.10.4 起，WASM 遷移 M5）：只限管理員，走與畫面相同的 <c>RoleViewService</c>（權限矩陣轉權限鍵、預設角色保護、仍是主要角色不可刪），
/// 稽核由服務寫、查看與搜尋由 controller 寫，每個動作一筆。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class RoleApiTests : IClassFixture<ApiTestApplicationFactoryWithBrowsingAudit>
{
    private const string Password = "Passw0rd-1";
    private const string BaseUrl = "/api/v1/roles";
    private readonly ApiTestApplicationFactoryWithBrowsingAudit factory;

    public RoleApiTests(ApiTestApplicationFactoryWithBrowsingAudit factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task FullLifecycle_ShouldSaveTheMatrixAsPermissionKeys_AndAuditEachActionOnce()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var options = await SendAsync<RoleFormOptionsDto>(client, HttpMethod.Get, $"{BaseUrl}/options");
        var role = new RoleDto { Name = Unique("角色"), RequireTwoFactor = true, DefaultTeams = [], Permission = options.EmptyPermission };
        var projects = role.Permission.Groups.SelectMany(g => g.Permissions).First(n => n.Name == MagicObjectHelper.角色_專案項目);
        projects.Actions[PermissionActions.View] = true;
        projects.Actions[PermissionActions.Edit] = true;

        var created = await SendAsync<RoleDto>(client, HttpMethod.Post, BaseUrl, role);
        var fetched = await SendAsync<RoleDto>(client, HttpMethod.Get, $"{BaseUrl}/{created.Id}");

        Assert.True(created.Id > 0);
        var node = fetched.Permission.Groups.SelectMany(g => g.Permissions).First(n => n.Name == MagicObjectHelper.角色_專案項目);
        Assert.True(node.Actions[PermissionActions.Edit]);
        Assert.Contains(PermissionKey.For(MagicObjectHelper.角色_專案項目, PermissionActions.Edit), await PermissionKeysOfAsync(created.Id));

        fetched.Name = Unique("改名");
        await SendAsync<object>(client, HttpMethod.Put, $"{BaseUrl}/{created.Id}", fetched);
        var stale = await client.PutAsJsonAsync($"{BaseUrl}/{created.Id}", fetched);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        await SendAsync<object>(client, HttpMethod.Delete, $"{BaseUrl}/{created.Id}");
        var deleted = await SendAsync<PagedResult<RoleDto>>(client, HttpMethod.Post, $"{BaseUrl}/search", new RoleSearchRequestDto { Keyword = fetched.Name, Deleted = true });
        Assert.True(Assert.Single(deleted.Items).IsDeleted);
        await SendAsync<object>(client, HttpMethod.Post, $"{BaseUrl}/{created.Id}/restore");
        await SendAsync<object>(client, HttpMethod.Delete, $"{BaseUrl}/{created.Id}");
        await SendAsync<object>(client, HttpMethod.Delete, $"{BaseUrl}/{created.Id}/purge");

        Assert.Equal(
            [AuditActions.Role.Create, AuditActions.Role.View, AuditActions.Role.Update, AuditActions.Role.Delete, AuditActions.Role.Restore, AuditActions.Role.Delete, AuditActions.Role.Purge],
            await AuditActionsForAsync("RoleView", created.Id.ToString(), admin.Id));
    }

    [Fact]
    public async Task Search_ShouldIncludeTheMatrix_AndAuditOnlyWhenTheUserSearches()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);

        var page = await SendAsync<PagedResult<RoleDto>>(client, HttpMethod.Post, $"{BaseUrl}/search", new RoleSearchRequestDto { Keyword = MagicObjectHelper.預設角色, PageSize = 8 });
        await SendAsync<PagedResult<RoleDto>>(client, HttpMethod.Post, $"{BaseUrl}/search", new RoleSearchRequestDto { RecordSearch = true, SortBy = "name" });

        var defaultRole = Assert.Single(page.Items, x => x.Name == MagicObjectHelper.預設角色);
        Assert.NotEmpty(defaultRole.Permission.Groups);
        Assert.Equal(1, await CountAuditAsync(AuditActions.Role.Search, admin.Id));
    }

    [Fact]
    public async Task Rules_ShouldMatchTheScreen()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var options = await SendAsync<RoleFormOptionsDto>(client, HttpMethod.Get, $"{BaseUrl}/options");
        int defaultRoleId;
        await using (var context = await CreateContextAsync())
        {
            defaultRoleId = (await context.RoleView.FirstAsync(x => x.Name == MagicObjectHelper.預設角色)).Id;
        }

        // 預設角色不可刪（400）；重名 409；不存在 404。
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"{BaseUrl}/{defaultRoleId}")).StatusCode);
        var duplicate = await client.PostAsJsonAsync(BaseUrl, new RoleDto { Name = MagicObjectHelper.預設角色, Permission = options.EmptyPermission });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{BaseUrl}/999999")).StatusCode);

        // 仍是某人的主要角色時不可刪（400，訊息列出帳號）。
        var role = await SendAsync<RoleDto>(client, HttpMethod.Post, BaseUrl, new RoleDto { Name = Unique("使用中"), Permission = options.EmptyPermission });
        await AddUserAsync(isAdmin: false, roleId: role.Id);
        var inUse = await client.DeleteAsync($"{BaseUrl}/{role.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, inUse.StatusCode);
    }

    /// <summary>
    /// 審查修正（中）：矩陣只能勾權限目錄裡有的項目。API 呼叫端多塞一個「稽核紀錄」節點，不能讓管理員專屬的權限鍵被授予（AdminOnlyPermissionTests 的不變量）。
    /// 建立時間也不能被呼叫端改掉；名稱必填。
    /// </summary>
    [Fact]
    public async Task Save_ShouldOnlyAcceptTheCatalog_KeepCreateAt_AndRequireAName()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var options = await SendAsync<RoleFormOptionsDto>(client, HttpMethod.Get, $"{BaseUrl}/options");
        var role = await SendAsync<RoleDto>(client, HttpMethod.Post, BaseUrl, new RoleDto { Name = Unique("目錄"), Permission = options.EmptyPermission });
        var opened = await SendAsync<RoleDto>(client, HttpMethod.Get, $"{BaseUrl}/{role.Id}");
        opened.Permission.Groups[1].Permissions.Add(new RolePermissionNodeDto { Name = MagicObjectHelper.角色_稽核紀錄, Enable = true });
        opened.Permission.Groups.Add(new RolePermissionGroupDto { Name = "偽造群組", Enable = true });
        var originalCreateAt = opened.CreateAt;
        opened.CreateAt = new DateTime(2001, 1, 1);

        await SendAsync<object>(client, HttpMethod.Put, $"{BaseUrl}/{role.Id}", opened);

        var keys = await PermissionKeysOfAsync(role.Id);
        Assert.DoesNotContain(MagicObjectHelper.角色_稽核紀錄, keys);
        Assert.DoesNotContain("偽造群組", keys);
        var after = await SendAsync<RoleDto>(client, HttpMethod.Get, $"{BaseUrl}/{role.Id}");
        Assert.Equal(originalCreateAt, after.CreateAt);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(BaseUrl, new RoleDto { Name = "", Permission = options.EmptyPermission })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{BaseUrl}/search", new RoleSearchRequestDto { PageSize = 0 })).StatusCode);
    }

    [Fact]
    public async Task NonAdmins_ShouldBeForbidden()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: false));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{BaseUrl}/search", new RoleSearchRequestDto())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{BaseUrl}/options")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(BaseUrl, new RoleDto { Name = "越權" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"{BaseUrl}/1")).StatusCode);
    }

    // ───────────────────────────── 工具 ─────────────────────────────

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..(prefix.Length + 10)];

    private async Task<List<string>> PermissionKeysOfAsync(int roleId)
    {
        await using var context = await CreateContextAsync();
        return await context.RolePermissionMap.Where(x => x.RoleViewId == roleId).Select(x => x.Permission!.Key).ToListAsync();
    }

    private async Task<List<string>> AuditActionsForAsync(string targetType, string targetId, int actorId)
    {
        await using var context = await CreateContextAsync();
        return await context.AuditLog.Where(x => x.TargetType == targetType && x.TargetId == targetId && x.ActorUserId == actorId)
            .OrderBy(x => x.Id).Select(x => x.Action).ToListAsync();
    }

    private async Task<int> CountAuditAsync(string action, int actorId)
    {
        await using var context = await CreateContextAsync();
        return await context.AuditLog.CountAsync(x => x.Action == action && x.ActorUserId == actorId);
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

    private async Task<MyUser> AddUserAsync(bool isAdmin, int? roleId = null)
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
        var writer = scope.ServiceProvider.GetRequiredService<IRbacWriteService>();
        var role = roleId is { } id ? await context.RoleView.FirstAsync(x => x.Id == id) : await context.RoleView.FirstAsync(x => x.Name == MagicObjectHelper.預設角色);
        var account = Unique("m5r-");
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
