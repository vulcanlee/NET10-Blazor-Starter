using System.Text.Json;
using System.Text.RegularExpressions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyProject.Web.Client.Commons;
using MyProject.Web.Client.Pages;
using MyProject.Web.Client.Views.Commons;

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

    /// <summary>
    /// 進度條不加寬度動畫（0.10.35 起）：從快取載入時百分比一下子到 100%，接著瀏覽器忙著啟動 .NET、動畫停在半途，
    /// 實機看到「100%」但進度條只到八成。
    /// </summary>
    [Fact]
    public void LoadingBar_ShouldNotAnimateItsWidth()
    {
        Assert.DoesNotMatch(@"\.app-loading-bar-fill\s*\{[^}]*transition", AppCss);
    }

    /// <summary>
    /// 「第一次開啟需要下載程式」只在這個瀏覽器還沒成功啟動過時顯示（0.10.35 起；登出後、F5 看到這句會以為又重新下載）。
    /// 判斷寫在外部腳本（CSP 不允許行內腳本），排在 head 讓卡片畫出來之前就決定好；成功啟動（卡片被取代）才記下來。
    /// </summary>
    [Fact]
    public void FirstDownloadHint_ShouldOnlyShowUntilTheAppHasStartedOnce()
    {
        var head = IndexHtml[..IndexHtml.IndexOf("</head>", StringComparison.Ordinal)];
        Assert.Contains("<script src=\"js/loading-hint.js\"></script>", head, StringComparison.Ordinal);
        Assert.Matches(@"\.app-loaded-before\s+\.app-loading-hint\s*\{[^}]*display:\s*none", AppCss);

        var script = File.ReadAllText(Path.Combine(SourceTree.ClientWebRoot, "js", "loading-hint.js"));
        Assert.Contains("app.loadedBefore", script, StringComparison.Ordinal);
        Assert.Contains("app-loaded-before", script, StringComparison.Ordinal);
        Assert.Contains("MutationObserver", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// 登出後整頁載入時（0.10.35 起，登出刻意整頁載入以清掉記憶體）：卡片說「已登出，正在回到登入頁…」，不顯示百分比與「第一次開啟」，
    /// 看起來是登出的過程而不是程式重新初始化。記號是登出時寫進這個分頁 sessionStorage 的一次性鍵，腳本讀到就刪掉（F5 不會再出現）。
    /// </summary>
    [Fact]
    public void LoadingCard_AfterSignOut_ShouldSaySignedOut()
    {
        var app = Regex.Match(IndexHtml, "<div id=\"app\">(?<content>[\\s\\S]*?)</div>\\s*<script", RegexOptions.None).Groups["content"].Value;
        Assert.Matches("class=\"app-loading-signed-out\"[^>]*>已登出，正在回到登入頁…<", app);
        Assert.Matches(@"\.app-loading-signed-out\s*\{[^}]*display:\s*none", AppCss);
        Assert.Matches(@"\.app-signed-out\s+\.app-loading-signed-out\s*\{[^}]*display:\s*block", AppCss);
        Assert.Matches(@"\.app-signed-out\s+\.app-loading-text[^{]*\{[^}]*display:\s*none", AppCss);

        var script = File.ReadAllText(Path.Combine(SourceTree.ClientWebRoot, "js", "loading-hint.js"));
        Assert.Contains($"'{MyProject.Web.Client.Auth.ClientSession.SignedOutMarkerKey}'", script, StringComparison.Ordinal);
        Assert.Contains("sessionStorage.removeItem", script, StringComparison.Ordinal);
        Assert.Contains("classList.remove('app-signed-out')", script, StringComparison.Ordinal);

        // 登入頁讀設定時的卡片接續同一句話（不會從「已登出…」跳成「正在載入…」）。
        var cut = Render<ClientLoadingScreen>(parameters => parameters.Add(p => p.Message, "正在載入…"));
        Assert.Equal("已登出，正在回到登入頁…", cut.Find(".app-loading-signed-out").TextContent);
    }

    [Fact]
    public void LoadingScreen_ShouldUseTheSameFixedSystemName()
    {
        Assert.Equal(ClientLoadingScreen.FixedSystemName, ShippedSystemName());
    }

    /// <summary>
    /// 啟動頁（<c>/</c>）的瀏覽器分頁標題是系統名稱（0.10.24 起；原本是範本殘留的英文「Home」）；設定還沒讀到的那一瞬間顯示「系統啟動中」。
    /// </summary>
    [Fact]
    public void SplashTabTitle_ShouldBeTheSystemName_OrStartingUpBeforeTheSettingsArrive()
    {
        Assert.Equal("艾克米企業系統", SplashView.ResolveTabTitle("艾克米企業系統"));
        Assert.Equal("系統啟動中", SplashView.ResolveTabTitle(string.Empty));
        Assert.Equal("系統啟動中", SplashView.ResolveTabTitle("  "));

        var page = File.ReadAllText(Path.Combine(SourceTree.Client, "Pages", "SplashPage.razor"));
        var view = File.ReadAllText(Path.Combine(SourceTree.Client, "Views", "Commons", "SplashView.razor"));
        Assert.DoesNotContain("<PageTitle>", page, StringComparison.Ordinal);
        Assert.Contains("<PageTitle>@ResolveTabTitle(SystemName)</PageTitle>", view, StringComparison.Ordinal);
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

    /// <summary>
    /// 帳號待審核頁（0.10.29 起）改用共用的整頁狀態卡片：原本自己寫一份卡片樣式，標題沒有設定顏色，
    /// 在深色漸層上變成深色字看不到（錯誤頁 0.10.13 修過，這頁漏了）。提示行用近白色粗體，不再用對比不足的粉紅色。
    /// </summary>
    [Fact]
    public void PendingPage_ShouldUseTheStatusCard_WithAHintAndABackToLoginButton()
    {
        var cut = Render<MyProject.Web.Client.Views.Auths.Pending>();

        Assert.NotNull(cut.Find("section.status-card-fullscreen"));
        Assert.Equal("帳號已建立，等待審核", cut.Find("h1.status-card-title").TextContent);
        Assert.Contains("請在管理者啟用您的帳號後", cut.Find("p.status-card-hint").TextContent, StringComparison.Ordinal);
        Assert.Contains("返回登入頁", cut.Find("a.status-card-primary[href='/Auths/Login']").TextContent, StringComparison.Ordinal);
    }

    /// <summary>狀態卡片的標題要跟著卡片的文字色（深色整頁版才看得到），頁面也不要再自己寫一份卡片樣式。</summary>
    [Fact]
    public void StatusCardTitle_ShouldInheritTheCardColor_AndThePendingPageHasNoOwnCardStyles()
    {
        var css = File.ReadAllText(Path.Combine(SourceTree.Client, "Commons", "StatusCard.razor.css"));

        Assert.Matches(@"\.status-card-title\s*\{[^}]*color:\s*inherit", css);
        Assert.Matches(@"\.status-card-fullscreen \.status-card-body ::deep p\s*\{[^}]*color:\s*var\(--app-on-accent\)", css);
        Assert.False(File.Exists(Path.Combine(SourceTree.Client, "Views", "Auths", "Pending.razor.css")), "帳號待審核頁改用 StatusCard，不要再自己寫一份卡片樣式。");
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
