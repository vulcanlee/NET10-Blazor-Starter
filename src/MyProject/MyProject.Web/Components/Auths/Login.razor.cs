using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Auth;
using MyProject.Web.Configuration;
using System.Security.Claims;

namespace MyProject.Web.Components.Auths
{
    public partial class Login
    {
        string errorMessage = string.Empty;
        string captchaCode = string.Empty;

        [CascadingParameter]
        private HttpContext HttpContext { get; set; } = default!;

        [SupplyParameterFromForm]
        private InputModel Input { get; set; } = default!;

        [SupplyParameterFromQuery]
        private string? ReturnUrl { get; set; }

        /// <summary>重設密碼成功後導回本頁時帶 <c>?reset=1</c>，用來顯示成功訊息。</summary>
        [SupplyParameterFromQuery(Name = "reset")]
        private string? Reset { get; set; }

        [Inject]
        public ILogger<Login> Logger { get; set; } = default!;

        [Inject]
        public MyUserServiceLogin MyUserServiceLogin { get; set; } = default!;

        [Inject]
        public IOptions<GoogleOAuthSettings> GoogleOptions { get; set; } = default!;

        [Inject]
        public IOptions<CookieSettings> CookieOptions { get; set; } = default!;

        [Inject]
        public IOptions<EmailSettings> EmailOptions { get; set; } = default!;

        string message = string.Empty;

        private bool ShowGoogleLogin => GoogleOptions.Value.IsConfigured;

        /// <summary>寄信未啟用（Provider = None）時沒有忘記密碼可用，連結不出現。</summary>
        private bool ShowForgotPassword => EmailOptions.Value.TryGetProvider(out var provider) && provider != EmailProvider.None;

        /// <summary>只在還沒送出登入表單時顯示；登入失敗的錯誤訊息優先。</summary>
        private bool ShowResetSucceeded => Reset == "1" && string.IsNullOrEmpty(message);

        private string GoogleLoginUrl =>
            string.IsNullOrWhiteSpace(ReturnUrl)
                ? "/Auths/Google/Login"
                : $"/Auths/Google/Login?returnUrl={Uri.EscapeDataString(ReturnUrlGuard.Sanitize(ReturnUrl))}";

        protected override Task OnInitializedAsync()
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

            Logger.LogDebug("Login component initialized. ReturnUrl={ReturnUrl}", ReturnUrl);
            return Task.CompletedTask;
        }

        public async Task LoginUser()
        {
            message = string.Empty;
            errorMessage = string.Empty;

            if (string.IsNullOrWhiteSpace(Input.Account))
            {
                message = "請輸入帳號";
                errorMessage = "alert-danger";
                Logger.LogWarning("Login submission rejected because account is empty.");
                return;
            }

            if (string.IsNullOrWhiteSpace(Input.Password))
            {
                message = "請輸入密碼";
                errorMessage = "alert-danger";
                Logger.LogInformation("Login submission rejected because password is empty. Account={Account}", Input.Account);
                return;
            }

            if (string.IsNullOrWhiteSpace(Input.CaptchaInput))
            {
                message = "請輸入驗證碼";
                errorMessage = "alert-danger";
                Logger.LogWarning("Login submission rejected because captcha is empty. Account={Account}", Input.Account);
                return;
            }

            if (!string.Equals(Input.CaptchaInput.Trim(), Input.CaptchaCode, StringComparison.Ordinal))
            {
                message = "驗證碼錯誤";
                errorMessage = "alert-danger";
                RefreshCaptcha();
                Input.CaptchaInput = string.Empty;
                Logger.LogWarning("Login submission rejected because captcha is invalid. Account={Account}", Input.Account);
                return;
            }

            (string result, MyUser? myUser) = await MyUserServiceLogin.LoginAsync(Input.Account, Input.Password);
            if (!string.IsNullOrEmpty(result) || myUser is null)
            {
                Logger.LogWarning("Login failed for Account={Account}. Reason={Reason}", Input.Account, result);
                message = string.IsNullOrEmpty(result) ? "登入失敗，請重新確認帳號與密碼。" : result;
                RefreshCaptcha();
                Input.CaptchaInput = string.Empty;
            }
            else
            {
                var claims = new List<Claim>
                {
                    new(ClaimTypes.Role, "User"),
                    new(ClaimTypes.Name, myUser.Name),
                    new(ClaimTypes.NameIdentifier, myUser.Account),
                    new(ClaimTypes.Sid, myUser.Id.ToString()),
                };

                var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

                string returnUrl = ReturnUrlGuard.Sanitize(ReturnUrl);
                var authProperties = new AuthenticationProperties
                {
                    IsPersistent = Input.RememberMe,
                    RedirectUri = returnUrl,
                };

                // 勾了「記住我」就走比較長的效期。
                // ⚠️ 不能靠 AddCookie 的 ExpireTimeSpan —— 那是整個 scheme 共用的，
                // 做不出兩種效期；明寫 ExpiresUtc 才能只覆蓋這一次登入的票證。
                if (Input.RememberMe)
                {
                    authProperties.ExpiresUtc =
                        DateTimeOffset.UtcNow.AddDays(CookieOptions.Value.RememberMeDays);
                }

                try
                {
                    await HttpContext.SignInAsync(
                        MagicObjectHelper.CookieScheme,
                        new ClaimsPrincipal(claimsIdentity),
                        authProperties);

                    Logger.LogInformation(
                        "Login succeeded for Account={Account}, UserId={UserId}, RedirectUri={RedirectUri}.",
                        Input.Account,
                        myUser.Id,
                        returnUrl);
                }
                catch (Exception ex)
                {
                    message = ex.Message;
                    RefreshCaptcha();
                    Input.CaptchaInput = string.Empty;
                    Logger.LogError(ex, "Sign-in failed for Account={Account}.", Input.Account);
                }
            }

            errorMessage = string.IsNullOrEmpty(message) ? string.Empty : "alert-danger";
        }

        private sealed class InputModel
        {
            public string Account { get; set; } = string.Empty;

            public string Password { get; set; } = string.Empty;

            public bool RememberMe { get; set; }

            public string CaptchaInput { get; set; } = string.Empty;

            public string CaptchaCode { get; set; } = string.Empty;
        }

        private void RefreshCaptcha()
        {
            captchaCode = AuthCaptcha.Generate();
            Input.CaptchaCode = captchaCode;
        }
    }
}
