using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MyProject.Dtos.Auths;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;

namespace MyProject.Web.Client.Views.Commons;

/// <summary>
/// 啟動頁（<c>/</c>，0.10.8 起在 WASM 執行，WASM 遷移 M9）：顯示系統名稱與簡介 3 秒後檢查登入，已登入導到 <c>/app</c>，
/// 未登入由 <see cref="ClientSession"/> 帶到登入頁、必須改密碼或設定兩步驟驗證時帶到那一頁（與 Server 版 <c>AuthenticationStateHelper.Check</c> 相同）。
/// 系統名稱與簡介來自不需登入的 <c>GET api/v1/Auth/login-settings</c>（可在「系統參數」頁修改）。
/// </summary>
public partial class SplashView
{
    /// <summary>啟動畫面停留的時間（與 Server 版相同）。</summary>
    internal static readonly TimeSpan Delay = TimeSpan.FromSeconds(3);

    [Inject]
    public LoginPageSettingsSource SettingsSource { get; set; } = default!;

    [Inject]
    public ClientSession Session { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    public ILogger<SplashView> Logger { get; set; } = default!;

    private string SystemName { get; set; } = string.Empty;

    private string SystemDescription { get; set; } = string.Empty;

    protected override async Task OnInitializedAsync()
    {
        var settings = await SettingsSource.GetAsync();
        SystemName = settings.SystemName;
        SystemDescription = settings.SystemDescription;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            Logger.LogDebug("Splash view running authentication check.");
            await Task.Delay(Delay);
            if (await Session.InitializeAsync())
            {
                Logger.LogInformation("Splash view authentication succeeded. Redirecting to /app.");
                NavigationManager.NavigateTo("/app", true, true);
            }
            else
            {
                Logger.LogWarning("Splash view authentication check did not succeed.");
            }
        }
    }
}
