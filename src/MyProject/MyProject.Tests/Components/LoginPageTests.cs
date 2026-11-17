using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Shell;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Client.Layout;
using MyProject.Web.Client.Views.Auths;

namespace MyProject.Tests.Components;

/// <summary>
/// 登入頁（0.10.8 起在 WASM，WASM 遷移 M9b）：保留 Server 版的元素 id 與隱藏欄位（實機腳本沿用）、固定的 sso 訊息、
/// 驗證碼在瀏覽器比對、登入成功先在登入頁確認身分並讀好外框資料、再依 returnUrl 在程式內換頁（0.10.35 起；以前整頁導過去）、需要第二步時帶到兩步驟驗證頁。API 行為見 WebTokenLoginApiTests。
/// </summary>
public sealed class LoginPageTests : ComponentTestBase
{
    private readonly FakeAuthHandler handler = new();

    public LoginPageTests()
    {
        handler.Respond("api/v1/Auth/login-settings", HttpStatusCode.OK, ApiResult<LoginPageSettingsDto>.SuccessResult(new LoginPageSettingsDto
        {
            SystemName = "測試系統",
            SystemDescription = "簡介",
            GoogleEnabled = false,
            EmailEnabled = true,
            MaxFailedAttempts = 5,
            LockoutMinutes = 15,
        }));

        Services.AddLogging();
        // 以物件註冊：容器不負責釋放（TokenStore 只實作 IAsyncDisposable，bUnit 以同步方式釋放容器）。
        Services.AddSingleton(new TokenStore(JSInterop.JSRuntime));
        Services.AddSingleton(sp => new ClientSession(Http(), sp.GetRequiredService<TokenStore>(), sp.GetRequiredService<NavigationManager>(), NullLogger<ClientSession>.Instance));
        Services.AddSingleton(sp => new ApiClient(Http(), sp.GetRequiredService<ClientSession>(), sp.GetRequiredService<NavigationManager>(), NullLogger<ApiClient>.Instance));
        Services.AddSingleton<LoginPageSettingsSource>();
        Services.AddSingleton(sp => new ClientShellData(sp.GetRequiredService<ApiClient>()));
        // API 主機的網址（0.10.10 起）：這裡模擬雙站台，前端在 localhost、API 在 api.example.com。
        Services.AddSingleton(ApiAddress.Resolve("https://api.example.com/", "https://localhost/"));
    }

    /// <summary>
    /// Google 登入是整頁導到 API 主機（0.10.10 起，雙站台時 API 在另一個網址）：連結一定要帶上 <c>ApiBaseUrl</c>，
    /// 否則會導到前端靜態站，只拿到 index.html。
    /// </summary>
    [Fact]
    public void GoogleLoginLink_ShouldPointAtTheApiHost()
    {
        handler.Respond("api/v1/Auth/login-settings", HttpStatusCode.OK, ApiResult<LoginPageSettingsDto>.SuccessResult(new LoginPageSettingsDto
        {
            SystemName = "測試系統",
            GoogleEnabled = true,
            MaxFailedAttempts = 5,
            LockoutMinutes = 15,
        }));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/Auths/Login?returnUrl=%2Fprojects");

        var page = Render<Login>();

        page.WaitForAssertion(() => Assert.StartsWith(
            "https://api.example.com/Auths/Google/Login?returnUrl=%2Fprojects&binding=",
            page.Find("a.google-login-button").GetAttribute("href")));
    }

    [Fact]
    public void ShouldKeepTheElementsTheRealMachineScriptsUse()
    {
        var page = Render<Login>();
        page.WaitForElement("#account");

        Assert.NotNull(page.Find("#password"));
        Assert.NotNull(page.Find("#captcha"));
        Assert.NotNull(page.Find("#rememberMe"));
        Assert.NotNull(page.Find("button.login-button"));
        var captcha = page.Find("input[name=\"Input.CaptchaCode\"]").GetAttribute("value");
        Assert.Matches("^[0-9]{4}$", captcha);
        Assert.Equal(captcha, page.Find(".captcha-display").TextContent.Trim());
        Assert.Contains("連續輸錯 5 次密碼會暫時鎖定 15 分鐘；被鎖定時可以用「忘記密碼」重設，或請系統管理員解鎖。", page.Markup);
        Assert.NotNull(page.Find("a[href=\"/Auths/ForgotPassword\"]"));
        Assert.Contains("測試系統", page.Markup);
    }

    [Fact]
    public void SsoDeleted_ShouldShowTheFixedMessage_AndUnknownCodesShouldNotBeEchoed()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/Auths/Login?sso=deleted");
        var deleted = Render<Login>();
        deleted.WaitForElement("#account");
        Assert.Contains("此帳號已被刪除，請洽系統管理員。", deleted.Markup);

