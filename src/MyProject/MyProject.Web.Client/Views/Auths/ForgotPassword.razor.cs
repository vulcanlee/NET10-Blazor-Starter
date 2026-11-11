using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MyProject.Dtos.Auths;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Components.Auths;

namespace MyProject.Web.Client.Views.Auths;

/// <summary>
/// 忘記密碼（0.9.60 起；0.10.8 起在 WASM 執行，WASM 遷移 M9）：輸入帳號或 Email，經 <c>POST api/v1/Auth/password/forgot</c> 申請重設信。
/// 不論帳號是否存在都顯示同一句話；只有輸入內建的 support 時伺服器明說不提供（速查表 §6.13）。
/// </summary>
public partial class ForgotPassword
{
    private LoginPageSettingsDto? settings;
    private string message = string.Empty;
    private string captchaCode = string.Empty;
    private bool isSubmitted;
    private bool isBusy;

    [Inject]
    public ApiClient Api { get; set; } = default!;

    [Inject]
    public LoginPageSettingsSource SettingsSource { get; set; } = default!;

    [Inject]
    public ILogger<ForgotPassword> Logger { get; set; } = default!;

    private InputModel Input { get; } = new();

    protected override async Task OnInitializedAsync()
    {
        RefreshCaptcha();
        settings = await SettingsSource.GetAsync();
    }

    private async Task SubmitAsync()
    {
        message = string.Empty;
        if (settings?.EmailEnabled != true || isBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Input.Identifier))
        {
            message = "請輸入帳號或 Email";
            return;
        }

        if (string.IsNullOrWhiteSpace(Input.CaptchaInput))
        {
            message = "請輸入驗證碼";
            return;
        }

        if (!string.Equals(Input.CaptchaInput.Trim(), Input.CaptchaCode, StringComparison.Ordinal))
        {
            message = "驗證碼錯誤";
            RefreshCaptcha();
            Input.CaptchaInput = string.Empty;
            Logger.LogInformation("Forgot password submission rejected because captcha is invalid.");
            return;
        }

        isBusy = true;
        try
        {
            var result = await Api.PostAnonymousAsync<object>("api/v1/Auth/password/forgot", new ForgotPasswordRequestDto { Identifier = Input.Identifier });
            if (result.Success)
            {
                isSubmitted = true;
                return;
            }

            // 400 是伺服器明說的原因（輸入 support、寄信未啟用）；其他（伺服器錯誤、連不上）照 Server 版的固定說法。
            message = result.StatusCode == 400 ? result.UserMessage() : "系統暫時無法處理您的申請，請稍後再試。";
            RefreshCaptcha();
            Input.CaptchaInput = string.Empty;
        }
        finally
        {
            isBusy = false;
        }
    }

    private void RefreshCaptcha()
    {
        captchaCode = AuthCaptcha.Generate();
        Input.CaptchaCode = captchaCode;
    }

    private sealed class InputModel
    {
        public string Identifier { get; set; } = string.Empty;

        public string CaptchaInput { get; set; } = string.Empty;

        public string CaptchaCode { get; set; } = string.Empty;
    }
}
