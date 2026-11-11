using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MyProject.Dtos.Auths;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Client.Views.Profiles;

/// <summary>
/// 變更密碼（0.9.101 起；0.10.8 起在 WASM 執行，WASM 遷移 M9）。畫面、文字與提示照 Server 版；資料來自 <c>/api/v1/Auth/password/change</c>，
/// 密碼原則、歷史、support 保護與稽核都在伺服器（<c>MyUserService.ChangeOwnPasswordAsync</c>）。
/// 成功後其他裝置都登出（工作階段版本已換），伺服器回這台的新 Token：存起來後整頁回首頁（「必須變更密碼」的狀態跟著重新讀）。
/// </summary>
public partial class ChangePasswordView
{
    private readonly InputModel input = new();
    private bool isLoading = true;
    private bool isSubmitting;
    private bool isSupportAccount;
    private string currentAccount = string.Empty;
    private string roleMessage = string.Empty;
    private string requiredReason = string.Empty;
    private string passwordRule = string.Empty;

    [Inject]
    public ApiClient Api { get; set; } = default!;

    [Inject]
    public ClientSession Session { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    public MessageService MessageService { get; set; } = default!;

    [Inject]
    public NotificationService NotificationService { get; set; } = default!;

    [Inject]
    public ILogger<ChangePasswordView> Logger { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        var status = await Api.GetAsync<ChangePasswordStatusDto>("api/v1/Auth/password/change");
        if (!status.Success || status.Data is not { } data)
        {
            roleMessage = "找不到目前登入使用者資料。";
            isLoading = false;
            return;
        }

        currentAccount = data.Account;
        isSupportAccount = data.IsSupportAccount;
        requiredReason = data.RequiredReason;
        passwordRule = data.PasswordRule;
        isLoading = false;
    }

    private async Task OnSubmitAsync()
    {
        if (isSubmitting)
        {
            return;
        }

        isSubmitting = true;
        try
        {
            var result = await Api.PostAsync<SessionRenewalDto>("api/v1/Auth/password/change", new ChangePasswordRequestDto
            {
                CurrentPassword = input.CurrentPassword,
                NewPassword = input.NewPassword,
                ConfirmPassword = input.ConfirmPassword,
            });

            if (!result.Success)
            {
                ViewNotification.Error(NotificationService, result.UserMessage());
                return;
            }

            await MessageService.SuccessAsync("密碼已變更。");

            // 改了密碼，其他裝置都已登出；這台換成伺服器給的新 Token 繼續使用。
            if (result.Data?.Tokens is { } tokens)
            {
                await Session.ApplyTokensAsync(tokens);
            }

            NavigationManager.NavigateTo("/App", forceLoad: true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Changing own password failed unexpectedly.");
            ViewNotification.UnexpectedError(NotificationService, "變更密碼時發生未預期的錯誤。", ex);
        }
        finally
        {
            isSubmitting = false;
        }
    }

    private void OnValidationFailed()
        => ViewNotification.ValidationError(NotificationService, "請確認密碼欄位皆已正確填寫。");
}