        navigation.NavigateTo("/Auths/Login?sso=" + Uri.EscapeDataString("<b>injected-marker</b>"));
        var unknown = Render<Login>();
        unknown.WaitForElement("#account");
        Assert.DoesNotContain("injected-marker", unknown.Markup);
        Assert.DoesNotContain("此帳號已被刪除", unknown.Markup);
    }

    [Fact]
    public void WrongCaptcha_ShouldBeRejectedInTheBrowser_WithoutCallingTheServer()
    {
        var page = Render<Login>();
        page.WaitForElement("#account");
        page.Find("#account").Change("alice");
        page.Find("#password").Change("secret");
        page.Find("#captcha").Change("wrong");

        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("驗證碼錯誤", page.Markup));
        Assert.DoesNotContain("api/v1/Auth/login/web", handler.Paths);
    }

    [Fact]
    public void SuccessfulLogin_ShouldStoreTheTokensAndGoToTheReturnUrlWithoutAFullLoad()
    {
        var tokens = new TokenResponseDto { AccessToken = "a1", RefreshToken = "r1", AccessTokenExpiresAt = DateTime.UtcNow.AddMinutes(15), RefreshTokenExpiresAt = DateTime.UtcNow.AddDays(30), IsPersistent = true, User = new CurrentUserDto { Id = 7 } };
        handler.Respond("api/v1/Auth/login/web", HttpStatusCode.OK, ApiResult<WebLoginResultDto>.SuccessResult(new WebLoginResultDto
        {
            Tokens = tokens,
            ReturnUrl = "/projects",
        }));
        handler.Respond("api/v1/Auth/me", HttpStatusCode.OK, ApiResult<SessionInfoDto>.SuccessResult(new SessionInfoDto { Id = 7, Account = "alice" }));
        handler.Respond("api/v1/shell/info", HttpStatusCode.OK, ApiResult<ShellInfoDto>.SuccessResult(new ShellInfoDto { SystemName = "測試系統" }));
        handler.Respond("api/v1/shell/menu", HttpStatusCode.OK, ApiResult<ShellMenuDto>.SuccessResult(new ShellMenuDto()));
        // 瀏覽器裡剛存好的 Token（bUnit 沒有真的 storage）。
        JSInterop.SetupModule("./js/client-auth.js").Setup<string?>("read").SetResult(TokenStore.Serialize(new StoredTokens("a1", tokens.AccessTokenExpiresAt, "r1", tokens.RefreshTokenExpiresAt, true, 7)));
        var navigation = (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/Auths/Login?returnUrl=%2Fprojects");
        var page = Render<Login>();
        page.WaitForElement("#account");
        page.Find("#account").Change("alice");
        page.Find("#password").Change("secret");
        page.Find("#captcha").Change(page.Find("input[name=\"Input.CaptchaCode\"]").GetAttribute("value")!);
        page.Find("#rememberMe").Change(true);

        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.EndsWith("/projects", navigation.Uri));
        var request = JsonSerializer.Deserialize<WebLoginRequestDto>(handler.Bodies["api/v1/Auth/login/web"], JsonSerializerOptions.Web)!;
        Assert.Equal("alice", request.Account);
        Assert.True(request.RememberMe);
        Assert.Equal("/projects", request.ReturnUrl);
        // 0.10.35 起登入成功不再整頁重新載入（登入頁本來就是剛整頁載入的），改在程式內換頁。
        Assert.False(navigation.History.First().Options.ForceLoad);
        // 換頁之前已在登入頁確認身分、讀好外框資料（外框不必再顯示「正在確認登入狀態…」）。
        Assert.Equal("alice", Services.GetRequiredService<ClientSession>().Info!.Account);
        Assert.Contains("api/v1/shell/menu", handler.Paths);
        Assert.NotNull(Services.GetRequiredService<ClientShellData>().TakePrepared());
    }

    [Fact]
    public void WhenASecondFactorIsNeeded_ShouldGoToTheTwoFactorPage()
    {
        handler.Respond("api/v1/Auth/login/web", HttpStatusCode.OK, ApiResult<WebLoginResultDto>.SuccessResult(new WebLoginResultDto
        {
            RequiresTwoFactor = true,
            TwoFactorTicket = "ticket",
            RememberDeviceDays = 30,
            ReturnUrl = "/App",
        }));
        var navigation = Services.GetRequiredService<NavigationManager>();
        var page = Render<Login>();
        page.WaitForElement("#account");
        page.Find("#account").Change("alice");
        page.Find("#password").Change("secret");
        page.Find("#captcha").Change(page.Find("input[name=\"Input.CaptchaCode\"]").GetAttribute("value")!);

        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.EndsWith("/Auths/TwoFactor", navigation.Uri));
    }

    [Fact]
    public void FailedLogin_ShouldShowTheServerMessage_AndANewCaptcha()
    {
        handler.Respond("api/v1/Auth/login/web", HttpStatusCode.Unauthorized, ApiResult<WebLoginResultDto>.UnauthorizedResult("帳號或密碼不正確，或帳號已被暫時鎖定。"));
        var page = Render<Login>();
        page.WaitForElement("#account");
        page.Find("#account").Change("alice");
        page.Find("#password").Change("wrong");
        page.Find("#captcha").Change(page.Find("input[name=\"Input.CaptchaCode\"]").GetAttribute("value")!);

        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("帳號或密碼不正確，或帳號已被暫時鎖定。", page.Markup));
        Assert.Equal(string.Empty, page.Find("#captcha").GetAttribute("value") ?? string.Empty);
    }

    private HttpClient Http() => new(handler) { BaseAddress = new Uri("https://localhost/") };

    private sealed class FakeAuthHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, object Body)> responses = new(StringComparer.Ordinal);

        public List<string> Paths { get; } = [];

        public Dictionary<string, string> Bodies { get; } = new(StringComparer.Ordinal);

        public void Respond(string path, HttpStatusCode status, object body)
        {
            lock (responses)
            {
                responses[path] = (status, body);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            (HttpStatusCode Status, object Body) response;
            lock (responses)
            {
                Paths.Add(path);
                Bodies[path] = body;
                response = responses.TryGetValue(path, out var found) ? found : (HttpStatusCode.NotFound, ApiResult<object>.NotFoundResult());
            }

            return new HttpResponseMessage(response.Status)
            {
                Content = JsonContent.Create(response.Body, response.Body.GetType(), options: JsonSerializerOptions.Web),
            };
        }
    }
}
