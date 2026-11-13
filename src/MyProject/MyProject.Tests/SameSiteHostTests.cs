using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using MyProject.Dtos.Commons;

namespace MyProject.Tests;

/// <summary>
/// 同站台模式（0.10.10 起，WASM 遷移 M11；<c>ClientSettings:BaseUrl</c> 留空，預設）：API 主機一併提供前端的靜態檔。
/// 不是 API、也不是實體檔案的網址一律回前端的 <c>index.html</c>（由前端路由決定顯示哪一頁，找不到時顯示找不到頁面）；
/// <c>/api/</c> 底下找不到的路由回 404 的 <see cref="ApiResult"/>；<c>_framework</c> 底下找不到的檔案回 404，不回 index.html。
/// 沒有前端網址時不開放跨來源（沒有任何 CORS 標頭）。安全標頭與 CSP 跟靜態站的 web.config 一致（見 StaticSiteWebConfigTests）。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class SameSiteHostTests : IClassFixture<ApiTestApplicationFactory>
{
    private static readonly Regex ImportMap = new("<script type=\"importmap\">(?<content>[\\s\\S]*?)</script>", RegexOptions.Compiled);
    private static readonly Regex BlazorScript = new("src=\"(?<src>_framework/blazor\\.webassembly[^\"]*\\.js)\"", RegexOptions.Compiled);

    private readonly ApiTestApplicationFactory factory;

    public SameSiteHostTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/projects")]
    [InlineData("/Auths/Login?returnUrl=%2Fprojects")]
    [InlineData("/no-such-page")]
    public async Task NonApiRoutes_ShouldServeTheFrontendIndexHtml(string route)
    {
        using var client = NoRedirectClient();

        var response = await client.GetAsync(route);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<div id=\"app\">", body, StringComparison.Ordinal);
        Assert.Matches(BlazorScript, body);

        // 前端的入口頁不可被快取：部署新版後瀏覽器要立刻拿到新的檔名（指紋）。
        Assert.Contains("no-cache", response.Headers.CacheControl?.ToString());
    }

    /// <summary>
    /// ⚠️ 只測 GET：從原始碼執行（開發、測試主機）時 MapStaticAssets 另有一個只收 GET／HEAD 的萬用端點 <c>{**path:file}</c>
    /// （建置後新增的檔案也能取得），方法比對先於路由約束，所以這時 POST 到不存在的 API 是 405（同樣是 ApiResult）。
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/v1/no-such-endpoint")]
    [InlineData("GET", "/api/no-such-controller/action")]
    public async Task UnknownApiRoute_ShouldReturn404ApiResult(string method, string url)
    {
        using var client = NoRedirectClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var result = await response.Content.ReadFromJsonAsync<ApiResult<object>>();
        Assert.False(result!.Success);
        Assert.Equal(404, result.StatusCode);
    }

    [Fact]
    public async Task FrameworkFiles_ShouldBeServed_AndMissingOnesShouldNotFallBackToIndexHtml()
    {
        using var client = NoRedirectClient();
        var index = await client.GetStringAsync("/");
        var script = BlazorScript.Match(index).Groups["src"].Value;

        var found = await client.GetAsync("/" + script);
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.Contains("javascript", found.Content.Headers.ContentType?.MediaType);

        var missing = await client.GetAsync("/_framework/no-such-file.js");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.DoesNotContain("<div id=\"app\">", await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>前端的靜態檔（0.10.10 起從 MyProject.Web 搬到 MyProject.Web.Client）由主機一併提供。</summary>
    [Theory]
    [InlineData("/theme.css", "text/css")]
    [InlineData("/app.css", "text/css")]
    [InlineData("/favicon.png", "image/png")]
    [InlineData("/js/client-auth.js", "text/javascript")]
    [InlineData("/js/clipboard.js", "text/javascript")]
    [InlineData("/appsettings.json", "application/json")]
    [InlineData("/MyProject.Web.Client.styles.css", "text/css")]
    public async Task FrontendStaticFiles_ShouldBeServed(string url, string contentType)
    {
        using var client = NoRedirectClient();

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task IndexHtml_ShouldCarryTheSecurityHeadersAndTheContentSecurityPolicy()
    {
        using var client = NoRedirectClient();

        var response = await client.GetAsync("/projects");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("strict-origin-when-cross-origin", Header(response, "Referrer-Policy"));

        var csp = Header(response, "Content-Security-Policy");
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("script-src 'self' 'wasm-unsafe-eval'", csp);
        Assert.Contains("style-src 'self' 'unsafe-inline' https://fonts.googleapis.com", csp);
        Assert.Contains("font-src 'self' https://fonts.gstatic.com", csp);
        Assert.Contains("img-src 'self' data:", csp);
        Assert.Contains("connect-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);

        // import map 是行內腳本：CSP 要帶它的雜湊（瀏覽器先把換行正規化成 LF 再算），否則整個前端載入不了。
        var importMap = ImportMap.Match(body);
        Assert.True(importMap.Success, "index.html 沒有 import map。");
        var normalized = importMap.Groups["content"].Value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        Assert.Contains($"'sha256-{hash}'", csp);
    }

    /// <summary>沒有設定前端網址（同站台）時不開放任何跨來源呼叫，連預檢也不回 CORS 標頭。</summary>
    [Fact]
    public async Task SameSite_ShouldNotSendAnyCorsHeaders()
    {
        using var client = NoRedirectClient();

        using var actual = new HttpRequestMessage(HttpMethod.Get, "/api/v1/Auth/login-settings");
        actual.Headers.Add("Origin", "https://app.example.com");
        var actualResponse = await client.SendAsync(actual);

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/Auth/login-settings");
        preflight.Headers.Add("Origin", "https://app.example.com");
        preflight.Headers.Add("Access-Control-Request-Method", "GET");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization");
        var preflightResponse = await client.SendAsync(preflight);

        Assert.Equal(HttpStatusCode.OK, actualResponse.StatusCode);
        foreach (var response in new[] { actualResponse, preflightResponse })
        {
            Assert.DoesNotContain(response.Headers, header => header.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
        }
    }

    private HttpClient NoRedirectClient() => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static string Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : string.Empty;
}
