using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Web.Configuration;
using MyProject.Web.Email;

namespace MyProject.Web.Components.Auths
{
    /// <summary>
    /// 忘記密碼（匿名、靜態 SSR 表單）。
    ///
    /// ⚠️ 不論帳號存不存在，送出後都顯示同一句話；是否寄信、為什麼沒寄，只記在稽核裡。
    /// ⚠️ 日誌不可記錄使用者輸入的帳號或 Email —— 那正是有人想試探的東西。
    /// </summary>
    public partial class ForgotPassword
    {
        private string message = string.Empty;
        private string captchaCode = string.Empty;
        private bool isSubmitted;

        [CascadingParameter]
        private HttpContext HttpContext { get; set; } = default!;

        [SupplyParameterFromForm]
        private InputModel Input { get; set; } = default!;

        [Inject]
        public PasswordResetService PasswordResetService { get; set; } = default!;

        [Inject]
        public IOptions<EmailSettings> EmailOptions { get; set; } = default!;

        [Inject]
        public IOptions<PasswordResetSettings> ResetOptions { get; set; } = default!;

        [Inject]
        public IHostEnvironment HostEnvironment { get; set; } = default!;

        [Inject]
        public ILogger<ForgotPassword> Logger { get; set; } = default!;

        private bool IsEmailEnabled => EmailOptions.Value.TryGetProvider(out var provider) && provider != EmailProvider.None;

        private int LifetimeMinutes => ResetOptions.Value.TokenLifetimeMinutes;

        protected override void OnInitialized()
        {
            Input ??= new();

            if (string.IsNullOrWhiteSpace(Input.CaptchaCode))
            {
                RefreshCaptcha();
            }
            else
            {
                captchaCode = Input.CaptchaCode;
            }
        }

        private async Task SubmitAsync()
        {
            message = string.Empty;

            if (!IsEmailEnabled)
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

            var baseUrl = PublicBaseUrlResolver.Resolve(EmailOptions.Value, HttpContext.Request, HostEnvironment);
            if (baseUrl is null)
            {
                // Production 用 Smtp 時啟動檢查已要求 PublicBaseUrl，走到這裡代表設定被改壞了。
                // 仍顯示同一句話：這不是使用者能處理的問題，也不該讓畫面透露任何差異。
                Logger.LogError("Password reset link cannot be built because EmailSettings:PublicBaseUrl is not configured.");
                isSubmitted = true;
                return;
            }

            try
            {
                await PasswordResetService.RequestAsync(Input.Identifier, $"{baseUrl}/Auths/ResetPassword");
                isSubmitted = true;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Forgot password request failed unexpectedly.");
                message = "系統暫時無法處理您的申請，請稍後再試。";
                RefreshCaptcha();
                Input.CaptchaInput = string.Empty;
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
}
