using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Auths;
using MyProject.Share.Helpers;
using MyProject.Web.Auth;

namespace MyProject.Tests;

/// <summary>
/// 雙站台模式的測試主機（0.10.10 起，WASM 遷移 M11）：設定 <c>ClientSettings:BaseUrl</c>（前端是獨立的靜態站台），
/// 另開 Google 登入與 Pickup 寄信，信件改收進 <see cref="CapturingEmailQueue"/>。
/// 登入配額調成每分鐘 3 次，用來確認跨來源的預檢不會吃掉配額。
/// </summary>
public sealed class DualSiteTestApplicationFactory : ApiTestApplicationFactory
{
    public const string FrontendBaseUrl = "https://app.example.com/";
    public const string FrontendOrigin = "https://app.example.com";

    /// <summary>Google 設定在 Program.cs 啟動時就讀，所以跟 <see cref="ApiTestApplicationFactoryWithGoogle"/> 一樣用環境變數。</summary>
    private static readonly Dictionary<string, string> EnvironmentSettings = new()
    {
        ["GoogleOAuthSettings__Enabled"] = "true",
        ["GoogleOAuthSettings__ClientId"] = "integration-client-id",
        ["GoogleOAuthSettings__ClientSecret"] = "integration-client-secret",
        ["ClientSettings__BaseUrl"] = FrontendBaseUrl,
    };

    public DualSiteTestApplicationFactory()
    {
        foreach (var item in EnvironmentSettings)
        {
            Environment.SetEnvironmentVariable(item.Key, item.Value);
        }
    }

    private readonly CapturingEmailQueue emails = new();

    /// <summary>入列的信（寄信佇列換成只收集、不寄送）。</summary>
    public IReadOnlyList<MyProject.Models.Systems.EmailMessage> Emails => emails.Enqueued;

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ClientSettings:BaseUrl"] = FrontendBaseUrl,
                ["EmailSettings:Provider"] = "Pickup",
                ["RateLimit:LoginRequestsPerMinute"] = "3",
            });
        });

        builder.ConfigureTestServices(services => services.AddSingleton<IEmailQueue>(emails));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        foreach (var item in EnvironmentSettings)
        {
            Environment.SetEnvironmentVariable(item.Key, null);
        }
    }
}

