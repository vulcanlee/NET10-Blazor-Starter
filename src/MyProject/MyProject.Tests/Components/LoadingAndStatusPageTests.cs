using System.Text.Json;
using System.Text.RegularExpressions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyProject.Web.Client.Commons;
using MyProject.Web.Client.Pages;

namespace MyProject.Tests.Components;

/// <summary>
/// 首次載入畫面、系統錯誤頁、找不到頁面（0.10.13 起）。
/// <list type="bullet">
/// <item>載入畫面：WASM 下載與初始化期間（index.html 的 #app）以及外框登入檢查期間（ClientMainLayout）顯示同一張卡片，
/// 不再整片空白。下載進度用 Blazor 內建的 CSS 變數 <c>--blazor-load-percentage</c>／<c>--blazor-load-percentage-text</c>，不需行內腳本（CSP 只允許 import map）。</item>
/// <item>系統名稱是固定值（下載完成前讀不到伺服器設定），要等於 appsettings 的 SystemName（manifest 也是，由 PwaConventionTests 比對）；改名時要一起改。</item>
/// <item>錯誤頁與找不到頁面一律繁體中文，並提供回首頁的按鈕。</item>
/// </list>
/// </summary>
public sealed class LoadingAndStatusPageTests : ComponentTestBase
{
    private static string IndexHtml => File.ReadAllText(Path.Combine(SourceTree.ClientWebRoot, "index.html"));

    private static string AppCss => File.ReadAllText(Path.Combine(SourceTree.ClientWebRoot, "app.css"));

