using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Models.Systems;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Models;
using MyProject.Web;
using MyProject.Web.Configuration;
using MyProject.Web.Controllers;
using MyProject.Business.Services.DataAccess;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MyProject.Tests;

public sealed class ApiIntegrationTests : IClassFixture<ApiTestApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ApiTestApplicationFactory factory;

    public ApiIntegrationTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task ProtectedCrudApi_WithoutBearerToken_ShouldReturnApiResult401()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/Project/1");
        var result = await ReadApiResultAsync<object>(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(result.Success);
        Assert.Equal(401, result.StatusCode);
        Assert.NotNull(result.TraceId);
    }

    [Fact]
    public async Task ProtectedCrudApi_WithoutRequiredPermission_ShouldReturnApiResult403()
    {
        var account = $"limited-{Guid.NewGuid():N}";
        const string password = "limited-pass";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var role = new RoleView
            {
                Name = $"受限角色-{Guid.NewGuid():N}",
                TabViewJson = JsonSerializer.Serialize(new[] { "首頁" }),
            };
            db.RoleView.Add(role);
            await db.SaveChangesAsync();

            db.MyUser.Add(new MyUser
            {
                Account = account,
                Name = "limited",
                Status = true,
                IsAdmin = false,
                RoleViewId = role.Id,
                Password = SecurePasswordHasher.HashPassword(password),
            });
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto
        {
            Account = account,
            Password = password,
        });
        var loginResult = await ReadApiResultAsync<TokenResponseDto>(login);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", loginResult.Data!.AccessToken);

        var response = await client.GetAsync("/api/Project/1");
        var result = await ReadApiResultAsync<object>(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(result.Success);
        Assert.Equal(403, result.StatusCode);
    }

    [Fact]
    public async Task ViewOnlyRole_CanRead_ButCannotCreate()
    {
        var account = $"viewer-{Guid.NewGuid():N}";
        const string password = "viewer-pass";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
            var writer = scope.ServiceProvider.GetRequiredService<MyProject.Business.Services.Other.IRbacWriteService>();

            var role = new RoleView
            {
                Name = $"唯讀-{Guid.NewGuid():N}",
                TabViewJson = JsonSerializer.Serialize(new[] { "專案項目:view" }),
            };
            db.RoleView.Add(role);
            await db.SaveChangesAsync();
            await writer.SyncRolePermissionsAsync(role.Id, new[] { "專案項目:view" });

            var user = new MyUser
            {
                Account = account,
                Name = "viewer",
                Status = true,
                IsAdmin = false,
                RoleViewId = role.Id,
                Password = SecurePasswordHasher.HashPassword(password),
            };
            db.MyUser.Add(user);
            await db.SaveChangesAsync();
            await writer.SyncUserRolesAsync(user.Id, new[] { role.Id });
        }

        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto { Account = account, Password = password });
        var loginResult = await ReadApiResultAsync<TokenResponseDto>(login);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResult.Data!.AccessToken);

        // 有 view：讀取被允許（不存在的 id → 404，而非 403）
        var readResponse = await client.GetAsync("/api/Project/999999");
        Assert.Equal(HttpStatusCode.NotFound, readResponse.StatusCode);

        // 無 create：新增被拒（403）
        var createResponse = await client.PostAsJsonAsync("/api/Project", new ProjectCreateUpdateDto
        {
            Id = 0,
            Title = "should be forbidden",
            Status = "進行中",
            Priority = "中",
            Owner = "viewer",
        });
        Assert.Equal(HttpStatusCode.Forbidden, createResponse.StatusCode);
    }

    [Fact]
    public async Task AuthEndpoints_LoginRefreshAndMe_ShouldReturnApiResult()
    {
        using var client = factory.CreateClient();

        var loginResult = await LoginAsync(client);
        Assert.False(string.IsNullOrWhiteSpace(loginResult.Data?.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(loginResult.Data?.RefreshToken));

        var refreshResponse = await client.PostAsJsonAsync("/api/Auth/refresh", new RefreshTokenRequestDto
        {
            RefreshToken = loginResult.Data!.RefreshToken
        });
        var refreshResult = await ReadApiResultAsync<TokenResponseDto>(refreshResponse);

        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
        Assert.True(refreshResult.Success);
        Assert.False(string.IsNullOrWhiteSpace(refreshResult.Data?.AccessToken));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResult.Data.AccessToken);
        var meResponse = await client.GetAsync("/api/Auth/me");
        var meResult = await ReadApiResultAsync<CurrentUserDto>(meResponse);

        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);
        Assert.True(meResult.Success);
        Assert.Equal("support", meResult.Data?.Account);
    }

    [Fact]
    public async Task VersionedAuthEndpoints_ShouldKeepApiResultContract()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/Auth/login", new LoginRequestDto
        {
            Account = "support",
            Password = "support"
        });
        var result = await ReadApiResultAsync<TokenResponseDto>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Data?.AccessToken));
    }

    [Fact]
    public async Task ProjectCreate_InvalidPayload_ShouldReturnApiResult400()
    {
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);

        var response = await client.PostAsJsonAsync("/api/Project", new { });
        var result = await ReadApiResultAsync<ProjectDto>(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(result.Success);
        Assert.Equal(400, result.StatusCode);
        Assert.NotNull(result.Errors);
    }

    [Fact]
    public async Task ProjectCrud_WithBearerToken_ShouldUseApiResultAndDto()
    {
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);

        var createDto = new ProjectCreateUpdateDto
        {
            Id = 1,
            Title = $"Integration Project {Guid.NewGuid():N}",
            Description = "Integration test project",
            StartDate = DateTime.Today,
            EndDate = DateTime.Today.AddDays(7),
            Status = "進行中",
            Priority = "中",
            CompletionPercentage = 10,
            Owner = "integration-test"
        };

        var createResponse = await client.PostAsJsonAsync("/api/Project", createDto);
        var createResult = await ReadApiResultAsync<ProjectDto>(createResponse);

        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        Assert.True(createResult.Success);
        Assert.NotNull(createResult.Data);
        Assert.True(createResult.Data!.Id > 0);
        Assert.Equal(createDto.Title, createResult.Data.Title);

        var getResponse = await client.GetAsync($"/api/Project/{createResult.Data.Id}");
        var getResult = await ReadApiResultAsync<ProjectDto>(getResponse);

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.True(getResult.Success);
        Assert.Equal(createDto.Title, getResult.Data?.Title);
    }

    [Fact]
    public async Task ForbiddenApi_ShouldReturnApiResult403()
    {
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);

        var response = await client.GetAsync("/api/ContractProbe/forbidden");
        var result = await ReadApiResultAsync<object>(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(result.Success);
        Assert.Equal(403, result.StatusCode);
        Assert.NotNull(result.TraceId);
    }

    [Fact]
    public async Task UnhandledApiException_ShouldReturnApiResult500()
    {
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);

        var response = await client.GetAsync("/api/ContractProbe/throw");
        var result = await ReadApiResultAsync<object>(response);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(result.Success);
        Assert.Equal(500, result.StatusCode);
        Assert.NotNull(result.Exception);
        Assert.Equal(typeof(InvalidOperationException).FullName, result.Exception.Type);
        Assert.NotNull(result.TraceId);
    }

    [Fact]
    public async Task HealthReadiness_ShouldReturnHealthy()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthLiveness_ShouldReturnHealthy()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SystemHealthPage_WithoutCookieLogin_ShouldNotExposeDetails()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/system-health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.OK
            || response.StatusCode == HttpStatusCode.Redirect
            || response.StatusCode == HttpStatusCode.Unauthorized);
        Assert.DoesNotContain("最後 100 筆日誌紀錄", body);
    }

    [Fact]
    public void ProductionSafetyValidation_WithDevelopmentDefaults_ShouldFailFast()
    {
        var settings = new Dictionary<string, string?>
        {
            ["JwtSettings:SigningKey"] = "DevelopmentOnly-ChangeThisJwtSigningKey-AtLeast32Chars",
            ["BootstrapSettings:SupportAccount"] = "support",
            ["BootstrapSettings:SupportPassword"] = "support",
            ["Swagger:EnabledInProduction"] = ""
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StartupSafetyValidator.Validate(configuration, "Production"));

        Assert.Contains("JwtSettings:SigningKey", exception.Message);
        Assert.Contains("BootstrapSettings:SupportPassword", exception.Message);
        Assert.Contains("Swagger:EnabledInProduction", exception.Message);
    }

    [Fact]
    public async Task ProjectFileDownloadEndpoint_ShouldBeRegistered()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/api/project-files/1/download");

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// .NET 範本殘留的 WeatherForecastController 已於 0.4.34 移除。
    /// 它沒有任何授權標註、路由不在 /api 之下（連 ApiExceptionFilter 都不涵蓋），
    /// 卻會被 New-StarterProject.ps1 複製到每一個客戶專案。
    /// </summary>
    [Fact]
    public async Task WeatherForecastEndpoint_ShouldNotExist()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/WeatherForecast");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 0)]
    [InlineData(1, int.MaxValue)]
    public async Task Search_WithOutOfRangePaging_ShouldReturn400(int pageIndex, int pageSize)
    {
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);

        var response = await client.PostAsJsonAsync("/api/Project/search", new
        {
            PageIndex = pageIndex,
            PageSize = pageSize
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Controller 自己 catch 後回傳的 500，也必須遵守 Security:ReturnExceptionDetails。
    ///
    /// 0.4.34 之前只有 ApiExceptionFilter 會判斷這個開關；Controller catch 區塊呼叫的
    /// ApiServerError 直接把 exception.ToString() 與堆疊追蹤塞進回應，
    /// 導致 Production 設了 false 仍會外洩。
    /// </summary>
    [Fact]
    public async Task CaughtApiException_WithDetailsEnabled_ShouldIncludeExceptionInfo()
    {
        using var client = factory.CreateClient();
        await AuthorizeAsync(client);

        var response = await client.GetAsync("/api/ContractProbe/caught");
        var result = await ReadApiResultAsync<object>(response);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(result.Success);
        Assert.NotNull(result.Exception);
        Assert.NotNull(result.ErrorDetail);
    }

    /// <summary>
    /// nosniff 對本專案特別重要：附件下載會回吐儲存時記下的 ContentType，
    /// 少了它，瀏覽器可能把附件當成 HTML 解析而造成同源 XSS。
    /// </summary>
    [Fact]
    public async Task Responses_ShouldIncludeSecurityHeaders()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/health/live");

        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal(
            "strict-origin-when-cross-origin",
            Assert.Single(response.Headers.GetValues("Referrer-Policy")));
    }

    /// <summary>
    /// /UploadFiles 是把 DownloadPath 直接掛出去的靜態檔案端點。
    /// 0.4.35 之前它註冊在 UseAuthentication/UseAuthorization 之前，且 UseStaticFiles
    /// 本身不看授權，等於整個目錄匿名可讀。
    /// </summary>
    [Fact]
    public async Task UploadFilesStaticPath_WithoutLogin_ShouldNotBeAnonymouslyReadable()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/UploadFiles/anything.txt");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// 從**真實 DI 容器**解析 Blazor 路徑的資料服務。
    ///
    /// 這些服務只被 Razor 元件使用，整合測試的 HTTP 端點碰不到它們，
    /// 因此 0.4.36 把它們改注入 IDbContextFactory 之後，若 DI 沒接好
    /// （例如漏了 AddDbContextFactory），單靠既有測試不會發現 —— 要到執行期開頁面才炸。
    /// </summary>
    [Theory]
    [InlineData(typeof(CategoryService))]
    [InlineData(typeof(TeamService))]
    [InlineData(typeof(RoleViewService))]
    [InlineData(typeof(ProjectService))]
    [InlineData(typeof(MyUserService))]
    public void DataAccessServices_ShouldResolveFromContainer(Type serviceType)
    {
        using var scope = factory.Services.CreateScope();

        var service = scope.ServiceProvider.GetRequiredService(serviceType);

        Assert.NotNull(service);
    }

    [Fact]
    public void DbContextFactory_ShouldResolveAndCreateContext()
    {
        using var scope = factory.Services.CreateScope();

        var contextFactory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<BackendDBContext>>();
        using var context = contextFactory.CreateDbContext();

        Assert.NotNull(context);
    }

    private static async Task AuthorizeAsync(HttpClient client)
    {
        var loginResult = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResult.Data!.AccessToken);
    }

    private static async Task<ApiResult<TokenResponseDto>> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto
        {
            Account = "support",
            Password = "support"
        });

        var result = await ReadApiResultAsync<TokenResponseDto>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        return result;
    }

    private static async Task<ApiResult<T>> ReadApiResultAsync<T>(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResult<T>>(json, JsonOptions);
        Assert.NotNull(result);
        return result!;
    }
}

