using Bunit;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Commons;

namespace MyProject.Tests.Client;

/// <summary>
/// 前端連到哪一台 API 主機（0.10.10 起，WASM 遷移 M11）：<c>wwwroot/appsettings.json</c> 的 <c>ApiBaseUrl</c>
/// 留空＝與網頁同一個網址（同站台），有值＝獨立的 API 主機（雙站台）。
/// 所有呼叫 API 的 HttpClient、整頁導到伺服器端點的網址（Google 登入）、交給瀏覽器的下載與上傳網址都以它為準。
/// </summary>
public sealed class ApiAddressTests
{
    private const string HostBase = "https://app.example.com/";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_ShouldUseTheSiteItself(string? configured)
    {
        var address = ApiAddress.Resolve(configured, HostBase);

        Assert.Equal(new Uri(HostBase), address.BaseAddress);
        Assert.Equal(new Uri(HostBase), address.CreateHttpClient().BaseAddress);
    }

    [Theory]
    [InlineData("https://api.example.com/", "https://api.example.com/")]
    [InlineData("https://api.example.com", "https://api.example.com/")]
    [InlineData(" https://api.example.com:8443/erp ", "https://api.example.com:8443/erp/")]
    [InlineData("http://localhost:5100", "http://localhost:5100/")]
    public void Configured_ShouldBeUsedWithATrailingSlash(string configured, string expected)
    {
        var address = ApiAddress.Resolve(configured, HostBase);

        Assert.Equal(new Uri(expected), address.BaseAddress);

        using var http = address.CreateHttpClient();
        Assert.Equal(new Uri(expected), http.BaseAddress);
        Assert.Equal(new Uri(expected + "api/v1/Auth/me"), new Uri(http.BaseAddress!, "api/v1/Auth/me"));
    }

    [Theory]
    [InlineData("api.example.com")]
    [InlineData("/api")]
    [InlineData("ftp://api.example.com/")]
    [InlineData("https://api.example.com/?x=1")]
    public void Invalid_ShouldFailWithTheKeyName(string configured)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ApiAddress.Resolve(configured, HostBase));

        Assert.Contains("ApiBaseUrl", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GoogleLogin_ShouldPointAtTheApiHost()
    {
        var address = ApiAddress.Resolve("https://api.example.com/", HostBase);

        Assert.Equal("https://api.example.com/Auths/Google/Login?binding=abc", address.GoogleLogin(returnUrl: null, binding: "abc"));
        Assert.Equal(
            "https://api.example.com/Auths/Google/Login?returnUrl=%2Fprojects%3Fx%3D1&binding=abc",
            address.GoogleLogin(returnUrl: "/projects?x=1", binding: "abc"));
        Assert.Equal("https://app.example.com/Auths/Google/Login?binding=abc", ApiAddress.Resolve("", HostBase).GoogleLogin(null, "abc"));
    }

    /// <summary>伺服器回的下載網址（例如 <c>/api/v1/downloads/{票證}</c>）是 API 主機上的路徑，不是前端網站的。</summary>
    [Fact]
    public void Absolute_ShouldResolveServerPathsAgainstTheApiHost()
    {
        var address = ApiAddress.Resolve("https://api.example.com/", HostBase);

        Assert.Equal(new Uri("https://api.example.com/api/v1/downloads/t1"), address.Absolute("/api/v1/downloads/t1"));
        Assert.Equal(new Uri("https://api.example.com/api/v1/Project/3"), address.Absolute("api/v1/Project/3"));
    }

    /// <summary>附件上傳與下載由瀏覽器直接送出（不經過 HttpClient），交給 JS 的一定是 API 主機的完整網址。</summary>
    [Fact]
    public async Task BrowserFiles_ShouldHandAbsoluteApiUrlsToTheBrowser()
    {
        await using var context = new BunitContext();
        var module = context.JSInterop.SetupModule("./js/browser-files.js");
        module.Setup<string?>("download", _ => true).SetResult(null);
        module.Setup<RawResponse>("send", _ => true).SetResult(new RawResponse(200, "{}"));
        var files = new BrowserFiles(context.JSInterop.JSRuntime, ApiAddress.Resolve("https://api.example.com/", HostBase));

        Assert.Null(await files.DownloadAsync("/api/v1/downloads/t1"));
        await files.SendAsync("PUT", "api/v1/Project/3", "token", "{}", [], []);

        Assert.Equal("https://api.example.com/api/v1/downloads/t1", module.Invocations["download"].Single().Arguments[0]);
        Assert.Equal("https://api.example.com/api/v1/Project/3", module.Invocations["send"].Single().Arguments[1]);
    }

    /// <summary>
    /// 0.10.10 獨立審查：雙站台時下載網址跨來源，瀏覽器會忽略 &lt;a download&gt; 而整頁導過去（失敗時整個前端換成 API 的 JSON）。
    /// 跨來源改由 JS 以 fetch 取回再存檔；失敗時把伺服器的訊息交回呼叫端顯示，頁面留在原地。
    /// </summary>
    [Fact]
    public async Task BrowserFiles_Download_ShouldReturnTheServerMessage_WhenTheDownloadFails()
    {
        await using var context = new BunitContext();
        var module = context.JSInterop.SetupModule("./js/browser-files.js");
        module.Setup<string?>("download", _ => true).SetResult("下載連結已失效，請重新下載。");
        var files = new BrowserFiles(context.JSInterop.JSRuntime, ApiAddress.Resolve("https://api.example.com/", HostBase));

        Assert.Equal("下載連結已失效，請重新下載。", await files.DownloadAsync("/api/v1/downloads/t1"));
    }
}