/// <summary>
/// 雙站台模式（0.10.10 起，WASM 遷移 M11）：前端在 <c>ClientSettings:BaseUrl</c>（例如 https://app.公司.com/），API 主機只提供 API。
/// CORS 只允許前端那一個來源（不帶憑證，不用 Cookie）；非 API 路徑回 404；Google 登入與信中連結一律導回設定的前端網址。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class DualSiteHostTests : IClassFixture<DualSiteTestApplicationFactory>
{
    private readonly DualSiteTestApplicationFactory factory;

    public DualSiteHostTests(DualSiteTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task Preflight_FromTheFrontend_ShouldBeAllowed_WithoutCredentials()
    {
        using var client = NoRedirectClient();

        var response = await client.SendAsync(Preflight("/api/v1/Auth/me", DualSiteTestApplicationFactory.FrontendOrigin, "PUT", "authorization,content-type"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(DualSiteTestApplicationFactory.FrontendOrigin, Header(response, "Access-Control-Allow-Origin"));
        var allowedHeaders = Header(response, "Access-Control-Allow-Headers").ToLowerInvariant();
        Assert.Contains("authorization", allowedHeaders);
        Assert.Contains("content-type", allowedHeaders);
        Assert.Contains("PUT", Header(response, "Access-Control-Allow-Methods"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    /// <summary>預檢不經過驗證與限流：登入配額每分鐘 3 次，連續 6 次預檢都要通過（否則前端第 4 個請求就被 CORS 擋下）。</summary>
    [Fact]
    public async Task Preflights_ShouldNotBeRateLimited()
    {
        using var client = NoRedirectClient();

        for (var i = 0; i < 6; i++)
        {
            var response = await client.SendAsync(Preflight("/api/v1/Auth/password/forgot", DualSiteTestApplicationFactory.FrontendOrigin, "POST", "content-type"));

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal(DualSiteTestApplicationFactory.FrontendOrigin, Header(response, "Access-Control-Allow-Origin"));
        }
    }

    [Fact]
    public async Task ActualRequest_FromTheFrontend_ShouldExposeTheDownloadFileName()
    {
        using var client = NoRedirectClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/Auth/login-settings");
        request.Headers.Add("Origin", DualSiteTestApplicationFactory.FrontendOrigin);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(DualSiteTestApplicationFactory.FrontendOrigin, Header(response, "Access-Control-Allow-Origin"));
        Assert.Contains("content-disposition", Header(response, "Access-Control-Expose-Headers").ToLowerInvariant());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    /// <summary>scheme、主機、連接埠任一不同都是別的來源。</summary>
    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("http://app.example.com")]
    [InlineData("https://app.example.com:8443")]
    [InlineData("https://app.example.com.evil.com")]
    public async Task OtherOrigins_ShouldGetNoCorsHeaders(string origin)
    {
        using var client = NoRedirectClient();
        using var actual = new HttpRequestMessage(HttpMethod.Get, "/api/v1/Auth/login-settings");
        actual.Headers.Add("Origin", origin);

        var preflightResponse = await client.SendAsync(Preflight("/api/v1/Auth/me", origin, "GET", "authorization"));
        var actualResponse = await client.SendAsync(actual);

        Assert.False(preflightResponse.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(actualResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/projects")]
    [InlineData("/index.html")]
    [InlineData("/theme.css")]
    public async Task NonApiPaths_ShouldReturn404_BecauseTheFrontendLivesElsewhere(string url)
    {
        using var client = NoRedirectClient();

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("<div id=\"app\">", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GoogleCallback_ShouldReturnToTheConfiguredFrontend()
    {
        var user = await AddUserAsync();
        var verifier = "verifier-" + Guid.NewGuid().ToString("N");

        var callback = await GoogleCallbackAsync(user.Email!, GoogleLoginCodes.HashVerifier(verifier), host: "attacker.example.com");

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.StartsWith(DualSiteTestApplicationFactory.FrontendBaseUrl + "Auths/Login#code=", callback.Headers.Location!.OriginalString);
    }

    /// <summary>失敗時導回的登入頁也在前端；只看設定值，不採用請求的 Host 或任何參數（不可成為開放式重新導向）。</summary>
    [Fact]
    public async Task GoogleFailures_ShouldAlsoReturnToTheConfiguredFrontend()
    {
        using var client = NoRedirectClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Auths/Google/Login?returnUrl=https%3A%2F%2Fevil.com%2F");
        request.Headers.Host = "attacker.example.com";

        var withoutBinding = await client.SendAsync(request);
        var withoutBindingOnCallback = await GoogleCallbackAsync((await AddUserAsync()).Email!, binding: null, host: "attacker.example.com");

        Assert.Equal(DualSiteTestApplicationFactory.FrontendBaseUrl + "Auths/Login", withoutBinding.Headers.Location!.OriginalString);
        Assert.Equal(DualSiteTestApplicationFactory.FrontendBaseUrl + "Auths/Login", withoutBindingOnCallback.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task PasswordResetEmail_ShouldLinkToTheFrontend()
    {
        var user = await AddUserAsync();
        using var client = NoRedirectClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/Auth/password/forgot")
        {
            Content = JsonContent.Create(new ForgotPasswordRequestDto { Identifier = user.Account }),
        };
        request.Headers.Host = "attacker.example.com";

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var mail = Assert.Single(factory.Emails, x => x.To.Contains(user.Email!, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(DualSiteTestApplicationFactory.FrontendBaseUrl + "Auths/ResetPassword?token=", mail.HtmlBody + mail.TextBody);
        Assert.DoesNotContain("attacker.example.com", mail.HtmlBody + mail.TextBody);
    }

    private HttpClient NoRedirectClient() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static HttpRequestMessage Preflight(string url, string origin, string method, string headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, url);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", headers);
        return request;
    }

    private static string Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : string.Empty;

    private async Task<HttpResponseMessage> GoogleCallbackAsync(string email, string? binding, string host)
    {
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(MagicObjectHelper.ExternalCookieScheme);
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, $"google-{Guid.NewGuid():N}"), new Claim(ClaimTypes.Email, email), new Claim(ClaimTypes.Name, email)],
            "Google");
        var properties = new AuthenticationProperties();
        if (binding is not null)
        {
            properties.Items[GoogleLoginCodes.BindingItemKey] = binding;
        }

        var ticket = options.TicketDataFormat.Protect(new AuthenticationTicket(new ClaimsPrincipal(identity), properties, MagicObjectHelper.ExternalCookieScheme));
        using var client = NoRedirectClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Auths/Google/Callback?returnUrl=%2Fprojects");
        request.Headers.Host = host;
        request.Headers.Add("Cookie", $"{options.Cookie.Name}={ticket}");
        return await client.SendAsync(request);
    }

    private async Task<MyUser> AddUserAsync()
    {
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<BackendDBContext>>();
        await using var context = await contextFactory.CreateDbContextAsync();
        var role = new RoleView { Name = $"m11-{Guid.NewGuid():N}", TabViewJson = "[]" };
        context.RoleView.Add(role);
        await context.SaveChangesAsync();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = new MyUser
        {
            Account = $"m11-{suffix}",
            Name = $"m11-{suffix}",
            Email = $"m11-{suffix}@example.com",
            Password = "unused",
            Status = true,
            RoleViewId = role.Id,
        };
        context.MyUser.Add(user);
        await context.SaveChangesAsync();
        return user;
    }
}
