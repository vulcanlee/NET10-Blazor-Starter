using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Models;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Client.Api;

namespace MyProject.Tests;

/// <summary>
/// 專案的 Web API（0.10.3 起，WASM 遷移 M4）：改走與畫面相同的 <c>ProjectService</c>；同一個網址收 JSON（不帶附件）與
/// multipart（帶附件），附件下載改用一次性下載票證（<c>POST files/{id}/ticket</c> → <c>GET api/v1/downloads/{ticket}</c>）。
/// 稽核由服務寫，每個動作只記一筆；範圍外的一律當作不存在（404）。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class ProjectApiTests : IClassFixture<ApiTestApplicationFactoryWithBrowsingAudit>
{
    private const string Password = "Passw0rd-1";
    private const string BaseUrl = "/api/v1/Project";
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private readonly ApiTestApplicationFactoryWithBrowsingAudit factory;

    public ProjectApiTests(ApiTestApplicationFactoryWithBrowsingAudit factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task FullLifecycle_WithAttachments_ShouldWork_AndWriteEachAuditOnce()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var title = Unique("附件專案");

        // 帶附件新增（multipart）。
        var created = await SendFormAsync<ProjectDto>(client, HttpMethod.Post, BaseUrl, NewDto(title),
            [("規格.txt", "第一份內容"u8.ToArray()), ("報價.pdf", Encoding.UTF8.GetBytes("%PDF-1.4 假的"))]);
        var id = created.Id!.Value;

        var fetched = await SendAsync<ProjectDto>(client, HttpMethod.Get, $"{BaseUrl}/{id}");
        Assert.Equal(2, fetched.Files.Count);
        var spec = Assert.Single(fetched.Files, x => x.OriginalFileName == "規格.txt");
        Assert.Equal(Encoding.UTF8.GetByteCount("第一份內容"), spec.FileSize);
        var specPath = await PhysicalPathAsync(spec.Id);
        Assert.True(File.Exists(specPath));

        // 下載內容與上傳的一致。
        Assert.Equal("第一份內容", Encoding.UTF8.GetString(await DownloadAsync(client, spec.Id)));

        // 帶附件修改：刪掉規格、加一份新的。
        fetched.Description = "改過";
        await SendFormAsync<object>(client, HttpMethod.Put, $"{BaseUrl}/{id}", fetched,
            [("會議.txt", "第二份"u8.ToArray())], removedFileIds: [spec.Id]);
        Assert.False(File.Exists(specPath));
        var afterUpdate = await SendAsync<ProjectDto>(client, HttpMethod.Get, $"{BaseUrl}/{id}");
        Assert.Equal(["報價.pdf", "會議.txt"], afterUpdate.Files.Select(x => x.OriginalFileName).Order(StringComparer.Ordinal));
        var remainingPaths = new List<string>();
        foreach (var file in afterUpdate.Files)
        {
            remainingPaths.Add(await PhysicalPathAsync(file.Id));
        }

        await SendAsync<object>(client, HttpMethod.Delete, $"{BaseUrl}/{id}");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{BaseUrl}/{id}")).StatusCode);

        var deleted = await SearchAsync(client, new ProjectSearchRequestDto { Keyword = title, Deleted = true });
        Assert.True(Assert.Single(deleted.Items).IsDeleted);

        await SendAsync<object>(client, HttpMethod.Post, $"{BaseUrl}/{id}/restore");
        await SendAsync<object>(client, HttpMethod.Delete, $"{BaseUrl}/{id}");
        await SendAsync<object>(client, HttpMethod.Delete, $"{BaseUrl}/{id}/purge");
        Assert.All(remainingPaths, path => Assert.False(File.Exists(path)));

        Assert.Equal(
            [
                AuditActions.Project.Create, AuditActions.Project.FileUpload, AuditActions.Project.View,
                AuditActions.Project.Update, AuditActions.Project.FileUpload, AuditActions.Project.FileDelete, AuditActions.Project.View,
                AuditActions.Project.Delete, AuditActions.Project.Restore, AuditActions.Project.Delete, AuditActions.Project.Purge,
            ],
            await AuditActionsForAsync("Project", id.ToString(), admin.Id));
        Assert.Equal([AuditActions.Project.FileDownload], await AuditActionsForAsync("ProjectFile", spec.Id.ToString(), admin.Id));
    }

    [Fact]
    public async Task JsonCreate_ShouldStillWork_AndReturnTheNewIdAndStamp()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));

        var created = await SendAsync<ProjectDto>(client, HttpMethod.Post, BaseUrl, NewDto(Unique("JSON")));

        Assert.True(created.Id > 0);
        Assert.False(string.IsNullOrWhiteSpace(created.ConcurrencyStamp));
        Assert.Empty(created.Files);
    }

    [Fact]
    public async Task DuplicateTitle_ShouldReturn409_IgnoringCase_ButAnUnchangedLegacyDuplicateCanStillBeSaved()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var title = Unique("Dup");
        await SendAsync<ProjectDto>(client, HttpMethod.Post, BaseUrl, NewDto(title));

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(BaseUrl, NewDto(title.ToLowerInvariant()))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostFormAsync(client, HttpMethod.Post, BaseUrl, NewDto($" {title.ToUpperInvariant()} "), [])).StatusCode);

        // 0.10.3 之前網頁允許重名：舊的重名資料名稱沒改時照常可存。
        int legacyId;
        await using (var context = await CreateContextAsync())
        {
            var legacy = new Project { Title = title, Status = "進行中", Priority = "中", Owner = "舊資料", ConcurrencyStamp = ConcurrencyStampHelper.New() };
            context.Project.Add(legacy);
            await context.SaveChangesAsync();
            legacyId = legacy.Id;
        }

        var opened = await SendAsync<ProjectDto>(client, HttpMethod.Get, $"{BaseUrl}/{legacyId}");
        opened.Description = "只改描述";
        await SendAsync<object>(client, HttpMethod.Put, $"{BaseUrl}/{legacyId}", opened);

        // 改成別的專案已用的名稱就擋。
        var other = await SendAsync<ProjectDto>(client, HttpMethod.Post, BaseUrl, NewDto(Unique("另一個")));
        var renamed = await SendAsync<ProjectDto>(client, HttpMethod.Get, $"{BaseUrl}/{other.Id}");
        renamed.Title = title;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"{BaseUrl}/{other.Id}", renamed)).StatusCode);
    }

    [Fact]
    public async Task RulesFromTheScreen_ShouldReturn400()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));

        var badStatus = NewDto(Unique("狀態"));
        badStatus.Status = "Active";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(BaseUrl, badStatus)).StatusCode);

        var badDates = NewDto(Unique("日期"));
        badDates.StartDate = DateTime.Today;
        badDates.EndDate = DateTime.Today.AddDays(-1);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(BaseUrl, badDates)).StatusCode);

        // 日期可以不填（與畫面相同）。
        var noDates = NewDto(Unique("無日期"));
        noDates.StartDate = null;
        noDates.EndDate = null;
        var created = await SendAsync<ProjectDto>(client, HttpMethod.Post, BaseUrl, noDates);
        Assert.Null(created.StartDate);
    }

    [Fact]
    public async Task Upload_WithADisallowedExtension_ShouldReturn400_AndCreateNothing()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var title = Unique("壞附件");

        var response = await PostFormAsync(client, HttpMethod.Post, BaseUrl, NewDto(title), [("攻擊.html", "<script>alert(1)</script>"u8.ToArray())]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("攻擊.html", (await ReadAsync<object>(response)).UserMessage());
        Assert.Empty((await SearchAsync(client, new ProjectSearchRequestDto { Keyword = title })).Items);
    }

    [Fact]
    public async Task Update_WithAStaleStamp_ShouldReturn409_AndSaveNoAttachment()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var created = await SendAsync<ProjectDto>(client, HttpMethod.Post, BaseUrl, NewDto(Unique("並行")));
        var first = await SendAsync<ProjectDto>(client, HttpMethod.Get, $"{BaseUrl}/{created.Id}");
        var second = await SendAsync<ProjectDto>(client, HttpMethod.Get, $"{BaseUrl}/{created.Id}");

        first.Description = "先存";
        await SendAsync<object>(client, HttpMethod.Put, $"{BaseUrl}/{created.Id}", first);
        second.Description = "後存";
        var stale = await PostFormAsync(client, HttpMethod.Put, $"{BaseUrl}/{created.Id}", second, [("晚到.txt", "x"u8.ToArray())]);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var current = await SendAsync<ProjectDto>(client, HttpMethod.Get, $"{BaseUrl}/{created.Id}");
        Assert.Equal("先存", current.Description);
        Assert.Empty(current.Files);
    }

    [Fact]
    public async Task Search_ShouldMatchTheScreen_AndKeepTheExistingApiFilters()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var owner = Unique("負責人");
        var a = NewDto(Unique("甲"));
        a.Owner = owner;
        a.Status = "已完成";
        a.Categories = "\n搜尋分類\n";
        var b = NewDto(Unique("乙"));
        b.Owner = owner;
        b.Status = "進行中";
        await SendAsync<ProjectDto>(client, HttpMethod.Post, BaseUrl, a);
        await SendAsync<ProjectDto>(client, HttpMethod.Post, BaseUrl, b);

        // 關鍵字與畫面相同：也比對負責人。
        Assert.Equal(2, (await SearchAsync(client, new ProjectSearchRequestDto { Keyword = owner })).TotalCount);
        Assert.Equal(a.Title, Assert.Single((await SearchAsync(client, new ProjectSearchRequestDto { Keyword = owner, Status = "已完成" })).Items).Title);
        Assert.Equal(a.Title, Assert.Single((await SearchAsync(client, new ProjectSearchRequestDto { Keyword = owner, Categories = ["搜尋分類"] })).Items).Title);
        Assert.Equal(2, (await SearchAsync(client, new ProjectSearchRequestDto { Owner = owner })).TotalCount);
        Assert.Equal(a.Title, (await SearchAsync(client, new ProjectSearchRequestDto { Owner = owner, SortBy = "STATUS", SortDescending = false })).Items[0].Title);
        Assert.Equal(b.Title, (await SearchAsync(client, new ProjectSearchRequestDto { Owner = owner, SortBy = "status", SortDescending = true })).Items[0].Title);
    }

    [Fact]
    public async Task Export_ShouldReturnAnXlsx_WithTheSameRowsAsTheList_AndAudit()
    {
        var admin = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(admin);
        var owner = Unique("匯出人");
        var dto = NewDto(Unique("匯出"));
        dto.Owner = owner;
        await SendAsync<ProjectDto>(client, HttpMethod.Post, BaseUrl, dto);

        var response = await client.PostAsJsonAsync($"{BaseUrl}/export", new ProjectSearchRequestDto { Keyword = owner });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(XlsxContentType, response.Content.Headers.ContentType?.MediaType);
        using var workbook = new XLWorkbook(await response.Content.ReadAsStreamAsync());
        Assert.Equal(2, workbook.Worksheet(1).RowsUsed().Count());
        Assert.Equal(1, await CountAuditAsync(AuditActions.Project.Export, admin.Id));
    }

    [Fact]
    public async Task ViewOnlyRole_ShouldReadAndDownload_ButBeForbiddenFromEveryWrite()
    {
        using var adminClient = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var created = await SendFormAsync<ProjectDto>(adminClient, HttpMethod.Post, BaseUrl, NewDto(Unique("唯讀")), [("說明.txt", "內容"u8.ToArray())]);
        var file = (await SendAsync<ProjectDto>(adminClient, HttpMethod.Get, $"{BaseUrl}/{created.Id}")).Files[0];

        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: false, permissions: [PermissionKey.For(MagicObjectHelper.角色_專案項目, PermissionActions.View)]));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{BaseUrl}/{created.Id}")).StatusCode);
        Assert.Equal("內容", Encoding.UTF8.GetString(await DownloadAsync(client, file.Id)));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(BaseUrl, NewDto(Unique("越權")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostFormAsync(client, HttpMethod.Post, BaseUrl, NewDto(Unique("越權")), [])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostFormAsync(client, HttpMethod.Put, $"{BaseUrl}/{created.Id}", created, [])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"{BaseUrl}/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{BaseUrl}/search", new ProjectSearchRequestDto { Deleted = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{BaseUrl}/export", new ProjectSearchRequestDto { Deleted = true })).StatusCode);
    }

    [Fact]
    public async Task TeamScope_ShouldHideOtherTeamsProjects_AndTheirAttachments()
    {
        var teamA = await AddTeamAsync(Unique("甲隊"));
        var teamB = await AddTeamAsync(Unique("乙隊"));
        var allActions = new[] { PermissionActions.View, PermissionActions.Create, PermissionActions.Edit, PermissionActions.Delete }
            .Select(x => PermissionKey.For(MagicObjectHelper.角色_專案項目, x)).ToArray();
        using var memberOfA = await SignedInClientAsync(await AddUserAsync(isAdmin: false, permissions: allActions, teams: [teamA.Id]));
        using var memberOfB = await SignedInClientAsync(await AddUserAsync(isAdmin: false, permissions: allActions, teams: [teamB.Id]));

        var dto = NewDto(Unique("甲隊專案"));
        dto.Teams = $"\n{teamA.Name}\n";
        var created = await SendFormAsync<ProjectDto>(memberOfA, HttpMethod.Post, BaseUrl, dto, [("機密.txt", "甲隊的"u8.ToArray())]);
        var file = (await SendAsync<ProjectDto>(memberOfA, HttpMethod.Get, $"{BaseUrl}/{created.Id}")).Files[0];

        Assert.Empty((await SearchAsync(memberOfB, new ProjectSearchRequestDto { Keyword = dto.Title })).Items);
        Assert.Equal(HttpStatusCode.NotFound, (await memberOfB.GetAsync($"{BaseUrl}/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await memberOfB.PostAsync($"{BaseUrl}/files/{file.Id}/ticket", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await memberOfB.DeleteAsync($"{BaseUrl}/{created.Id}")).StatusCode);

        // 範圍外的修改一律 404：就算名稱與別人重複，也不會回 409 透露它存在。
        var stolen = NewDto(dto.Title);
        stolen.Id = created.Id;
        stolen.ConcurrencyStamp = created.ConcurrencyStamp;
        Assert.Equal(HttpStatusCode.NotFound, (await memberOfB.PutAsJsonAsync($"{BaseUrl}/{created.Id}", stolen)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostFormAsync(memberOfB, HttpMethod.Put, $"{BaseUrl}/{created.Id}", stolen, [])).StatusCode);
    }

    [Fact]
    public async Task DownloadTicket_ShouldWorkOnlyOnce()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var fileId = await CreateProjectWithFileAsync(client);

        var url = await TicketUrlAsync(client, fileId);
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var first = await anonymous.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("attachment", first.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/v1/downloads/not-a-ticket")).StatusCode);
    }

    [Fact]
    public async Task DownloadTicket_AfterTheUserIsDisabled_ShouldReturn404()
    {
        var user = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(user);
        var fileId = await CreateProjectWithFileAsync(client);
        var url = await TicketUrlAsync(client, fileId);

        await using (var context = await CreateContextAsync())
        {
            var stored = await context.MyUser.FirstAsync(x => x.Id == user.Id);
            stored.Status = false;
            await context.SaveChangesAsync();
        }

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task DownloadTicket_AfterTheSessionIsRevoked_ShouldReturn404()
    {
        var user = await AddUserAsync(isAdmin: true);
        using var client = await SignedInClientAsync(user);
        var fileId = await CreateProjectWithFileAsync(client);
        var url = await TicketUrlAsync(client, fileId);

        // 改密碼、強制登出等都會換工作階段版本（SecurityStamp）。
        await using (var context = await CreateContextAsync())
        {
            var stored = await context.MyUser.FirstAsync(x => x.Id == user.Id);
            stored.SecurityStamp = SecurityStamps.New();
            await context.SaveChangesAsync();
        }

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task DownloadTicket_AfterTheProjectIsDeleted_ShouldReturn404()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var title = Unique("刪後下載");
        var created = await SendFormAsync<ProjectDto>(client, HttpMethod.Post, BaseUrl, NewDto(title), [("a.txt", "a"u8.ToArray())]);
        var fileId = (await SendAsync<ProjectDto>(client, HttpMethod.Get, $"{BaseUrl}/{created.Id}")).Files[0].Id;
        var url = await TicketUrlAsync(client, fileId);

        await SendAsync<object>(client, HttpMethod.Delete, $"{BaseUrl}/{created.Id}");

        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"{BaseUrl}/files/{fileId}/ticket", null)).StatusCode);
    }

    [Fact]
    public async Task TheOldCookieDownloadEndpoint_ShouldBeGone()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/project-files/1/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/project-files/1/download")).StatusCode);
    }

    [Fact]
    public async Task Options_ShouldListTheFormChoices()
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));

        var options = await SendAsync<ProjectFormOptionsDto>(client, HttpMethod.Get, $"{BaseUrl}/options");

        Assert.True(options.IsAdmin);
        Assert.Equal(1024L * 1024L * 1024L, options.MaxUploadFileSize);
        Assert.Contains(".pdf", options.AllowedExtensions);
        Assert.DoesNotContain(".html", options.AllowedExtensions);
    }

    /// <summary>審查修正：同一個網址有 JSON 與 multipart 兩個 action，Swagger 文件仍要產得出來（開發環境的 /swagger）。</summary>
    [Fact]
    public void SwaggerDocument_ShouldStillGenerate()
    {
        var swagger = factory.Services.GetRequiredService<Swashbuckle.AspNetCore.Swagger.ISwaggerProvider>().GetSwagger("v1");

        Assert.Contains(swagger.Paths, x => string.Equals(x.Key, "/api/v1/Project", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>審查修正：沒帶 Content-Type 的新增，與改寫前一樣是 401（沒登入）／415（有登入），不是路由模稜兩可的 500。</summary>
    [Fact]
    public async Task Create_WithoutAContentType_ShouldBehaveAsBefore()
    {
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(BaseUrl, null)).StatusCode);

        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PostAsync(BaseUrl, null)).StatusCode);
    }

    /// <summary>審查修正：改寫前 [FromBody] 也接受 text/json 與 application/*+json，狀態碼不能因為加了 [Consumes] 而變 415。</summary>
    [Theory]
    [InlineData("text/json")]
    [InlineData("application/problem+json")]
    public async Task Create_WithOtherJsonContentTypes_ShouldStillWork(string mediaType)
    {
        using var client = await SignedInClientAsync(await AddUserAsync(isAdmin: true));
        var body = new StringContent(JsonSerializer.Serialize(NewDto(Unique("JSON類型")), new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, mediaType);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(BaseUrl, body)).StatusCode);
    }

    // ───────────────────────────── 工具 ─────────────────────────────

    private static ProjectCreateUpdateDto NewDto(string title) => new()
    {
        Id = 0,
        Title = title,
        Description = "整合測試",
        StartDate = DateTime.Today,
        EndDate = DateTime.Today.AddDays(7),
        Status = "進行中",
        Priority = "中",
        CompletionPercentage = 10,
        Owner = "整合測試",
    };

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..(prefix.Length + 10)];

    private async Task<int> CreateProjectWithFileAsync(HttpClient client)
    {
        var created = await SendFormAsync<ProjectDto>(client, HttpMethod.Post, BaseUrl, NewDto(Unique("票證")), [("票證.txt", "票證內容"u8.ToArray())]);
        return (await SendAsync<ProjectDto>(client, HttpMethod.Get, $"{BaseUrl}/{created.Id}")).Files[0].Id;
    }

    private static async Task<string> TicketUrlAsync(HttpClient client, int fileId)
    {
        var ticket = await SendAsync<DownloadTicketDto>(client, HttpMethod.Post, $"{BaseUrl}/files/{fileId}/ticket");
        Assert.StartsWith("/api/v1/downloads/", ticket.Url);
        return ticket.Url;
    }

    /// <summary>申請票證後以不帶任何登入的用戶端下載（模擬瀏覽器的原生下載）。</summary>
    private async Task<byte[]> DownloadAsync(HttpClient client, int fileId)
    {
        var url = await TicketUrlAsync(client, fileId);
        using var anonymous = factory.CreateClient();
        var response = await anonymous.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, HttpMethod method, string url, ProjectCreateUpdateDto data, (string Name, byte[] Content)[] files, int[]? removedFileIds = null)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8), "data" },
        };
        foreach (var (name, content) in files)
        {
            var part = new ByteArrayContent(content);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, "files", name);
        }

        foreach (var removed in removedFileIds ?? [])
        {
            form.Add(new StringContent(removed.ToString()), "removedFileIds");
        }

        using var request = new HttpRequestMessage(method, url) { Content = form };
        return await client.SendAsync(request);
    }

    private static async Task<T> SendFormAsync<T>(
        HttpClient client, HttpMethod method, string url, ProjectCreateUpdateDto data, (string Name, byte[] Content)[] files, int[]? removedFileIds = null)
    {
        var response = await PostFormAsync(client, method, url, data, files, removedFileIds);
        var result = await ReadAsync<T>(response);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{method} {url} (multipart) → {(int)response.StatusCode} {result.UserMessage()}");
        return result.Data!;
    }

    private static async Task<T> SendAsync<T>(HttpClient client, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        var response = await client.SendAsync(request);
        var result = await ReadAsync<T>(response);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{method} {url} → {(int)response.StatusCode} {result.UserMessage()}");
        return result.Data!;
    }

    private static async Task<PagedResult<ProjectDto>> SearchAsync(HttpClient client, ProjectSearchRequestDto request)
        => await SendAsync<PagedResult<ProjectDto>>(client, HttpMethod.Post, $"{BaseUrl}/search", request);

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

        var account = Unique("m4-");
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

    private async Task<string> PhysicalPathAsync(int fileId)
    {
        await using var context = await CreateContextAsync();
        var relative = await context.ProjectFile.Where(x => x.Id == fileId).Select(x => x.RelativePath).SingleAsync();
        var root = factory.Services.GetRequiredService<IOptions<SystemSettings>>().Value.ExternalFileSystem.ProjectFilePath;
        return Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
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
