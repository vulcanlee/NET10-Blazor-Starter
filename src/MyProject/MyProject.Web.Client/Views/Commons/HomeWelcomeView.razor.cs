using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MyProject.Dtos.Shell;
using MyProject.Share.Helpers;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Client.Views.Commons;

/// <summary>
/// 登入後首頁（/App；0.10.6 起在 WASM 執行，WASM 遷移 M7）：精簡的歡迎列（品牌、系統名稱與說明、版本）、快速入口，以及依權限過濾的小工具區。
/// 快速入口與小工具由伺服器依權限決定（<c>GET api/v1/dashboard</c>），這裡只渲染收到的；畫面只做開頁檢查。
/// ⚠️ 除了開頁檢查，這個檔案不要再寫頁面權限檢查的常數呼叫 —— <c>MenuPermissionConsistencyTests</c> 以第一個出現的那種呼叫比對選單權限（連註解裡的也算）。
/// </summary>
public partial class HomeWelcomeView
{
    [Inject]
    public DashboardApi Api { get; set; } = default!;

    [Inject]
    public PagePermissions Permissions { get; set; } = default!;

    [Inject]
    public NotificationService NotificationService { get; set; } = default!;

    [Inject]
    public ILogger<HomeWelcomeView> Logger { get; set; } = default!;

    private string RoleMessage = string.Empty;
    private bool isAccessChecked;

    private string SystemName = string.Empty;
    private string SystemDescription = string.Empty;
    private string SystemVersion = string.Empty;
    private string EnvironmentName = string.Empty;
    private IReadOnlyList<DashboardQuickLinkDto> quickLinks = [];
    private IReadOnlyList<DashboardWidgetDto> widgets = [];

    protected override async Task OnInitializedAsync()
    {
        Logger.LogDebug("Initializing home welcome view.");

        // 沒有權限的開頁稽核由伺服器在開頁紀錄時寫（shell/page-view），這裡不讀任何資料。
        if (Permissions.CanAccessPage(MagicObjectHelper.角色_首頁) == false)
        {
            isAccessChecked = true;
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            Logger.LogWarning("Home welcome view denied because current user has not this role permission.");
            return;
        }

        var result = await Api.GetAsync();
        if (result.Success && result.Data is { } dashboard)
        {
            SystemName = dashboard.SystemName;
            SystemDescription = dashboard.SystemDescription;
            SystemVersion = dashboard.SystemVersion;
            EnvironmentName = dashboard.EnvironmentName;
            quickLinks = dashboard.QuickLinks;
            widgets = dashboard.Widgets;
        }
        else
        {
            ViewNotification.Error(NotificationService, $"讀取首頁失敗：{result.UserMessage()}");
        }

        // 資料到了才畫：避免歡迎列先以空白的系統名稱出現再跳動。
        isAccessChecked = true;
    }
}