public class ApiTestApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string rootPath = Path.Combine(
        Path.GetTempPath(),
        "MyProjectIntegrationTests",
        Guid.NewGuid().ToString("N"));

    private readonly Dictionary<string, string> environmentVariables;

    public ApiTestApplicationFactory()
    {
        environmentVariables = CreateSettings()
            .ToDictionary(
                x => x.Key.Replace(":", "__", StringComparison.Ordinal),
                x => x.Value ?? string.Empty);

        foreach (var item in environmentVariables)
        {
            Environment.SetEnvironmentVariable(item.Key, item.Value);
        }
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(CreateSettings());
        });
        builder.ConfigureServices(services =>
        {
            services.AddAuthorization(options =>
            {
                options.AddPolicy("IntegrationForbidden", policy =>
                    policy.RequireClaim("integration_forbidden", "true"));
            });

            services
                .AddControllers()
                .PartManager
                .ApplicationParts
                .Add(new AssemblyPart(typeof(ContractProbeController).Assembly));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        foreach (var item in environmentVariables)
        {
            Environment.SetEnvironmentVariable(item.Key, null);
        }

        if (disposing && Directory.Exists(rootPath))
        {
            try
            {
                Directory.Delete(rootPath, recursive: true);
            }
            catch (IOException)
            {
                // SQLite may release file handles shortly after the test host stops.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup keeps integration test assertions independent from OS file timing.
            }
        }
    }

    private Dictionary<string, string?> CreateSettings()
    {
        return new Dictionary<string, string?>
        {
            ["NLog:BasePath"] = Path.Combine(rootPath, "Logs"),
            ["Security:ReturnExceptionDetails"] = "true",

            // 測試會反覆登入，不應受正式配額牽制（正式預設為 120 / 10）。
            // 限流本身的行為另由單元測試驗證分割鍵，不靠整合測試踩配額。
            ["RateLimit:ApiRequestsPerMinute"] = "100000",
            ["RateLimit:LoginRequestsPerMinute"] = "100000",
            ["JwtSettings:Issuer"] = "MyProject.Tests",
            ["JwtSettings:Audience"] = "MyProject.Tests.Api",
            ["JwtSettings:SigningKey"] = "IntegrationTests-ChangeThisJwtSigningKey-AtLeast32Chars",
            ["JwtSettings:AccessTokenMinutes"] = "30",
            ["JwtSettings:RefreshTokenDays"] = "7",
            ["JwtSettings:ClockSkewMinutes"] = "0",
            ["BootstrapSettings:SupportAccount"] = "support",
            ["BootstrapSettings:SupportName"] = "support",
            ["BootstrapSettings:SupportEmail"] = "support",
            ["BootstrapSettings:SupportPassword"] = "support",
            ["SystemSettings:ExternalFileSystem:DatabasePath"] = Path.Combine(rootPath, "DB"),
            ["SystemSettings:ExternalFileSystem:DownloadPath"] = Path.Combine(rootPath, "Download"),
            ["SystemSettings:ExternalFileSystem:UploadPath"] = Path.Combine(rootPath, "Upload"),
            ["SystemSettings:ExternalFileSystem:ProjectFilePath"] = Path.Combine(rootPath, "ProjectFile")
        };
    }
}

