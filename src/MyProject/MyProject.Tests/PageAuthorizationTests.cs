using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Mvc.Testing;
using MyProject.Web.Client.Layout;
using MyProject.Web.Components.Auths;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;

namespace MyProject.Tests;

/// <summary>
/// 守住「未登入的人看不到任何頁面內容」這件事（0.9.41 起；0.10.8 起純 Token 登入，WASM 遷移 M9b）。
///
/// 0.10.8 起伺服器不發登入 Cookie，整頁請求時伺服器不知道誰在登入，所以頁面**不再**標 [Authorize]（預設驗證機制是 JWT，
/// 標了反而會讓整頁請求拿到 401 JSON）。防線改成：
/// 1. 整頁請求只回空的 WASM 外殼（不預先渲染），伺服器回應裡沒有任何頁面內容。
/// 2. ClientMainLayout 是閘門：登入檢查（Token、/me）通過前不渲染任何東西，失敗就帶 returnUrl 回登入頁。
/// 3. 不經 ClientMainLayout 的頁面（自己指定別的版面）必須是匿名頁並列在白名單；所有頁面都不可標 [Authorize]。
/// 4. 資料一律經 API，API 各自以 Token 授權（ApiAuthorizationConventionTests）。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class PageAuthorizationTests : IClassFixture<ApiTestApplicationFactory>
{
    /// <summary>
    /// 允許匿名存取的頁面（不經 ClientMainLayout 的閘門，或本身就要能在未登入時顯示）。⚠️ 新增項目前請確認該頁面**不含任何需要登入才能看的內容**。
    /// </summary>
    private static readonly string[] AnonymousPages =
    [
        "SplashPage",     // "/" 啟動頁，只有品牌與載入中動畫
        "ErrorPage",      // 例外處理頁，追蹤碼由網址帶入
        "NotFoundPage",   // 狀態碼頁的重跑目標；內容靠 ClientMainLayout 的閘門擋住
        "Login",
        "Logout",         // 直接開也能登出（呼叫登出 API、清 Token）
        "Pending",        // Google 登入後等待審核
        "ForgotPassword", // 忘記密碼：只有輸入框，送出後一律顯示同一句話
        "ResetPassword",  // 以信中連結重設密碼：連結無效時只顯示「連結無效」
        "TwoFactor",      // 兩步驟驗證第二步：沒有待驗票證一律回登入頁
    ];

    private readonly ApiTestApplicationFactory factory;

    public PageAuthorizationTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public void NoPage_ShouldCarryAuthorize()
    {
        var pages = GetRoutablePages();
        Assert.True(pages.Count > 30, $"只找到 {pages.Count} 個頁面，掃描可能失效。");

        var withAuthorize = pages
            .Where(page => page.GetCustomAttribute<AuthorizeAttribute>() is not null)
            .Select(page => page.Name)
            .ToList();

        Assert.True(withAuthorize.Count == 0,
            "0.10.8 起頁面不可標 [Authorize]（整頁請求沒有登入資訊，預設驗證機制是 JWT，標了會讓整頁請求拿到 401）；登入由 ClientMainLayout 把關："
                + string.Join("、", withAuthorize));
    }

    [Fact]
    public void AnonymousPages_ShouldMatchTheAllowList()
    {
        var anonymous = GetRoutablePages()
            .Where(page => page.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
            .Select(page => page.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(AnonymousPages.Order(StringComparer.Ordinal), anonymous);
    }

    /// <summary>不是匿名頁的頁面一律經 ClientMainLayout（預設版面或明寫），閘門才擋得到。</summary>
    [Fact]
    public void ProtectedPages_ShouldGoThroughTheClientMainLayoutGate()
    {
        var bypassing = GetRoutablePages()
            .Where(page => page.GetCustomAttribute<AllowAnonymousAttribute>() is null)
            .Where(page => page.GetCustomAttribute<LayoutAttribute>() is { } layout && layout.LayoutType != typeof(ClientMainLayout))
            .Select(page => page.Name)
            .ToList();

        Assert.True(bypassing.Count == 0, "以下頁面不是匿名頁，卻指定了別的版面（繞過登入閘門）：" + string.Join("、", bypassing));
    }

    public static TheoryData<string> ProtectedRoutes()
    {
        var data = new TheoryData<string>();
        foreach (var route in GetRoutablePages()
            .Where(page => page.GetCustomAttribute<AllowAnonymousAttribute>() is null)
            .SelectMany(page => page.GetCustomAttributes<RouteAttribute>())
            .Select(attribute => attribute.Template)
            .Where(template => !template.Contains('{'))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            data.Add(route);
        }

        return data;
    }

    /// <summary>未登入的整頁請求只拿到空的 WASM 外殼（不預先渲染），回應裡沒有任何頁面內容；登入檢查在瀏覽器裡由 ClientMainLayout 做。</summary>
    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task ProtectedPage_WithoutLogin_ShouldServeOnlyTheEmptyWasmShell(string route)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync(route);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("webassembly", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("class=\"sidebar\"", body);
        Assert.DoesNotContain("top-row", body);
    }

    /// <summary>
    /// 不存在的網址維持 404（API 呼叫端要靠它），回應裡也沒有任何頁面內容。
    /// </summary>
    [Fact]
    public async Task UnknownUrl_WithoutLogin_ShouldKeep404AndRenderNoPageContent()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/no-such-page");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Not Found", body);
        Assert.DoesNotContain("sidebar", body);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Error?trace=ABCD1234")]
    [InlineData("/Auths/Login")]
    [InlineData("/Auths/Login?returnUrl=%2Fprojects")]
    [InlineData("/Auths/TwoFactor")]
    [InlineData("/Auths/Pending")]
    [InlineData("/Auths/Logout")]
    [InlineData("/Auths/ForgotPassword")]
    [InlineData("/Auths/ResetPassword")]
    public async Task AnonymousPage_WithoutLogin_ShouldBeServed(string route)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync(route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/projects", "/projects")]
    [InlineData("/App?tab=1", "/App?tab=1")]
    [InlineData("/", "/")]
    [InlineData(null, "/App")]
    [InlineData("", "/App")]
    [InlineData("https://evil.com", "/App")]
    [InlineData("//evil.com", "/App")]
    [InlineData("/\\evil.com", "/App")]
    [InlineData("/\t/evil.com", "/App")]
    [InlineData("evil.com", "/App")]
    public void ReturnUrlGuard_ShouldOnlyAllowLocalPaths(string? input, string expected)
    {
        Assert.Equal(expected, ReturnUrlGuard.Sanitize(input));
    }

    private static readonly Regex AuthenticationCheckCall = new(
        @"AuthenticationStateHelper\s*\.\s*Check\(",
        RegexOptions.Compiled);

    /// <summary>
    /// 不需要 isAccessChecked 閘門的元件（0.10.8 起啟動頁與變更密碼頁搬到 WASM 之後已經沒有）。
    /// </summary>
    private static readonly string[] CheckWithoutGateAllowList = [];

    [Fact]
    public void ComponentsCallingAuthenticationCheck_ShouldRenderNothingUntilTheCheckCompletes()
    {
        var componentsRoot = FindComponentsRoot();

        // 防空跑：比對式本身還認得出 Check() 的呼叫（Server 版外框仍會呼叫）。
        Assert.Matches(AuthenticationCheckCall, File.ReadAllText(Path.Combine(componentsRoot, "Layout", "MainLayout.razor.cs")));

        var sourceFiles = new[] { "Views", "Pages" }
            .Select(folder => Path.Combine(componentsRoot, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.razor*", SearchOption.AllDirectories))
            .Where(file => file.EndsWith(".razor", StringComparison.Ordinal)
                || file.EndsWith(".razor.cs", StringComparison.Ordinal))
            .Where(file => Path.GetFileName(file) != "_Imports.razor")
            .ToList();

        var components = sourceFiles
            .Where(file => AuthenticationCheckCall.IsMatch(File.ReadAllText(file)))
            .Select(file => file.EndsWith(".razor.cs", StringComparison.Ordinal) ? file[..^3] : file)
            .Distinct()
            .Where(markup => !CheckWithoutGateAllowList.Contains(Path.GetFileNameWithoutExtension(markup)))
            .ToList();

        // WASM 遷移期間 Server 版頁面逐步搬走（0.10.6 起 M7 剩 4 個、0.10.7 起 M8 剩兩步驟驗證設定、0.10.8 起 M9 一個都不剩）：
        // Server 的頁面與檢視已沒有呼叫 Check() 的元件，這條規則留著守門日後新增的 Server 元件（M10 移除 Server 模式時一併刪除）。

        var violations = new List<string>();
        foreach (var markup in components)
        {
            var codeBehind = markup + ".cs";
            var code = File.ReadAllText(markup)
                + (File.Exists(codeBehind) ? File.ReadAllText(codeBehind) : string.Empty);

            if (!File.ReadAllText(markup).Contains("!isAccessChecked", StringComparison.Ordinal)
                || !code.Contains("isAccessChecked = true", StringComparison.Ordinal))
            {
                violations.Add(Path.GetFileName(markup));
            }
        }

        Assert.True(
            violations.Count == 0,
            "呼叫 AuthenticationStateHelper.Check() 的元件，在檢查完成前不得渲染任何內容 ——"
                + "否則沒有權限的人會先看到工具列與表格框架，才換成「你沒有權限存取此頁面」。"
                + "請比照既有檢視：宣告 isAccessChecked，權限判定完成（通過或拒絕）後設為 true，"
                + "markup 最外層以 @if (!isAccessChecked) { } 擋住。"
                + Environment.NewLine
                + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void MainLayout_ShouldRenderNothingUntilAuthenticated()
    {
        var markup = File.ReadAllText(Path.Combine(FindComponentsRoot(), "Layout", "MainLayout.razor"));

        Assert.Contains("@if (isAuthenticated)", markup);
    }

    /// <summary>
    /// ⭐ 0.10.8 起這就是登入的閘門（伺服器不再以 [Authorize] 擋整頁請求）：ClientMainLayout 在 <c>Session.InitializeAsync()</c>
    /// 通過之前不渲染任何東西（側邊欄、頁首、頁面本體），失敗就由 ClientSession 帶 returnUrl 回登入頁。
    /// </summary>
    [Fact]
    public void ClientMainLayout_ShouldRenderNothingUntilTheSessionIsInitialized()
    {
        var markup = File.ReadAllText(Path.Combine(SourceTree.Client, "Layout", "ClientMainLayout.razor"));
        var code = File.ReadAllText(Path.Combine(SourceTree.Client, "Layout", "ClientMainLayout.razor.cs"));

        Assert.StartsWith("@if (isAuthenticated)", markup[markup.IndexOf("@if (", StringComparison.Ordinal)..]);
        var initialize = code.IndexOf("if (!await Session.InitializeAsync())", StringComparison.Ordinal);
        Assert.True(initialize >= 0, "ClientMainLayout 必須先呼叫 Session.InitializeAsync() 並在失敗時停止。");
        Assert.True(code.IndexOf("isAuthenticated = true", StringComparison.Ordinal) > initialize, "isAuthenticated 只能在登入檢查通過之後設為 true。");
    }

    /// <summary>Server（MyProject.Web）與 WASM（MyProject.Web.Client，0.10.1 起）兩個組件裡的所有頁面。</summary>
    private static List<Type> GetRoutablePages()
    {
        return new[] { typeof(MyProject.Web.Program).Assembly, typeof(MyProject.Web.Client.ClientRouteTable).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => typeof(IComponent).IsAssignableFrom(type)
                && type.GetCustomAttributes<RouteAttribute>().Any())
            .ToList();
    }

    private static string FindComponentsRoot() => SourceTree.Components;
}