    private static string ShippedSystemName()
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(SourceTree.WebFile("appsettings.json")), ShippedAppSettings.JsonOptions);
        return settings.RootElement.GetProperty("SystemSettings").GetProperty("SystemInformation").GetProperty("SystemName").GetString()!;
    }

    [Fact]
    public void IndexHtml_ShouldShowTheLoadingCard_InsideTheAppElement()
    {
        var app = Regex.Match(IndexHtml, "<div id=\"app\">(?<content>[\\s\\S]*?)</div>\\s*<script", RegexOptions.None).Groups["content"].Value;

        Assert.Contains("class=\"app-loading\"", app, StringComparison.Ordinal);
        Assert.Contains($">{ShippedSystemName()}<", app, StringComparison.Ordinal);
        Assert.Contains("正在載入，請稍候", app, StringComparison.Ordinal);
        Assert.Contains("第一次開啟需要下載程式，之後會更快", app, StringComparison.Ordinal);
        Assert.Contains("app-loading-bar-fill", app, StringComparison.Ordinal);
    }

    /// <summary>啟動失敗時卡片不會被取代：30 秒後浮現「請重新整理」的提示（純 CSS，獨立審查的建議）。</summary>
    [Fact]
    public void IndexHtml_ShouldHintWhenStartupNeverFinishes()
    {
        Assert.Contains("class=\"app-loading-stuck\"", IndexHtml, StringComparison.Ordinal);
        Assert.Matches(@"\.app-loading-stuck\s*\{[^}]*animation:[^;]*\d+s forwards", AppCss);
    }

    [Fact]
    public void LoadingStyles_ShouldFollowBlazorsDownloadProgress()
    {
        Assert.Matches(@"\.app-loading-bar-fill\s*\{[^}]*width:\s*var\(--blazor-load-percentage", AppCss);
        Assert.Matches(@"\.app-loading-percent::after\s*\{[^}]*content:\s*var\(--blazor-load-percentage-text", AppCss);
    }

    [Fact]
    public void LoadingScreen_ShouldUseTheSameFixedSystemName()
    {
        Assert.Equal(ClientLoadingScreen.FixedSystemName, ShippedSystemName());
    }

    [Fact]
    public void LoadingScreen_ShouldShowTheNameAndTheMessage_WithoutTheFirstDownloadHint()
    {
        var cut = Render<ClientLoadingScreen>(parameters => parameters.Add(p => p.Message, "正在確認登入狀態…"));

        Assert.Equal(ClientLoadingScreen.FixedSystemName, cut.Find(".app-loading-title").TextContent);
        Assert.Contains("正在確認登入狀態…", cut.Find(".app-loading-text").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find(".app-loading.app-loading-done"));
        Assert.Empty(cut.FindAll(".app-loading-hint"));
    }

    [Fact]
    public void MainLayout_ShouldShowTheLoadingScreen_WhileCheckingTheSession()
    {
        var markup = File.ReadAllText(Path.Combine(SourceTree.Client, "Layout", "ClientMainLayout.razor"));

        // 閘門維持：登入檢查通過（isAuthenticated）前不渲染任何頁面內容，只有載入卡片（不含任何資料）。
        var lastElse = markup.LastIndexOf("else\n", StringComparison.Ordinal) is var lf and >= 0 ? lf : markup.LastIndexOf("else\r\n", StringComparison.Ordinal);
        Assert.True(lastElse > markup.IndexOf("@if (isAuthenticated)", StringComparison.Ordinal), "ClientMainLayout 的最後一個 else 分支要顯示載入卡片。");
        Assert.Contains("<ClientLoadingScreen", markup[lastElse..], StringComparison.Ordinal);
    }

    /// <summary>登入相關頁面在讀取設定（或登出、完成 Google 登入）期間也顯示載入卡片，接續下載期間的那一張（0.10.13 實機：登入頁讀設定時一閃空白）。</summary>
    [Theory]
    [InlineData("Login.razor")]
    [InlineData("ForgotPassword.razor")]
    [InlineData("ResetPassword.razor")]
    [InlineData("TwoFactor.razor")]
    [InlineData("Logout.razor")]
    public void AuthPages_ShouldShowTheLoadingScreen_WhileLoading(string file)
    {
        var markup = File.ReadAllText(Path.Combine(SourceTree.Client, "Views", "Auths", file));

        Assert.Contains("<ClientLoadingScreen", markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("A1B2C3", true)]
    [InlineData("<script>", false)]
    [InlineData(null, false)]
    public void ErrorPage_ShouldBeInChinese_WithActions_AndOnlyAValidTraceCode(string? trace, bool showsTrace)
    {
        Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(trace is null ? "/Error" : $"/Error?trace={Uri.EscapeDataString(trace)}");

        var cut = Render<ErrorPage>();

        Assert.Contains("系統發生未預期的錯誤", cut.Find("h1").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("a.status-card-primary[href='/']"));
        Assert.Contains("回首頁", cut.Find("a.status-card-primary").TextContent, StringComparison.Ordinal);
        Assert.Contains("回上一頁", cut.Find("button.status-card-secondary").TextContent, StringComparison.Ordinal);
        Assert.Equal(showsTrace, cut.FindAll(".status-card-trace code").Count == 1);
        Assert.Equal(showsTrace, cut.FindAll("button.status-card-copy").Count == 1);
        Assert.DoesNotContain("<script>", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorPage_CopyButton_ShouldCopyTheTraceCode()
    {
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        JSInterop.Setup<bool>("appClipboard.copyText", "A1B2C3").SetResult(true);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/Error?trace=A1B2C3");

        var cut = Render<ErrorPage>();
        cut.Find("button.status-card-copy").Click();

        cut.WaitForAssertion(() => Assert.Contains("已複製", cut.Find(".status-card-trace").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void NotFound_ShouldBeInChinese_WithAHomeButton()
    {
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var cut = Render<NotFoundContent>();

        Assert.Contains("找不到這個頁面", cut.Find("h1").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("a.status-card-primary[href='/']"));
        Assert.DoesNotContain("Not Found", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Sorry", cut.Markup, StringComparison.Ordinal);
    }

    /// <summary>Blazor 未處理例外時出現在畫面底部的黃色提示列（#blazor-error-ui）與預設錯誤邊界的文字，也要是繁體中文。</summary>
    [Fact]
    public void BuiltInErrorTexts_ShouldBeInChinese()
    {
        foreach (var layout in Directory.GetFiles(Path.Combine(SourceTree.Client, "Layout"), "*.razor"))
        {
            var markup = File.ReadAllText(layout);
            Assert.DoesNotContain("An unhandled error has occurred", markup, StringComparison.Ordinal);
            Assert.DoesNotContain(">Reload<", markup, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("An error has occurred", AppCss, StringComparison.Ordinal);
    }
}
