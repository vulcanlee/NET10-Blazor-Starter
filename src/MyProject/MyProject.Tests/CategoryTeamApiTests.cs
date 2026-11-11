using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Models;
using MyProject.Share.Helpers;

namespace MyProject.Tests;

/// <summary>
/// 分類、團隊的 Web API（0.10.2 起，WASM 遷移 M3）：改走與畫面相同的 <c>CategoryService</c>／<c>TeamService</c>，
/// 補齊 WASM 頁面要用的端點（顯示已刪除、還原、永久刪除、切換啟用、存檔前檢查、匯出、表單選項、部門樹）。
/// 稽核由服務寫，每個動作只記一筆；操作者是 Token 的使用者。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class CategoryTeamApiTests : IClassFixture<ApiTestApplicationFactoryWithBrowsingAudit>
{
    private const string Password = "Passw0rd-1";
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly ApiTestApplicationFactoryWithBrowsingAudit factory;

    public CategoryTeamApiTests(ApiTestApplicationFactoryWithBrowsingAudit factory)
    {
        this.factory = factory;
    }

    // ───────────────────────────── 分類 ─────────────────────────────

    [Fact]
    public async Task Category_FullLifecycle_ShouldWork_AndWriteEachAuditOnce()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var name = Unique("分類");

        var created = await SendAsync<CategoryDto>(client, HttpMethod.Post, "/api/v1/Category", new CategoryCreateUpdateDto { Id = 0, Name = name, Description = "描述" });
        var id = created.Id!.Value;
        Assert.True(id > 0);

        var fetched = await SendAsync<CategoryDto>(client, HttpMethod.Get, $"/api/v1/Category/{id}");
        Assert.Equal(name, fetched.Name);
        Assert.False(string.IsNullOrEmpty(fetched.ConcurrencyStamp));

        fetched.Description = "改過";
        await SendAsync<object>(client, HttpMethod.Put, $"/api/v1/Category/{id}", fetched);

        await SendAsync<object>(client, HttpMethod.Put, $"/api/v1/Category/{id}/enabled", new SetEnabledRequestDto { Enabled = false });
        Assert.False((await SendAsync<CategoryDto>(client, HttpMethod.Get, $"/api/v1/Category/{id}")).IsEnabled);

        await SendAsync<object>(client, HttpMethod.Delete, $"/api/v1/Category/{id}");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/Category/{id}")).StatusCode);

        var deleted = await SearchAsync<CategoryDto>(client, "Category", new CategorySearchRequestDto { Keyword = name, Deleted = true });
        var row = Assert.Single(deleted.Items);
        Assert.True(row.IsDeleted);
        Assert.Equal(admin.Account, row.DeletedBy);
        Assert.NotNull(row.DeletedAt);

        await SendAsync<object>(client, HttpMethod.Post, $"/api/v1/Category/{id}/restore");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/Category/{id}")).StatusCode);

        await SendAsync<object>(client, HttpMethod.Delete, $"/api/v1/Category/{id}");
        await SendAsync<object>(client, HttpMethod.Delete, $"/api/v1/Category/{id}/purge");
        Assert.Empty((await SearchAsync<CategoryDto>(client, "Category", new CategorySearchRequestDto { Keyword = name, Deleted = true })).Items);

        var actions = await AuditActionsForAsync("Category", id.ToString(), admin.Id);
        Assert.Equal(
            [
                AuditActions.Category.Create, AuditActions.Category.View, AuditActions.Category.Update, AuditActions.Category.Disable,
                AuditActions.Category.View, AuditActions.Category.Delete, AuditActions.Category.Restore, AuditActions.Category.View,
                AuditActions.Category.Delete, AuditActions.Category.Purge,
            ],
            actions);
    }

    [Fact]
    public async Task Category_Update_WithAStaleStamp_ShouldReturn409_AndKeepTheOtherChange()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var created = await SendAsync<CategoryDto>(client, HttpMethod.Post, "/api/v1/Category", new CategoryCreateUpdateDto { Id = 0, Name = Unique("並行") });
        var first = await SendAsync<CategoryDto>(client, HttpMethod.Get, $"/api/v1/Category/{created.Id}");
        var second = await SendAsync<CategoryDto>(client, HttpMethod.Get, $"/api/v1/Category/{created.Id}");

        first.Description = "先存的";
        await SendAsync<object>(client, HttpMethod.Put, $"/api/v1/Category/{created.Id}", first);
        second.Description = "後存的";
        var response = await client.PutAsJsonAsync($"/api/v1/Category/{created.Id}", second);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ConcurrencyStampHelper.ConflictMessage, (await ReadAsync<object>(response)).ErrorMessage);
        Assert.Equal("先存的", (await SendAsync<CategoryDto>(client, HttpMethod.Get, $"/api/v1/Category/{created.Id}")).Description);
    }

    [Fact]
    public async Task Category_DuplicateName_ShouldReturn409_ForCreateAndCheck_IgnoringCaseAndSpaces()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var name = Unique("Dup");
        await SendAsync<CategoryDto>(client, HttpMethod.Post, "/api/v1/Category", new CategoryCreateUpdateDto { Id = 0, Name = name });

        var create = await client.PostAsJsonAsync("/api/v1/Category", new CategoryCreateUpdateDto { Id = 0, Name = " " + name.ToUpperInvariant() + " " });
        Assert.Equal(HttpStatusCode.Conflict, create.StatusCode);

        var check = await client.PostAsJsonAsync("/api/v1/Category/check", new CategoryCreateUpdateDto { Id = 0, Name = name.ToLowerInvariant() });
        Assert.Equal(HttpStatusCode.Conflict, check.StatusCode);
        Assert.Equal("分類名稱已存在，無法新增。", (await ReadAsync<object>(check)).ErrorMessage);

        var free = await client.PostAsJsonAsync("/api/v1/Category/check", new CategoryCreateUpdateDto { Id = 0, Name = Unique("Free") });
        Assert.Equal(HttpStatusCode.OK, free.StatusCode);
    }

    [Fact]
    public async Task Category_Restore_WhenAnActiveOneHasTheSameName_ShouldReturn409()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var name = Unique("還原");
        var old = await SendAsync<CategoryDto>(client, HttpMethod.Post, "/api/v1/Category", new CategoryCreateUpdateDto { Id = 0, Name = name });
        await SendAsync<object>(client, HttpMethod.Delete, $"/api/v1/Category/{old.Id}");
        await SendAsync<CategoryDto>(client, HttpMethod.Post, "/api/v1/Category", new CategoryCreateUpdateDto { Id = 0, Name = name });

        var response = await client.PostAsync($"/api/v1/Category/{old.Id}/restore", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("已有同名的分類", (await ReadAsync<object>(response)).ErrorMessage);
    }

    [Fact]
    public async Task Category_Purge_ShouldOnlyWorkOnDeletedRecords()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var created = await SendAsync<CategoryDto>(client, HttpMethod.Post, "/api/v1/Category", new CategoryCreateUpdateDto { Id = 0, Name = Unique("永久") });

        var response = await client.DeleteAsync($"/api/v1/Category/{created.Id}/purge");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/Category/{created.Id}")).StatusCode);
    }

    [Fact]
    public async Task Category_UnknownId_ShouldReturn404_ForEveryEndpoint()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        const int missing = 987654;
        var dto = new CategoryCreateUpdateDto { Id = missing, Name = Unique("無"), ConcurrencyStamp = "x" };

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/Category/{missing}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/v1/Category/{missing}", dto)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/v1/Category/{missing}/enabled", new SetEnabledRequestDto())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/Category/{missing}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/v1/Category/{missing}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/Category/{missing}/purge")).StatusCode);

        // 與改寫前相同：不是數字的 id 是 400（模型繫結），不是 404。
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/Category/abc")).StatusCode);
    }

    [Fact]
    public async Task Category_Search_ShouldFilterSortAndAuditOnlyWhenTheUserSearches()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var prefix = Unique("搜");
        await SendAsync<CategoryDto>(client, HttpMethod.Post, "/api/v1/Category", new CategoryCreateUpdateDto { Id = 0, Name = prefix + "B" });
        await SendAsync<CategoryDto>(client, HttpMethod.Post, "/api/v1/Category", new CategoryCreateUpdateDto { Id = 0, Name = prefix + "A", IsEnabled = false });

        var sorted = await SearchAsync<CategoryDto>(client, "Category", new CategorySearchRequestDto { Keyword = prefix, SortBy = "NAME" });
        Assert.Equal([prefix + "A", prefix + "B"], sorted.Items.Select(x => x.Name));
        Assert.Equal(2, sorted.TotalCount);

        var enabledOnly = await SearchAsync<CategoryDto>(client, "Category", new CategorySearchRequestDto { Keyword = prefix, IsEnabled = true });
        Assert.Equal([prefix + "B"], enabledOnly.Items.Select(x => x.Name));

        var paged = await SearchAsync<CategoryDto>(client, "Category", new CategorySearchRequestDto { Keyword = prefix, SortBy = "name", PageSize = 1, PageIndex = 2 });
        Assert.Equal([prefix + "B"], paged.Items.Select(x => x.Name));
        Assert.Equal(2, paged.TotalPages);

        Assert.Equal(0, await CountAuditAsync(AuditActions.Category.Search, admin.Id));
        await SearchAsync<CategoryDto>(client, "Category", new CategorySearchRequestDto { Keyword = prefix, RecordSearch = true });
        Assert.Equal(1, await CountAuditAsync(AuditActions.Category.Search, admin.Id));
    }

    [Fact]
    public async Task Category_Export_ShouldReturnAnXlsx_WithTheSameRowsAsTheList_AndAudit()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var prefix = Unique("匯");
        await SendAsync<CategoryDto>(client, HttpMethod.Post, "/api/v1/Category", new CategoryCreateUpdateDto { Id = 0, Name = prefix + "1" });
        await SendAsync<CategoryDto>(client, HttpMethod.Post, "/api/v1/Category", new CategoryCreateUpdateDto { Id = 0, Name = prefix + "2" });

        var response = await client.PostAsJsonAsync("/api/v1/Category/export", new CategorySearchRequestDto { Keyword = prefix, SortBy = "name" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(XlsxContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith(".xlsx", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        using var workbook = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var sheet = workbook.Worksheets.First();
        Assert.Equal("名稱", sheet.Cell(1, 1).GetString());
        Assert.Equal([prefix + "1", prefix + "2"], [sheet.Cell(2, 1).GetString(), sheet.Cell(3, 1).GetString()]);
        Assert.True(sheet.Cell(4, 1).IsEmpty());
        Assert.Equal(1, await CountAuditAsync(AuditActions.Category.Export, admin.Id));
    }

    [Fact]
    public async Task Category_TeamScope_ShouldHideOtherTeamsRecords_LikeTheList()
    {
        var teamA = await AddTeamAsync(Unique("甲隊"));
        var teamB = await AddTeamAsync(Unique("乙隊"));
        using var admin = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var mine = await SendAsync<CategoryDto>(admin, HttpMethod.Post, "/api/v1/Category", new CategoryCreateUpdateDto { Id = 0, Name = Unique("甲的"), Teams = TagStringHelper.ToStored([teamA.Name]) });
        var theirs = await SendAsync<CategoryDto>(admin, HttpMethod.Post, "/api/v1/Category", new CategoryCreateUpdateDto { Id = 0, Name = Unique("乙的"), Teams = TagStringHelper.ToStored([teamB.Name]) });

        var member = await AddUserAsync(isAdmin: false, permissions: AllCategoryActions, teams: [teamA.Id]);
        using var client = await SignedInClientAsync(member);

        var list = await SearchAsync<CategoryDto>(client, "Category", new CategorySearchRequestDto { PageSize = SearchRequestBaseDto.MaxPageSize });
        Assert.Contains(list.Items, x => x.Id == mine.Id);
        Assert.DoesNotContain(list.Items, x => x.Id == theirs.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/Category/{theirs.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/Category/{theirs.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/v1/Category/{theirs.Id}/enabled", new SetEnabledRequestDto())).StatusCode);

        var options = await SendAsync<CategoryFormOptionsDto>(client, HttpMethod.Get, "/api/v1/Category/options");
        Assert.False(options.IsAdmin);
        Assert.Contains(teamA.Name, options.MyTeams);
        Assert.DoesNotContain(teamB.Name, options.MyTeams);
        Assert.Contains(teamB.Name, options.Teams);

        // 非管理員不能指定範圍外的團隊（與畫面相同的規則）。
        var outside = await client.PostAsJsonAsync("/api/v1/Category", new CategoryCreateUpdateDto { Id = 0, Name = Unique("越權"), Teams = TagStringHelper.ToStored([teamB.Name]) });
        Assert.Equal(HttpStatusCode.BadRequest, outside.StatusCode);
    }

    [Fact]
    public async Task ViewOnlyRole_ShouldReadButBeForbiddenFromEveryWrite()
    {
        using var admin = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var category = await SendAsync<CategoryDto>(admin, HttpMethod.Post, "/api/v1/Category", new CategoryCreateUpdateDto { Id = 0, Name = Unique("唯讀看") });
        var team = await SendAsync<TeamDto>(admin, HttpMethod.Post, "/api/v1/Team", new TeamCreateUpdateDto { Id = 0, Name = Unique("唯讀隊") });

        var viewer = await AddUserAsync(isAdmin: false, permissions: [PermissionKey.For(MagicObjectHelper.角色_分類清單, PermissionActions.View), PermissionKey.For(MagicObjectHelper.角色_團隊清單, PermissionActions.View)]);
        using var client = await SignedInClientAsync(viewer);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/Category/{category.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/Category/search", new CategorySearchRequestDto())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/Category/export", new CategorySearchRequestDto())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/Team/all")).StatusCode);

        foreach (var (method, url) in new[]
        {
            ("POST", "/api/v1/Category"),
            ("PUT", $"/api/v1/Category/{category.Id}"),
            ("PUT", $"/api/v1/Category/{category.Id}/enabled"),
            ("DELETE", $"/api/v1/Category/{category.Id}"),
            ("POST", $"/api/v1/Category/{category.Id}/restore"),
            ("DELETE", $"/api/v1/Category/{category.Id}/purge"),
            ("POST", "/api/v1/Team"),
            ("PUT", $"/api/v1/Team/{team.Id}"),
            ("PUT", $"/api/v1/Team/{team.Id}/enabled"),
            ("DELETE", $"/api/v1/Team/{team.Id}"),
            ("POST", $"/api/v1/Team/{team.Id}/restore"),
            ("DELETE", $"/api/v1/Team/{team.Id}/purge"),
        })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url) { Content = JsonContent.Create(new { name = "x", id = category.Id, enabled = false }) };
            var response = await client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {url} → {(int)response.StatusCode}");
            Assert.Equal(403, (await ReadAsync<object>(response)).StatusCode);
        }
    }

    /// <summary>審查修正（0.10.2）：「顯示已刪除」與 Server 版相同，要有刪除權限；只有檢視權限的人查、匯出已刪除的資料回 403。</summary>
    [Fact]
    public async Task DeletedList_WithoutDeletePermission_ShouldReturn403()
    {
        var viewer = await AddUserAsync(isAdmin: false, permissions: [PermissionKey.For(MagicObjectHelper.角色_分類清單, PermissionActions.View), PermissionKey.For(MagicObjectHelper.角色_團隊清單, PermissionActions.View)]);
        using var client = await SignedInClientAsync(viewer);

        foreach (var url in new[] { "/api/v1/Category/search", "/api/v1/Category/export", "/api/v1/Team/search", "/api/v1/Team/export" })
        {
            var response = await client.PostAsJsonAsync(url, new { deleted = true });
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{url} → {(int)response.StatusCode}");
        }

        using var admin = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/Category/search", new CategorySearchRequestDto { Deleted = true })).StatusCode);
    }

    /// <summary>
    /// 審查修正（0.10.2）：存檔前檢查要有新增（Id 為 0）或修改權限；修改時先看團隊範圍，範圍外的一律 404，
    /// 不能用名稱檢查探出範圍外的資料是否存在。還原、永久刪除也套用範圍。
    /// </summary>
    [Fact]
    public async Task CheckAndOutOfScopeWrites_ShouldNotRevealOtherTeamsRecords()
    {
        var teamA = await AddTeamAsync(Unique("甲隊"));
        var teamB = await AddTeamAsync(Unique("乙隊"));
        using var admin = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var theirs = await SendAsync<CategoryDto>(admin, HttpMethod.Post, "/api/v1/Category", new CategoryCreateUpdateDto { Id = 0, Name = Unique("乙的"), Teams = TagStringHelper.ToStored([teamB.Name]) });
        var mine = await SendAsync<CategoryDto>(admin, HttpMethod.Post, "/api/v1/Category", new CategoryCreateUpdateDto { Id = 0, Name = Unique("甲的"), Teams = TagStringHelper.ToStored([teamA.Name]) });

        using var member = await SignedInClientAsync(await AddUserAsync(isAdmin: false, permissions: AllCategoryActions, teams: [teamA.Id]));
        var probe = new CategoryCreateUpdateDto { Id = theirs.Id, Name = mine.Name, ConcurrencyStamp = "x" };
        Assert.Equal(HttpStatusCode.NotFound, (await member.PostAsJsonAsync("/api/v1/Category/check", probe)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.PutAsJsonAsync($"/api/v1/Category/{theirs.Id}", probe)).StatusCode);

        await SendAsync<object>(admin, HttpMethod.Delete, $"/api/v1/Category/{theirs.Id}");
        Assert.Equal(HttpStatusCode.NotFound, (await member.PostAsync($"/api/v1/Category/{theirs.Id}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.DeleteAsync($"/api/v1/Category/{theirs.Id}/purge")).StatusCode);

        using var viewer = await SignedInClientAsync(await AddUserAsync(isAdmin: false, permissions: [PermissionKey.For(MagicObjectHelper.角色_分類清單, PermissionActions.View)]));
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/Category/check", new CategoryCreateUpdateDto { Id = 0, Name = mine.Name })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/v1/Category/check", new CategoryCreateUpdateDto { Id = mine.Id, Name = mine.Name })).StatusCode);
    }

    /// <summary>審查修正（0.10.2）：部門樹模式的匯出也照「啟用狀態」條件篩選。</summary>
    [Fact]
    public async Task Team_Export_InTreeMode_ShouldHonourTheEnabledFilter()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var off = await SendAsync<TeamDto>(client, HttpMethod.Post, "/api/v1/Team", new TeamCreateUpdateDto { Id = 0, Name = Unique("停用隊"), IsEnabled = false });

        var response = await client.PostAsJsonAsync("/api/v1/Team/export", new TeamSearchRequestDto { IsEnabled = true, PageSize = SearchRequestBaseDto.MaxPageSize });

        using var workbook = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        Assert.DoesNotContain(workbook.Worksheets.First().RowsUsed(), r => r.Cell(1).GetString() == off.Name);
    }

    // ───────────────────────────── 團隊 ─────────────────────────────

    [Fact]
    public async Task Team_FullLifecycle_ShouldWork_AndWriteEachAuditOnce()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var name = Unique("隊");

        var created = await SendAsync<TeamDto>(client, HttpMethod.Post, "/api/v1/Team", new TeamCreateUpdateDto { Id = 0, Name = name, Code = Unique("C") });
        var id = created.Id!.Value;
        var fetched = await SendAsync<TeamDto>(client, HttpMethod.Get, $"/api/v1/Team/{id}");
        fetched.Description = "改過";
        await SendAsync<object>(client, HttpMethod.Put, $"/api/v1/Team/{id}", fetched);
        await SendAsync<object>(client, HttpMethod.Put, $"/api/v1/Team/{id}/enabled", new SetEnabledRequestDto { Enabled = false });
        await SendAsync<object>(client, HttpMethod.Delete, $"/api/v1/Team/{id}");
        Assert.Single((await SearchAsync<TeamDto>(client, "Team", new TeamSearchRequestDto { Keyword = name, Deleted = true })).Items);
        await SendAsync<object>(client, HttpMethod.Post, $"/api/v1/Team/{id}/restore");
        await SendAsync<object>(client, HttpMethod.Delete, $"/api/v1/Team/{id}");
        await SendAsync<object>(client, HttpMethod.Delete, $"/api/v1/Team/{id}/purge");

        var actions = await AuditActionsForAsync("Team", id.ToString(), admin.Id);
        Assert.Equal(
            [
                AuditActions.Team.Create, AuditActions.Team.View, AuditActions.Team.Update, AuditActions.Team.Disable,
                AuditActions.Team.Delete, AuditActions.Team.Restore, AuditActions.Team.Delete, AuditActions.Team.Purge,
            ],
            actions);
    }

    [Fact]
    public async Task Team_Tree_ParentCandidates_AndDeletingAParent_ShouldFollowTheHierarchyRules()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var parent = await SendAsync<TeamDto>(client, HttpMethod.Post, "/api/v1/Team", new TeamCreateUpdateDto { Id = 0, Name = Unique("上層") });
        var child = await SendAsync<TeamDto>(client, HttpMethod.Post, "/api/v1/Team", new TeamCreateUpdateDto { Id = 0, Name = Unique("下屬"), ParentId = parent.Id });

        var all = await SendAsync<List<TeamDto>>(client, HttpMethod.Get, "/api/v1/Team/all");
        Assert.Contains(all, x => x.Id == child.Id && x.ParentId == parent.Id);

        var candidates = await SendAsync<List<TeamNodeDto>>(client, HttpMethod.Get, $"/api/v1/Team/{parent.Id}/parent-candidates");
        Assert.DoesNotContain(candidates, x => x.Id == parent.Id || x.Id == child.Id);
        var forNew = await SendAsync<List<TeamNodeDto>>(client, HttpMethod.Get, "/api/v1/Team/0/parent-candidates");
        Assert.Contains(forNew, x => x.Id == child.Id);

        // 與改寫前的 API 相同：有下屬不可刪回 400。
        var deleteParent = await client.DeleteAsync($"/api/v1/Team/{parent.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, deleteParent.StatusCode);

        var cycle = await SendAsync<TeamDto>(client, HttpMethod.Get, $"/api/v1/Team/{parent.Id}");
        cycle.ParentId = child.Id;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/v1/Team/{parent.Id}", cycle)).StatusCode);
    }

    [Fact]
    public async Task Team_Rename_ShouldRenameTheReferencesInCategories()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var team = await SendAsync<TeamDto>(client, HttpMethod.Post, "/api/v1/Team", new TeamCreateUpdateDto { Id = 0, Name = Unique("舊名") });
        var category = await SendAsync<CategoryDto>(client, HttpMethod.Post, "/api/v1/Category", new CategoryCreateUpdateDto { Id = 0, Name = Unique("引用"), Teams = TagStringHelper.ToStored([team.Name]) });

        var fetched = await SendAsync<TeamDto>(client, HttpMethod.Get, $"/api/v1/Team/{team.Id}");
        var newName = Unique("新名");
        fetched.Name = newName;
        await SendAsync<object>(client, HttpMethod.Put, $"/api/v1/Team/{team.Id}", fetched);

        var after = await SendAsync<CategoryDto>(client, HttpMethod.Get, $"/api/v1/Category/{category.Id}");
        Assert.Equal([newName], TagStringHelper.ToList(after.Teams));
    }

    [Fact]
    public async Task Team_DuplicateNameOrCode_ShouldReturn409()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var code = Unique("CODE");
        var first = await SendAsync<TeamDto>(client, HttpMethod.Post, "/api/v1/Team", new TeamCreateUpdateDto { Id = 0, Name = Unique("隊名"), Code = code });

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/Team", new TeamCreateUpdateDto { Id = 0, Name = first.Name.ToUpperInvariant() })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/Team", new TeamCreateUpdateDto { Id = 0, Name = Unique("別名"), Code = code.ToLowerInvariant() })).StatusCode);
        var check = await client.PostAsJsonAsync("/api/v1/Team/check", new TeamCreateUpdateDto { Id = 0, Name = Unique("別名"), Code = code });
        Assert.Equal(HttpStatusCode.Conflict, check.StatusCode);
        Assert.Equal("團隊代號已存在，無法新增。", (await ReadAsync<object>(check)).ErrorMessage);
    }

    [Fact]
    public async Task Team_Export_InTreeMode_ShouldIncludeTheParentName()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var parent = await SendAsync<TeamDto>(client, HttpMethod.Post, "/api/v1/Team", new TeamCreateUpdateDto { Id = 0, Name = Unique("匯上") });
        var child = await SendAsync<TeamDto>(client, HttpMethod.Post, "/api/v1/Team", new TeamCreateUpdateDto { Id = 0, Name = Unique("匯下"), ParentId = parent.Id });

        var response = await client.PostAsJsonAsync("/api/v1/Team/export", new TeamSearchRequestDto());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var workbook = new XLWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        var sheet = workbook.Worksheets.First();
        var childRow = sheet.RowsUsed().Single(r => r.Cell(1).GetString() == child.Name);
        Assert.Equal(parent.Name, childRow.Cell(2).GetString());
        Assert.Equal(1, await CountAuditAsync(AuditActions.Team.Export, admin.Id));
    }

    // ───────────────────────────── WASM 頁面 ─────────────────────────────

    [Theory]
    [InlineData("/categories")]
    [InlineData("/teams")]
    public async Task OpeningAWasmPage_WithoutPermission_ShouldAuditTheDenial_LikeTheServerPage(string path)
    {
        var outsider = await AddUserAsync(isAdmin: false, permissions: [PermissionKey.For(MagicObjectHelper.角色_專案項目, PermissionActions.View)]);
        var insider = await AddUserAsync(isAdmin: false, permissions: [.. AllCategoryActions, PermissionKey.For(MagicObjectHelper.角色_團隊清單, PermissionActions.View)]);

        foreach (var user in new[] { outsider, insider })
        {
            using var client = await SignedInClientAsync(user);
            var response = await client.PostAsJsonAsync("/api/v1/shell/page-view", new MyProject.Dtos.Shell.PageViewRequestDto { Path = path, From = "/App" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await using var context = await CreateContextAsync();
        Assert.Equal(1, await context.AuditLog.CountAsync(x => x.Action == AuditActions.Page.View && x.ActorUserId == outsider.Id && x.TargetId == path));
        var denied = await context.AuditLog.SingleAsync(x => x.Action == AuditActions.Permission.Denied && x.ActorUserId == outsider.Id);
        Assert.Equal(path, denied.TargetId);
        Assert.Equal("via=WASM", denied.Detail);
        Assert.False(await context.AuditLog.AnyAsync(x => x.Action == AuditActions.Permission.Denied && x.ActorUserId == insider.Id));
    }

    // ───────────────────────────── 異動歷程 ─────────────────────────────

    [Fact]
    public async Task AuditHistory_ForAdmins_ShouldListTheChanges_AndAuditOpeningAndViewingOnce()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var created = await SendAsync<CategoryDto>(client, HttpMethod.Post, "/api/v1/Category", new CategoryCreateUpdateDto { Id = 0, Name = Unique("歷程") });
        var fetched = await SendAsync<CategoryDto>(client, HttpMethod.Get, $"/api/v1/Category/{created.Id}");
        fetched.Description = "改過";
        await SendAsync<object>(client, HttpMethod.Put, $"/api/v1/Category/{created.Id}", fetched);

        var page = await SendAsync<PagedResult<AuditHistoryItemDto>>(client, HttpMethod.Get, $"/api/v1/audit-logs/history?targetType=Category&targetId={created.Id}&opened=true");
        Assert.Equal([AuditActions.Category.Update, AuditActions.Category.Create], page.Items.Select(x => x.Action));
        Assert.Equal(admin.Account, page.Items[0].ActorAccount);
        await SendAsync<PagedResult<AuditHistoryItemDto>>(client, HttpMethod.Get, $"/api/v1/audit-logs/history?targetType=Category&targetId={created.Id}&page=1");

        var update = page.Items[0];
        var content = await SendAsync<AuditContentDto>(client, HttpMethod.Get, $"/api/v1/audit-logs/{update.Id}/content");
        if (update.HasContent)
        {
            Assert.Contains("改過", content.Text);
        }

        Assert.Equal(1, await CountAuditAsync(AuditActions.Audit.Search, admin.Id));
        Assert.Equal(1, await CountAuditAsync(AuditActions.Audit.View, admin.Id));
    }

    [Fact]
    public async Task AuditHistory_ForNonAdmins_ShouldReturn403()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: false, permissions: AllCategoryActions));

        var history = await client.GetAsync("/api/v1/audit-logs/history?targetType=Category&targetId=1");
        var content = await client.GetAsync("/api/v1/audit-logs/1/content");

        Assert.Equal(HttpStatusCode.Forbidden, history.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, content.StatusCode);
        Assert.Equal(403, (await ReadAsync<object>(history)).StatusCode);
    }

    // ───────────────────────────── 工具 ─────────────────────────────

    private static readonly string[] AllCategoryActions =
    [
        PermissionKey.For(MagicObjectHelper.角色_分類清單, PermissionActions.View),
        PermissionKey.For(MagicObjectHelper.角色_分類清單, PermissionActions.Create),
        PermissionKey.For(MagicObjectHelper.角色_分類清單, PermissionActions.Edit),
        PermissionKey.For(MagicObjectHelper.角色_分類清單, PermissionActions.Delete),
    ];

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..(prefix.Length + 10)];

    private static async Task<T> SendAsync<T>(HttpClient client, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        var response = await client.SendAsync(request);
        var result = await ReadAsync<T>(response);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{method} {url} → {(int)response.StatusCode} {result.Message}");
        return result.Data!;
    }

    private static async Task<PagedResult<T>> SearchAsync<T>(HttpClient client, string resource, object request)
        => await SendAsync<PagedResult<T>>(client, HttpMethod.Post, $"/api/v1/{resource}/search", request);

    private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<ApiResult<T>>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException($"不是 ApiResult：{text}");
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

    /// <summary>新增使用者；給了 <paramref name="permissions"/> 就另建一個只有這些權限鍵的角色。</summary>
    private async Task<MyUser> AddUserAsync(bool isAdmin, string[]? permissions = null, int[]? teams = null)
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

        var account = Unique("m3-");
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
        foreach (var teamId in teams ?? [])
        {
            context.UserTeam.Add(new UserTeam { MyUserId = user.Id, TeamId = teamId });
        }

        await context.SaveChangesAsync();
        return user;
    }

    private async Task<Team> AddTeamAsync(string name)
    {
        await using var context = await CreateContextAsync();
        var team = new Team { Name = name, IsEnabled = true };
        context.Team.Add(team);
        await context.SaveChangesAsync();
        factory.Services.GetRequiredService<ITeamTreeCache>().Invalidate();
        return team;
    }

    private async Task<List<string>> AuditActionsForAsync(string targetType, string targetId, int actorId)
    {
        await using var context = await CreateContextAsync();
        return await context.AuditLog
            .Where(x => x.TargetType == targetType && x.TargetId == targetId && x.ActorUserId == actorId)
            .OrderBy(x => x.Id)
            .Select(x => x.Action)
            .ToListAsync();
    }

    private async Task<int> CountAuditAsync(string action, int actorId)
    {
        await using var context = await CreateContextAsync();
        return await context.AuditLog.CountAsync(x => x.Action == action && x.ActorUserId == actorId);
    }

    private Task<BackendDBContext> CreateContextAsync()
        => factory.Services.GetRequiredService<IDbContextFactory<BackendDBContext>>().CreateDbContextAsync();
}
