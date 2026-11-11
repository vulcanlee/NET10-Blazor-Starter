using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MyProject.Dtos.Auths;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Components.Auths;

namespace MyProject.Web.Client.Views.Auths;

/// <summary>
/// 登入頁（0.10.8 起在 WASM 執行，WASM 遷移 M9；畫面、文字、元素 id 與隱藏欄位照抄 Server 版）。
/// <list type="bullet">
/// <item>帳號密碼經 <c>POST api/v1/Auth/login/web</c> 換 Token，依「記住我」存 localStorage 或 sessionStorage，再整頁導到 returnUrl（經 <see cref="ReturnUrlGuard"/>）。</item>
/// <item>帳號已啟用兩步驟驗證：記住過的裝置（localStorage 的記住裝置 Token）直接完成，否則把待驗票證存在這個分頁、帶到 <c>/Auths/TwoFactor</c>。</item>
/// <item>Google：按鈕帶這個分頁產生的驗證值雜湊；回呼導回 <c>#code=</c> 時以原值兌換（<c>POST api/v1/Auth/google/exchange</c>）。</item>
/// <item>驗證碼 4 碼、答案就在頁面上（不是防機器人），真正的防線是伺服器的登入配額與帳號鎖定。</item>
/// </list>
/// ⚠️ 開登入頁不清 storage（以前的 auth-reset.js）：不可把其他分頁登出；登入成功時寫入新的 Token，其他分頁發現換人會重新載入。
/// </summary>
public partial class Login
{
    private const string CodeFragment = "#code=";

    private LoginPageSettingsDto? settings;
    private string captchaCode = string.Empty;
    private string message = string.Empty;
    private string errorMessage = string.Empty;
    private string? googleLoginUrl;
    private bool isBusy;
    private bool isExchanging;

    [Inject]
    public ApiClient Api { get; set; } = default!;

    [Inject]
    public ClientSession Session { get; set; } = default!;

    [Inject]
    public TokenStore Store { get; set; } = default!;

    [Inject]
    public LoginPageSettingsSource SettingsSource { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    public ILogger<Login> Logger { get; set; } = default!;

    [SupplyParameterFromQuery(Name = "returnUrl")]
    private string? ReturnUrl { get; set; }

    /// <summary>重設密碼成功後導回本頁時帶 <c>?reset=1</c>，用來顯示成功訊息。</summary>
    [SupplyParameterFromQuery(Name = "reset")]
    private string? Reset { get; set; }

    /// <summary>
    /// Google 登入被拒時導回本頁帶 <c>?sso=代碼</c>（0.9.95 起）。只認得固定代碼、對應固定文字，
    /// 其他值一律忽略 —— 不把網址內容顯示到頁面上。
    /// </summary>
    [SupplyParameterFromQuery(Name = "sso")]
    private string? Sso { get; set; }

    private InputModel Input { get; } = new();

    /// <summary>
    /// 登入失敗的訊息刻意不分「帳號不存在／密碼錯／鎖定中」（0.9.101 起），所以把鎖定規則固定寫在頁面上，
    /// 被鎖住的人才知道可以等、重設密碼或找管理員。
    /// </summary>
    private string LockoutHint
    {
        get
        {
            var way = settings?.EmailEnabled == true ? "可以用「忘記密碼」重設，或請系統管理員解鎖" : "請稍後再試，或請系統管理員解鎖";
            return $"連續輸錯 {settings?.MaxFailedAttempts} 次密碼會暫時鎖定 {settings?.LockoutMinutes} 分鐘；被鎖定時{way}。";
        }
    }

    /// <summary>只在還沒送出登入表單時顯示；登入失敗的錯誤訊息優先。</summary>
    private bool ShowResetSucceeded => Reset == "1" && string.IsNullOrEmpty(message);

    /// <summary>只在還沒送出登入表單時顯示；登入失敗的錯誤訊息優先。</summary>
    private string? SsoFailureMessage => !string.IsNullOrEmpty(message)
        ? null
        : Sso switch
        {
            "deleted" => "此帳號已被刪除，請洽系統管理員。",
            "locked" => "此帳號因連續輸錯密碼已暫時鎖定，請稍後再試，或請系統管理員解鎖。",
            "account-taken" => "這個 Email 已經是其他使用者的帳號名稱，無法自動建立帳號。如果那就是你的帳號，請系統管理員在帳號資料補上這個 Email，再用 Google 登入即可連結。",
            _ => null,
        };

    protected override async Task OnInitializedAsync()
    {
        RefreshCaptcha();
        var code = ReadGoogleCode();
        isExchanging = code is not null;
        settings = await SettingsSource.GetAsync();

        if (code is not null)
        {
            // 網址片段裡的代碼用過就拿掉（不留在瀏覽器歷史）。
            NavigationManager.NavigateTo(NavigationManager.Uri.Split('#')[0], replace: true);
            if (await CompleteGoogleAsync(code))
            {
                return;
            }

            isExchanging = false;
        }

        if (settings.GoogleEnabled)
        {
            var binding = await LoginStorage.NewGoogleBindingAsync(Store);
            var returnUrl = string.IsNullOrWhiteSpace(ReturnUrl) ? null : ReturnUrlGuard.Sanitize(ReturnUrl);
            googleLoginUrl = returnUrl is null
                ? $"/Auths/Google/Login?binding={binding}"
                : $"/Auths/Google/Login?returnUrl={Uri.EscapeDataString(returnUrl)}&binding={binding}";
        }

        Logger.LogDebug("Login page initialized.");
    }

    public async Task LoginUser()
    {
        message = string.Empty;
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(Input.Account))
        {
            message = "請輸入帳號";
            errorMessage = "alert-danger";
            return;
        }

        if (string.IsNullOrWhiteSpace(Input.Password))
        {
            message = "請輸入密碼";
            errorMessage = "alert-danger";
            return;
        }

        if (string.IsNullOrWhiteSpace(Input.CaptchaInput))
        {
            message = "請輸入驗證碼";
            errorMessage = "alert-danger";
            return;
        }

        if (!string.Equals(Input.CaptchaInput.Trim(), Input.CaptchaCode, StringComparison.Ordinal))
        {
            message = "驗證碼錯誤";
            errorMessage = "alert-danger";
            RefreshCaptcha();
            Input.CaptchaInput = string.Empty;
            return;
        }

        isBusy = true;
        try
        {
            var result = await Api.PostAnonymousAsync<WebLoginResultDto>("api/v1/Auth/login/web", new WebLoginRequestDto
            {
                Account = Input.Account,
                Password = Input.Password,
                RememberMe = Input.RememberMe,
                RememberDeviceToken = await LoginStorage.ReadDeviceTokenAsync(Store),
                ReturnUrl = ReturnUrl,
            });

            if (result.Success && result.Data is { } data && await ContinueAsync(data))
            {
                return;
            }

            message = string.IsNullOrWhiteSpace(result.UserMessage()) ? "登入失敗，請重新確認帳號與密碼。" : result.UserMessage();
            RefreshCaptcha();
            Input.CaptchaInput = string.Empty;
        }
        finally
        {
            isBusy = false;
        }

        errorMessage = string.IsNullOrEmpty(message) ? string.Empty : "alert-danger";
    }

