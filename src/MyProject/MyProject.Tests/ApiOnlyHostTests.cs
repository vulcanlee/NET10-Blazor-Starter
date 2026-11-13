using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MyProject.Tests;

/// <summary>
/// <c>MyProject.Web</c> 只是 API 主機（0.10.10 起，WASM 遷移 M11；取代 M10 的 WasmOnlyHostTests）：
/// 前端是獨立的 Blazor WebAssembly 靜態站台（<c>MyProject.Web.Client/wwwroot/index.html</c>），主機沒有任何 Razor 元件、
/// 主機頁或渲染模式；同站台部署時只是把前端的靜態檔當檔案提供（見 SameSiteHostTests）。
/// 有人照 Blazor Web App 的寫法把元件、主機頁或渲染模式加回主機時，這裡會擋下（畫面一律放在 <c>MyProject.Web.Client</c>）。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class ApiOnlyHostTests : IClassFixture<ApiTestApplicationFactory>
{
    /// <summary>Blazor Web App（伺服器渲染、互動模式、主機頁）的痕跡：出現在 <c>MyProject.Web</c> 的任何原始檔（含註解）都算。</summary>
    private static readonly string[] RazorHostMarkers =
    [
        "MapRazorComponents",
        "AddRazorComponents",
        "AddInteractiveWebAssembly",
        "InteractiveServer",
        "UseAntiforgery",
        "CircuitHandler",
        "ReconnectModal",
        "AuthenticationStateHelper",
        "AuthorizeRouteView",
        "BlazorDisableThrowNavigationException",
        "blazor.web.js",
    ];

    private readonly ApiTestApplicationFactory factory;

    public ApiOnlyHostTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    /// <summary>沒有 Blazor Server 就沒有 <c>/_blazor</c> 的 SignalR 端點：協商一定不會成功。</summary>
    [Fact]
    public async Task BlazorServerHub_ShouldNotBeMapped()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", content: null);

        Assert.False(response.IsSuccessStatusCode, $"/_blazor/negotiate 回 {(int)response.StatusCode}：主機仍註冊了 Blazor Server 的 SignalR 端點。");
    }

    [Fact]
    public void CircuitHandlers_ShouldNotBeRegistered()
    {
        using var scope = factory.Services.CreateScope();

        Assert.Empty(scope.ServiceProvider.GetServices<CircuitHandler>());
    }

    /// <summary>主機組件沒有任何元件（0.10.9 以前的主機頁 <c>App</c> 也已由前端的 <c>index.html</c> 取代）。</summary>
    [Fact]
    public void WebAssembly_ShouldContainNoComponents()
    {
        var types = typeof(MyProject.Web.Program).Assembly.GetTypes();
        var components = types
            .Where(type => typeof(IComponent).IsAssignableFrom(type) && !type.IsAbstract)
            .ToList();

        // 防空跑：反射失效時至少還找得到主機的進入點。
        Assert.Contains(typeof(MyProject.Web.Program), types);
        Assert.True(components.Count == 0,
            "MyProject.Web 不可有任何元件（畫面一律放在 MyProject.Web.Client）：" + string.Join("、", components.Select(type => type.FullName)));
    }

    [Fact]
    public void WebProject_ShouldHaveNoRazorFilesOrWebRoot()
    {
        var razorFiles = WebSourceFiles().Where(path => Path.GetExtension(path) is ".razor" or ".cshtml").ToList();

        Assert.True(razorFiles.Count == 0, "MyProject.Web 不可有 .razor／.cshtml：" + string.Join("、", razorFiles));

        // 前端的靜態檔（theme.css、app.css、js、圖片、bootstrap）0.10.10 起在 MyProject.Web.Client/wwwroot。
        Assert.False(Directory.Exists(SourceTree.WebFile("wwwroot")), "MyProject.Web/wwwroot 應已搬到 MyProject.Web.Client/wwwroot。");
        Assert.True(File.Exists(Path.Combine(SourceTree.ClientWebRoot, "index.html")));
    }

    [Fact]
    public void WebSources_ShouldNotMentionTheRazorHost()
    {
        var files = WebSourceFiles()
            .Where(path => Path.GetExtension(path) is ".cs" or ".razor" or ".css" or ".js" or ".csproj" or ".json" or ".config")
            .ToList();

        // 防空跑：掃描路徑失效時不能變成永遠通過。
        Assert.True(files.Count > 100, $"只掃到 {files.Count} 個檔案，掃描路徑可能失效。");

        var violations = files
            .SelectMany(path => RazorHostMarkers
                .Where(marker => File.ReadAllText(path).Contains(marker, StringComparison.Ordinal))
                .Select(marker => $"{Path.GetRelativePath(SourceTree.Web, path)}：{marker}"))
            .ToList();

        Assert.True(violations.Count == 0,
            "MyProject.Web 只是 API 主機（0.10.10 起），下列檔案仍提到 Blazor 的主機頁、渲染模式或 Server 模式："
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    private static List<string> WebSourceFiles()
        => Directory.EnumerateFiles(SourceTree.Web, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(SourceTree.Web, path).Split(Path.DirectorySeparatorChar).Any(x => x is "bin" or "obj"))
            .ToList();
}
