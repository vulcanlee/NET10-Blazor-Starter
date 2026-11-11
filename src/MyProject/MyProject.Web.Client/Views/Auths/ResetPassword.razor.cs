using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MyProject.Dtos.Auths;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;

namespace MyProject.Web.Client.Views.Auths;

/// <summary>
/// 以信中連結重設密碼（0.9.60 起；0.10.8 起在 WASM 執行，WASM 遷移 M9）。開頁時經 <c>POST api/v1/Auth/password/reset/check</c> 檢查連結（不消耗），
/// 送出經 <c>POST api/v1/Auth/password/reset</c>：密碼規則或歷史不過時不消耗連結，可以改正後再送；成功回登入頁顯示「密碼已重設」。
/// Token 放在請求本文（不進伺服器日誌）；主機對這一頁回 <c>Referrer-Policy: no-referrer</c> 與 <c>no-store</c>（網址裡有 Token）。
/// </summary>
public partial class ResetPassword
{
    /// <summary>與伺服器 <c>PasswordResetService.InvalidLinkMessage</c> 相同。</summary>
    internal const string InvalidLinkMessage = "重設連結無效或已過期，請重新申請。";

    private LoginPageSettingsDto? settings;
    private string message = string.Empty;
    private bool isChecked;
    private bool isTokenValid;
    private bool isBusy;
    private string? account;
    private string passwordRule = string.Empty;

    [Inject]
    public ApiClient Api { get; set; } = default!;

    [Inject]
    public LoginPageSettingsSource SettingsSource { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    public ILogger<ResetPassword> Logger { get; set; } = default!;

    [SupplyParameterFromQuery(Name = "token")]
    private string? Token { get; set; }

    private InputModel Input { get; } = new();

    private string Subtitle => isTokenValid
        ? $"為帳號「{account}」設定新密碼。密碼規則：{passwordRule}"
        : "請使用重設密碼信中的連結開啟本頁。";

    protected override async Task OnInitializedAsync()
    {
        settings = await SettingsSource.GetAsync();
        if (settings.EmailEnabled)
        {
            var check = await Api.PostAnonymousAsync<ResetPasswordCheckDto>("api/v1/Auth/password/reset/check", new ResetPasswordCheckRequestDto { Token = Token });
            isTokenValid = check.Data?.Valid == true;
            account = check.Data?.Account;
            passwordRule = check.Data?.PasswordRule ?? string.Empty;
            if (!isTokenValid)
            {
                Logger.LogInformation("Reset password page opened with an invalid or expired link.");
            }
        }

        isChecked = true;
    }

    private async Task SubmitAsync()
    {
        message = string.Empty;
        if (settings?.EmailEnabled != true || !isTokenValid || isBusy)
        {
            return;
        }

        isBusy = true;
        try
        {
            var result = await Api.PostAnonymousAsync<ResetPasswordResultDto>("api/v1/Auth/password/reset", new ResetPasswordRequestDto
            {
                Token = Token,
                NewPassword = Input.NewPassword,
                ConfirmPassword = Input.ConfirmPassword,
            });
            if (result.Success)
            {
                NavigationManager.NavigateTo("/Auths/Login?reset=1");
                return;
            }

            message = result.UserMessage();
            isTokenValid = result.Data?.LinkInvalid != true;
            Input.NewPassword = string.Empty;
            Input.ConfirmPassword = string.Empty;
        }
        finally
        {
            isBusy = false;
        }
    }

    private sealed class InputModel
    {
        public string NewPassword { get; set; } = string.Empty;

        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