    /// <summary>登入或 Google 兌換成功之後：有 Token 就存起來整頁導過去；要第二步就帶到兩步驟驗證頁。處理了回 true。</summary>
    private async Task<bool> ContinueAsync(WebLoginResultDto data)
    {
        if (data.Tokens is { } tokens)
        {
            await Session.SignedInAsync(tokens);
            Logger.LogInformation("Login succeeded. UserId={UserId}", tokens.User.Id);
            NavigationManager.NavigateTo(ReturnUrlGuard.Sanitize(data.ReturnUrl), forceLoad: true);
            return true;
        }

        if (data.RequiresTwoFactor && !string.IsNullOrEmpty(data.TwoFactorTicket))
        {
            await LoginStorage.WritePendingAsync(Store, new PendingTwoFactor(data.TwoFactorTicket, data.RememberDeviceDays, ReturnUrlGuard.Sanitize(data.ReturnUrl)));
            NavigationManager.NavigateTo("/Auths/TwoFactor");
            return true;
        }

        return false;
    }

    private async Task<bool> CompleteGoogleAsync(string code)
    {
        var result = await Api.PostAnonymousAsync<WebLoginResultDto>("api/v1/Auth/google/exchange", new GoogleExchangeRequestDto
        {
            Code = code,
            Verifier = await LoginStorage.ReadGoogleVerifierAsync(Store) ?? string.Empty,
            RememberDeviceToken = await LoginStorage.ReadDeviceTokenAsync(Store),
        });

        if (result.Success && result.Data is { } data && await ContinueAsync(data))
        {
            return true;
        }

        message = result.UserMessage();
        errorMessage = "alert-danger";
        Logger.LogInformation("Google login code could not be exchanged. Status={Status}", result.StatusCode);
        return false;
    }

    private string? ReadGoogleCode()
    {
        var uri = NavigationManager.Uri;
        var index = uri.IndexOf(CodeFragment, StringComparison.Ordinal);
        return index < 0 ? null : Uri.UnescapeDataString(uri[(index + CodeFragment.Length)..]);
    }

    private void RefreshCaptcha()
    {
        captchaCode = AuthCaptcha.Generate();
        Input.CaptchaCode = captchaCode;
    }

    private sealed class InputModel
    {
        public string Account { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;

        public bool RememberMe { get; set; }

        public string CaptchaInput { get; set; } = string.Empty;

        public string CaptchaCode { get; set; } = string.Empty;
    }
}