/// <summary>
/// 與 <see cref="ApiTestApplicationFactory"/> 相同，但把
/// <c>Security:ReturnExceptionDetails</c> 關掉，用來驗證 Production 組態下不會外洩堆疊。
/// </summary>
public sealed class ApiTestApplicationFactoryWithoutExceptionDetails : ApiTestApplicationFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // 註冊順序在 base 之後，因此覆寫得掉 base 加進去的設定。
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ReturnExceptionDetails"] = "false"
            });
        });
    }
}

/// <summary>
/// 迴歸測試：Production 組態（ReturnExceptionDetails=false）下，
/// **兩條 500 路徑**都不得回傳例外訊息或堆疊追蹤。
///
/// 路徑一：未攔截的例外 → ApiExceptionFilter（原本就有判斷）
/// 路徑二：Controller 自行 catch → ApiServerError（0.4.34 之前完全繞過判斷）
/// </summary>
public sealed class ApiExceptionDetailSuppressionTests
    : IClassFixture<ApiTestApplicationFactoryWithoutExceptionDetails>
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly ApiTestApplicationFactoryWithoutExceptionDetails factory;

    public ApiExceptionDetailSuppressionTests(ApiTestApplicationFactoryWithoutExceptionDetails factory)
    {
        this.factory = factory;
    }

    [Theory]
    [InlineData("/api/ContractProbe/throw")]
    [InlineData("/api/ContractProbe/caught")]
    public async Task ServerError_WithDetailsDisabled_ShouldNotLeakExceptionInfo(string url)
    {
        using var client = factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync("/api/Auth/login", new LoginRequestDto
        {
            Account = "support",
            Password = "support"
        });
        var login = JsonSerializer.Deserialize<ApiResult<TokenResponseDto>>(
            await loginResponse.Content.ReadAsStringAsync(), JsonOptions)!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", login.Data!.AccessToken);

        var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiResult<object>>(body, JsonOptions)!;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.False(result.Success);
        Assert.Equal(500, result.StatusCode);

        // 回應仍是 ApiResult 信封，但不得夾帶任何例外細節。
        Assert.Null(result.Exception);
        Assert.Null(result.ErrorDetail);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Integration probe", body, StringComparison.Ordinal);
    }
}

