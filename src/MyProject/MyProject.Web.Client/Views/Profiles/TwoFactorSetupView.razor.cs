using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MyProject.Dtos.Auths;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Client.Views.Profiles;

/// <summary>
/// 兩步驟驗證的設定、備用碼與停用（0.9.104 起；0.10.8 起在 WASM 執行，WASM 遷移 M9）。登入即可使用；必須使用卻還沒設定的人，外框與 API 都會把人帶到這裡。
/// 畫面與文字照 Server 版；資料來自 <c>/api/v1/Auth/two-factor/setup</c>：QR Code 由伺服器產生 PNG，金鑰在確認驗證碼之前不存進資料庫，
/// 由伺服器發的設定票證帶著（15 分鐘）。啟用與停用會換工作階段版本（其他裝置登出）：伺服器回這台的新 Token，存起來後
/// 整頁回個人資料頁（與 Server 版換發登入後回個人資料頁相同；「必須設定兩步驟驗證」的狀態跟著重新讀）。
/// </summary>
public partial class TwoFactorSetupView
{
    private const string SetupApi = "api/v1/Auth/two-factor/setup";

    private bool isAccessChecked;
    private bool isBusy;
    private bool isRequired;
    private bool isEnabled;
    private bool hasLocalPassword;
    private int remainingBackupCodes;
    private string account = string.Empty;
    private TwoFactorSetupStartDto? enrollment;
    private IReadOnlyList<string>? backupCodes;
    private SessionRenewalDto? pendingRenewal;
    private string enableCode = string.Empty;
    private string regenerateCode = string.Empty;
    private string disableCode = string.Empty;

    [Inject]
    public ApiClient Api { get; set; } = default!;

    [Inject]
    public ClientSession Session { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    public NotificationService NotificationService { get; set; } = default!;

    [Inject]
    public ILogger<TwoFactorSetupView> Logger { get; set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        // 版面已完成登入檢查；這裡等到資料讀回來才渲染（與搬遷前相同，不先閃出空白框架）。
        await LoadAsync();
        isAccessChecked = true;
    }

    private async Task LoadAsync()
    {
        var result = await Api.GetAsync<TwoFactorSetupStatusDto>(SetupApi);
        var status = result.Data;
        account = status?.Account ?? string.Empty;
        hasLocalPassword = status?.HasLocalPassword ?? false;
        isEnabled = status?.IsEnabled ?? false;
        isRequired = status?.IsRequired ?? false;
        remainingBackupCodes = status?.RemainingBackupCodes ?? 0;
    }

    private Task BeginEnrollment() => RunAsync(async () =>
    {
        var result = await Api.PostAsync<TwoFactorSetupStartDto>($"{SetupApi}/start");
        if (!result.Success)
        {
            ViewNotification.Error(NotificationService, result.UserMessage());
            return;
        }

        enrollment = result.Data;
    });

    private Task EnableAsync() => RunAsync(async () =>
    {
        if (enrollment is null)
        {
            return;
        }

        var result = await Api.PostAsync<TwoFactorBackupCodesDto>($"{SetupApi}/enable", new TwoFactorEnableRequestDto { SetupTicket = enrollment.SetupTicket, Code = enableCode });
        enableCode = string.Empty;
        if (!result.Success || result.Data is not { } data)
        {
            ViewNotification.Error(NotificationService, Message(result, "啟用失敗。"));
            return;
        }

        enrollment = null;
        backupCodes = data.BackupCodes;

        // 工作階段版本已換：先存新 Token（之後的 API 呼叫才不會被登出），看完備用碼再回個人資料頁（與 Server 版相同的時機）。
        if (data.Tokens is { } tokens)
        {
            await Session.ApplyTokensAsync(tokens);
        }

        pendingRenewal = data;
    });

    private Task RegenerateAsync() => RunAsync(async () =>
    {
        var result = await Api.PostAsync<TwoFactorBackupCodesDto>($"{SetupApi}/backup-codes", new TwoFactorCodeRequestDto { Code = regenerateCode });
        regenerateCode = string.Empty;
        if (!result.Success || result.Data is not { } data)
        {
            ViewNotification.Error(NotificationService, Message(result, "重新產生失敗。"));
            return;
        }

        backupCodes = data.BackupCodes;
    });

    private Task DisableAsync() => RunAsync(async () =>
    {
        var result = await Api.PostAsync<SessionRenewalDto>($"{SetupApi}/disable", new TwoFactorCodeRequestDto { Code = disableCode });
        disableCode = string.Empty;
        if (!result.Success)
        {
            ViewNotification.Error(NotificationService, result.UserMessage());
            return;
        }

        ViewNotification.Warning(NotificationService, "已停用兩步驟驗證");
        if (!await KeepSignedInAsync(result.Data))
        {
            await LoadAsync();
        }
    });

    /// <summary>看完備用碼：剛啟用的話工作階段版本已換掉，換發這台的登入再回個人資料頁；只是重新產生就留在這頁。</summary>
    private Task ContinueAsync() => RunAsync(async () =>
    {
        backupCodes = null;
        if (pendingRenewal is { } renewal)
        {
            pendingRenewal = null;
            if (await KeepSignedInAsync(renewal))
            {
                return;
            }
        }

        await LoadAsync();
    });

    /// <summary>
    /// 自己的工作階段版本換掉之後（Server 版呼叫 SessionRefreshNavigator 的同一個時機）：存下新 Token，整頁回個人資料頁。
    /// 伺服器沒回新 Token（版本沒換）時不導頁、回 false。
    /// </summary>
    private async Task<bool> KeepSignedInAsync(SessionRenewalDto? renewal)
    {
        if (renewal?.Tokens is not { } tokens)
        {
            return false;
        }

        await Session.ApplyTokensAsync(tokens);
        NavigationManager.NavigateTo("/Profile", forceLoad: true);
        return true;
    }

    private static string Message<T>(MyProject.Dtos.Commons.ApiResult<T> result, string fallback)
        => string.IsNullOrWhiteSpace(result.UserMessage()) ? fallback : result.UserMessage();

    private async Task RunAsync(Func<Task> action)
    {
        if (isBusy)
        {
            return;
        }

        isBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Two-factor setup action failed unexpectedly.");
            ViewNotification.UnexpectedError(NotificationService, "處理兩步驟驗證時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
        }
        finally
        {
            isBusy = false;
        }
    }

    /// <summary>金鑰每 4 個字元空一格，方便手動輸入。</summary>
    internal static string FormatKey(string secret)
        => string.Join(' ', secret.Chunk(4).Select(chunk => new string(chunk)));
}
