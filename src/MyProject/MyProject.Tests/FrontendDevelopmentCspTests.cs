using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using MyProject.Web.Hosting;

namespace MyProject.Tests;

/// <summary>
/// 開發環境的前端 CSP（0.10.13 起）：Visual Studio 的「瀏覽器連結」（Browser Link，SignalR 連到 http://localhost:&lt;埠&gt;）
/// 與熱重載（WebSocket 連到 ws/wss://localhost:&lt;埠&gt;）都不是本站來源，原本被 <c>connect-src 'self'</c> 擋下，
/// 開發者第一次啟動就在主控台看到錯誤。只在 Development 才允許連到 localhost 的開發工具；其他環境（含正式）完全不變。
/// </summary>
public sealed class FrontendDevelopmentCspTests
{
    private static readonly string[] DevToolSources = ["http://localhost:*", "https://localhost:*", "ws://localhost:*", "wss://localhost:*"];

    [Fact]
    public void Build_WithLocalDevTools_ShouldAllowLocalhostInConnectSrcOnly()
    {
        var csp = FrontendContentSecurityPolicy.Build(importMapHash: null, apiOrigin: null, allowLocalDevTools: true);

        var connectSrc = Directive(csp, "connect-src");
        Assert.StartsWith("connect-src 'self'", connectSrc, StringComparison.Ordinal);
        foreach (var source in DevToolSources)
        {
            Assert.Contains(source, connectSrc, StringComparison.Ordinal);
        }

        // 只放寬連線：腳本來源等其他規則不變。
        Assert.Equal("script-src 'self' 'wasm-unsafe-eval'", Directive(csp, "script-src"));
        Assert.Equal("default-src 'self'", Directive(csp, "default-src"));
    }

    [Fact]
    public void Build_ByDefault_ShouldNotMentionLocalhost()
    {
        var csp = FrontendContentSecurityPolicy.Build(importMapHash: null, apiOrigin: null);

        Assert.DoesNotContain("localhost", csp, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    [InlineData("Testing", false)]
    public void IndexPage_ShouldAllowLocalDevTools_OnlyInDevelopment(string environmentName, bool expected)
    {
        var page = new FrontendIndexPage(new TestWebHostEnvironment { EnvironmentName = environmentName });

        var connectSrc = Directive(page.ContentSecurityPolicy, "connect-src");

        Assert.Equal(expected, connectSrc.Contains("localhost", StringComparison.Ordinal));
    }

    private static string Directive(string csp, string name)
        => csp.Split(';', StringSplitOptions.TrimEntries).Single(d => d.StartsWith(name + " ", StringComparison.Ordinal));

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "MyProject.Web";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
