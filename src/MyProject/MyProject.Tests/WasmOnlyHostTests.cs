using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MyProject.Tests;

/// <summary>
/// 主機只提供 WebAssembly（0.10.9 起，WASM 遷移 M10 移除 Server 模式）：<c>MyProject.Web</c> 只剩 API、WASM 的主機頁（<c>Components/App.razor</c>）與靜態檔，
/// 沒有 Blazor Server 的互動模式、電路與 SignalR <c>/_blazor</c> 連線。
/// 有人照舊的 Blazor Server 寫法加回元件或渲染模式時，這裡會擋下（頁面一律放在 <c>MyProject.Web.Client</c>）。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class WasmOnlyHostTests : IClassFixture<ApiTestApplicationFactory>
{
    /// <summary>Server 模式的痕跡：出現在 <c>MyProject.Web</c> 的任何原始檔（含註解）都算。</summary>
    private static readonly string[] ServerModeMarkers =
    [
        "InteractiveServer",
        "CircuitHandler",
        "ReconnectModal",
        "AuthenticationStateHelper",
        "AuthorizeRouteView",
    ];

    private readonly ApiTestApplicationFactory factory;

    public WasmOnlyHostTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    /// <summary>沒有註冊 Server 互動模式時就沒有 <c>/_blazor</c> 的 SignalR 端點：協商一定不會成功。</summary>
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

    /// <summary>主機組件只剩主機頁 <c>App</c> 一個元件，而且沒有任何 <c>@page</c>；頁面一律放在 <c>MyProject.Web.Client</c>。</summary>
    [Fact]
    public void WebAssembly_ShouldOnlyContainTheHostPage()
    {
        var components = typeof(MyProject.Web.Program).Assembly.GetTypes()
            .Where(type => typeof(IComponent).IsAssignableFrom(type) && !type.IsAbstract && type.Name != "_Imports")
            .ToList();

        // 防空跑：掃描方式失效時至少還找得到主機頁。
        Assert.Contains(typeof(MyProject.Web.Components.App), components);
        Assert.True(components.Count == 1,
            "MyProject.Web 只能有主機頁 App 一個元件（畫面一律放在 MyProject.Web.Client）：" + string.Join("、", components.Select(type => type.FullName)));
        Assert.DoesNotContain(components, type => type.GetCustomAttributes<RouteAttribute>().Any());
    }

    [Fact]
    public void WebSources_ShouldNotMentionServerMode()
    {
        var files = Directory.EnumerateFiles(SourceTree.Web, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".cs" or ".razor" or ".css" or ".js" or ".csproj" or ".json" or ".config")
            .Where(path => !Path.GetRelativePath(SourceTree.Web, path).Split(Path.DirectorySeparatorChar).Any(x => x is "bin" or "obj" or "lib"))
            .ToList();

        // 防空跑：掃描路徑失效時不能變成永遠通過。
        Assert.True(files.Count > 100, $"只掃到 {files.Count} 個檔案，掃描路徑可能失效。");

        var violations = files
            .SelectMany(path => ServerModeMarkers
                .Where(marker => File.ReadAllText(path).Contains(marker, StringComparison.Ordinal))
                .Select(marker => $"{Path.GetRelativePath(SourceTree.Web, path)}：{marker}"))
            .ToList();

        Assert.True(violations.Count == 0,
            "MyProject.Web 不再有 Blazor Server 模式（0.10.9 起），下列檔案仍提到它："
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }
}
