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
using MyProject.Dtos.Models;
using MyProject.Share.Helpers;

namespace MyProject.Tests;

/// <summary>
/// 存檔時檢查下拉選項仍然有效（0.10.15 起，畫面選項資料一致性）：前後端分離後，畫面上的選項是開窗時讀的，
/// 別的使用者可能在這之間刪除或停用了那個分類、團隊。存檔時伺服器擋下「這次新選的、已不存在或已停用」的值，回 400 與
/// 「所選的〇〇「名稱」已不存在或已停用…」；畫面收到失敗後重讀選項。
/// 原本就有、這次沒動的值不檢查：舊資料只改其他欄位時照常可存（例如分類後來被停用）。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class SelectedOptionValidationApiTests : IClassFixture<ApiTestApplicationFactory>
{
    private const string Password = "Passw0rd-1";

    private readonly ApiTestApplicationFactory factory;

    public SelectedOptionValidationApiTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    // ───────────────────────────── 專案：分類與團隊 ─────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Project_WithADeletedOrDisabledCategory_ShouldReturn400(bool delete)
    {
        using var client = await AdminClientAsync();
        var category = await AddCategoryAsync(Unique("分類"));
        await RetireCategoryAsync(category.Id, delete);

        var response = await client.PostAsJsonAsync("/api/v1/Project", NewProject(categories: [category.Name]));
        var result = await ReadAsync<object>(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"所選的分類「{category.Name}」已不存在或已停用", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Project_WithADeletedTeam_ShouldReturn400()
    {
        using var client = await AdminClientAsync();
        var team = await AddTeamAsync(Unique("隊"));
        await RetireTeamAsync(team.Id, delete: true);

        var response = await client.PostAsJsonAsync("/api/v1/Project", NewProject(teams: [team.Name]));
        var result = await ReadAsync<object>(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"所選的團隊「{team.Name}」已不存在或已停用", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Project_KeepingACategoryThatWasDisabledLater_ShouldStillSave()
    {
        using var client = await AdminClientAsync();
        var category = await AddCategoryAsync(Unique("分類"));
        var created = await SendAsync<ProjectDto>(client, HttpMethod.Post, "/api/v1/Project", NewProject(categories: [category.Name]));
        await RetireCategoryAsync(category.Id, delete: false);

        var current = await SendAsync<ProjectDto>(client, HttpMethod.Get, $"/api/v1/Project/{created.Id}");
        current.Description = "只改說明";
        var response = await client.PutAsJsonAsync($"/api/v1/Project/{created.Id}", current);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Project_WithValidOptions_ShouldSave()
    {
        using var client = await AdminClientAsync();
        var category = await AddCategoryAsync(Unique("分類"));
        var team = await AddTeamAsync(Unique("隊"));

        var response = await client.PostAsJsonAsync("/api/v1/Project", NewProject(categories: [category.Name], teams: [team.Name]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ───────────────────────────── 分類：適用團隊 ─────────────────────────────

    [Fact]
    public async Task Category_WithADisabledTeam_ShouldReturn400_ButKeepingItShouldStillSave()
    {
        using var client = await AdminClientAsync();
        var kept = await AddTeamAsync(Unique("隊"));
        var category = await SendAsync<CategoryDto>(client, HttpMethod.Post, "/api/v1/Category",
            new CategoryCreateUpdateDto { Id = 0, Name = Unique("分類"), Teams = TagStringHelper.ToStored([kept.Name]) });
        await RetireTeamAsync(kept.Id, delete: false);
        var fresh = await AddTeamAsync(Unique("隊"));
        await RetireTeamAsync(fresh.Id, delete: false);

        var current = await SendAsync<CategoryDto>(client, HttpMethod.Get, $"/api/v1/Category/{category.Id}");
        current.Description = "只改說明";
        var keep = await client.PutAsJsonAsync($"/api/v1/Category/{category.Id}", current);
        Assert.Equal(HttpStatusCode.OK, keep.StatusCode);

        var again = await SendAsync<CategoryDto>(client, HttpMethod.Get, $"/api/v1/Category/{category.Id}");
        again.Teams = TagStringHelper.ToStored([kept.Name, fresh.Name]);
        var add = await client.PutAsJsonAsync($"/api/v1/Category/{category.Id}", again);
        var result = await ReadAsync<object>(add);

        Assert.Equal(HttpStatusCode.BadRequest, add.StatusCode);
        Assert.Contains($"所選的團隊「{fresh.Name}」已不存在或已停用", result.ErrorMessage, StringComparison.Ordinal);
    }

    // ───────────────────────────── 角色：預設團隊 ─────────────────────────────

    [Fact]
    public async Task Role_WithADeletedDefaultTeam_ShouldReturn400()
    {
        using var client = await AdminClientAsync();
        var options = await SendAsync<RoleFormOptionsDto>(client, HttpMethod.Get, "/api/v1/roles/options");
        var team = await AddTeamAsync(Unique("隊"));
        await RetireTeamAsync(team.Id, delete: true);

        var response = await client.PostAsJsonAsync("/api/v1/roles",
            new RoleDto { Name = Unique("角色"), DefaultTeams = [team.Name], Permission = options.EmptyPermission });
        var result = await ReadAsync<object>(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"所選的團隊「{team.Name}」已不存在或已停用", result.ErrorMessage, StringComparison.Ordinal);
    }

    // ───────────────────────────── 使用者：團隊 ─────────────────────────────

    [Fact]
    public async Task User_WithADisabledTeam_ShouldReturn400_InsteadOfSilentlyDroppingIt()
    {
        using var client = await AdminClientAsync();
        var options = await SendAsync<UserFormOptionsDto>(client, HttpMethod.Get, "/api/v1/users/options");
        var team = await AddTeamAsync(Unique("隊"));
        await RetireTeamAsync(team.Id, delete: false);

        var response = await client.PostAsJsonAsync("/api/v1/users", new UserDto
        {
            Account = Unique("u-"),
            Name = "新同仁",
            Password = "Abcd!2345678",
            RoleViewId = options.DefaultRoleId,
            TeamNames = [team.Name],
        });
        var result = await ReadAsync<object>(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"所選的團隊「{team.Name}」已不存在或已停用", result.ErrorMessage, StringComparison.Ordinal);
    }

    // ───────────────────────────── 工具 ─────────────────────────────

    private static ProjectCreateUpdateDto NewProject(string[]? categories = null, string[]? teams = null) => new()
    {
        Id = 0,
        Title = Unique("專案"),
        Status = "進行中",
        Priority = "中",
        Owner = "王小明",
        Categories = categories is null ? null : TagStringHelper.ToStored(categories),
        Teams = teams is null ? null : TagStringHelper.ToStored(teams),
    };

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

    private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<ApiResult<T>>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException($"不是 ApiResult：{text}");
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        MyUser user;
        await using (var context = await CreateContextAsync())
        {
            var role = await context.RoleView.FirstAsync(x => x.Name == MagicObjectHelper.預設角色);
            var account = Unique("opt-");
            user = new MyUser
            {
                Account = account,
                Name = account,
                Password = SecurePasswordHasher.HashPassword(Password),
                Status = true,
                IsAdmin = true,
                RoleViewId = role.Id,
                PasswordChangedAtUtc = DateTime.UtcNow,
            };
            context.MyUser.Add(user);
            await context.SaveChangesAsync();
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRbacWriteService>().SyncUserRolesAsync(user.Id, [role.Id]);
        }

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/Auth/login", new LoginRequestDto { Account = user.Account, Password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var tokens = (await login.Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())!.Data!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
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

    private async Task<Category> AddCategoryAsync(string name)
    {
        await using var context = await CreateContextAsync();
        var category = new Category { Name = name, IsEnabled = true, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        context.Category.Add(category);
        await context.SaveChangesAsync();
        return category;
    }

    /// <summary>模擬別的使用者在這之間停用（或刪除）了它。</summary>
    private async Task RetireTeamAsync(int id, bool delete)
    {
        await using var context = await CreateContextAsync();
        var team = await context.Team.SingleAsync(x => x.Id == id);
        team.IsEnabled = delete;
        team.IsDeleted = delete;
        await context.SaveChangesAsync();
        factory.Services.GetRequiredService<ITeamTreeCache>().Invalidate();
    }

    private async Task RetireCategoryAsync(int id, bool delete)
    {
        await using var context = await CreateContextAsync();
        var category = await context.Category.SingleAsync(x => x.Id == id);
        category.IsEnabled = delete;
        category.IsDeleted = delete;
        await context.SaveChangesAsync();
    }

    private Task<BackendDBContext> CreateContextAsync()
        => factory.Services.GetRequiredService<IDbContextFactory<BackendDBContext>>().CreateDbContextAsync();
}
