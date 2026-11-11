using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Components.Auths;

namespace MyProject.Web.Client.Views.Auths;

/// <summary>
/// 登入第二步（0.9.104 起；0.10.8 起在 WASM 執行，WASM 遷移 M9）：密碼或 Google 已通過、帳號已啟用兩步驟驗證時，從登入頁帶過來。
/// 誰在登入記在伺服器發的 5 分鐘待驗票證（加密，存在這個分頁的 sessionStorage），不在網址裡；沒有票證就回登入頁。
/// 驗證碼錯了計入登入失敗次數（伺服器處理）；票證失效（逾時、期間改了密碼或被強制登出）回登入頁重新輸入帳號密碼。
/// </summary>
public partial class TwoFactor
{
    private LoginPageSettingsDto? settings;
    private PendingTwoFactor? pending;
    private string message = string.Empty;
    private bool isBusy;

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
    public ILogger<TwoFactor> Logger { get; set; } = default!;

    private InputModel Input { get; } = new();

    protected override async Task OnInitializedAsync()
    {
        pending = await LoginStorage.ReadPendingAsync(Store);
        if (pending is null)
        {
            Logger.LogInformation("Two-factor page opened without a pending login; returning to the login page.");
            NavigationManager.NavigateTo(ClientSession.LoginPath);
            return;
        }

        settings = await SettingsSource.GetAsync();
    }

    private async Task SubmitAsync()
    {
        message = string.Empty;
        if (pending is null || isBusy)
        {
            return;
        }

        isBusy = true;
        try
        {
            var result = await Api.PostAnonymousAsync<WebLoginResultDto>("api/v1/Auth/two-factor", new TwoFactorLoginRequestDto
            {
                Ticket = pending.Ticket,
                Code = Input.Code,
                RememberDevice = Input.RememberDevice,
            });

            if (result.Success && result.Data?.Tokens is { } tokens)
            {
                if (result.Data.RememberDeviceToken is { } deviceToken)
                {
                    await LoginStorage.WriteDeviceTokenAsync(Store, deviceToken);
                }

                await LoginStorage.ClearPendingAsync(Store);
                await Session.SignedInAsync(tokens);
                Logger.LogInformation("Two-factor login completed. UserId={UserId}", tokens.User.Id);
                NavigationManager.NavigateTo(ReturnUrlGuard.Sanitize(result.Data.ReturnUrl), forceLoad: true);
                return;
            }

            if (result.ErrorCode == ApiErrorCodes.TwoFactorTicketInvalid)
            {
                // 等待輸入驗證碼的期間逾時、改了密碼、被停用或被強制登出：這次登入作廢，重新從密碼開始。
                Logger.LogInformation("Pending two-factor login discarded.");
                await LoginStorage.ClearPendingAsync(Store);
                NavigationManager.NavigateTo(ClientSession.LoginPath);
                return;
            }

            message = result.UserMessage();
            Input.Code = string.Empty;
        }
        finally
        {
            isBusy = false;
        }
    }

    private sealed class InputModel
    {
        public string Code { get; set; } = string.Empty;

        public bool RememberDevice { get; set; }
    }
}