/// <summary>登入配額壓到 2，用來驗證限流真的會觸發且回應維持 ApiResult 信封。</summary>
public sealed class ApiTestApplicationFactoryWithTightLoginLimit : ApiTestApplicationFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimit:LoginRequestsPerMinute"] = "2"
            });
        });
    }
}

/// <summary>
/// 限流的兩個迴歸點，**都是實跑才發現的**：
///
/// 1. 登入的較嚴格配額原本用 `[EnableRateLimiting("login")]` 屬性指定，
///    但 `MapControllers().RequireRateLimiting("api")` 這個端點慣例套用時機晚於屬性，
///    會把它蓋掉 —— 登入配額**靜默失效**。改為在同一個 policy 內依路徑判斷。
/// 2. 限流拒絕原本不寫 body，空 body 的 429 會被 `UseStatusCodePagesWithReExecute`
///    拿原始的 POST + JSON 去重跑 `/not-found`（Blazor 頁面），被 antiforgery 擋下後
///    變成 **400 HTML** —— 呼叫端根本看不出真正發生什麼事。
/// </summary>
public sealed class LoginRateLimitTests : IClassFixture<ApiTestApplicationFactoryWithTightLoginLimit>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ApiTestApplicationFactoryWithTightLoginLimit factory;

    public LoginRateLimitTests(ApiTestApplicationFactoryWithTightLoginLimit factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Login_BeyondQuota_ShouldReturn429_WithApiResultEnvelope()
    {
        using var client = factory.CreateClient();
        var request = new LoginRequestDto { Account = "nobody", Password = "wrong" };

        // 配額 2：前兩次應為 401（帳密錯誤），之後才被限流。
        for (var i = 0; i < 2; i++)
        {
            var allowed = await client.PostAsJsonAsync("/api/Auth/login", request);
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        var response = await client.PostAsJsonAsync("/api/Auth/login", request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        // 必須是 ApiResult JSON，不能是被錯誤頁重跑後的 HTML。
        Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        var result = JsonSerializer.Deserialize<ApiResult<object>>(body, JsonOptions)!;
        Assert.False(result.Success);
        Assert.Equal(429, result.StatusCode);
    }

    /// <summary>登入配額不應消耗一般 API 的配額（兩者分開計數）。</summary>
    [Fact]
    public async Task GeneralApi_ShouldNotBeAffectedByLoginQuota()
    {
        using var client = factory.CreateClient();
        var request = new LoginRequestDto { Account = "nobody", Password = "wrong" };

        for (var i = 0; i < 5; i++)
        {
            await client.PostAsJsonAsync("/api/Auth/login", request);
        }

        var response = await client.PostAsJsonAsync("/api/Project/search", new { PageIndex = 1, PageSize = 10 });

        // 未帶 token 應為 401；若是 429 就代表兩者共用了計數器。
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

[ApiController]
[Route("api/[controller]")]
public sealed class ContractProbeController : ControllerBase
{
    [HttpGet("forbidden")]
    [Authorize(
        AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme,
        Policy = "IntegrationForbidden")]
    public IActionResult ForbiddenProbe()
    {
        return Ok();
    }

    [HttpGet("throw")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public IActionResult ThrowProbe()
    {
        throw new InvalidOperationException("Integration probe exception.");
    }

    /// <summary>
    /// 模擬 Controller 自行 catch 後回 500 的路徑（正式 Controller 共 16 處這樣寫）。
    /// 與 <see cref="ThrowProbe"/> 的差別在於它不會冒泡到 ApiExceptionFilter。
    /// </summary>
    [HttpGet("caught")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public IActionResult CaughtProbe()
    {
        try
        {
            throw new InvalidOperationException("Integration probe caught exception.");
        }
        catch (Exception ex)
        {
            return this.ApiServerError("探針錯誤", ex);
        }
    }
}
