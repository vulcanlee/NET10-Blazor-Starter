using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Mvc.Testing;
using MyProject.Web.Client.Layout;
using MyProject.Web.Components.Auths;
using System.Net;
using System.Reflection;

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
    /// 不存在的網址（0.10.10 起）：不是 API 的一律拿到前端的入口頁（SPA 回退，由前端路由顯示找不到頁面），回應裡沒有任何頁面內容；
    /// API 底下不存在的路由維持 404（API 呼叫端要靠它，見 SameSiteHostTests）。
    /// </summary>
    [Fact]
    public async Task UnknownUrl_WithoutLogin_ShouldServeOnlyTheEmptyShell_AndUnknownApisShouldKeep404()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/no-such-page");
        var body = await response.Content.ReadAsStringAsync();
        var api = await client.GetAsync("/api/v1/no-such-page");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<div id=\"app\"></div>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("sidebar", body);
        Assert.Equal(HttpStatusCode.NotFound, api.StatusCode);
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

    /// <summary>兩個組件裡的所有頁面：0.10.9 起頁面只在 WASM（MyProject.Web.Client），主機組件一併掃描，有人把頁面加回主機時也會被這裡的規則涵蓋（另見 ApiOnlyHostTests）。</summary>
    private static List<Type> GetRoutablePages()
    {
        return new[] { typeof(MyProject.Web.Program).Assembly, typeof(MyProject.Web.Client.ClientRouteTable).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => typeof(IComponent).IsAssignableFrom(type)
                && type.GetCustomAttributes<RouteAttribute>().Any())
            .ToList();
    }
}
