using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;

namespace MyProject.Web.Components.Views.Commons;

/// <summary>
/// 登入後首頁（/App）的系統介紹檢視：品牌、系統說明、能力介紹與快速入口。
/// 純靜態內容，不讀取資料庫。
/// </summary>
public partial class HomeWelcomeView
{
    /// <summary>
    /// 能力介紹卡片。屬「設計文案」，比照登入頁與啟動頁的既有作法刻意寫死不參數化
    /// （見 docs/guides/VS Code 開發環境與新專案上手指南.md §7.6）。
    /// </summary>
    private sealed record FeatureCard(string Icon, string Title, string Description);

    /// <summary>快速入口。<paramref name="PermissionKey"/> 用於濾掉目前使用者沒有權限的項目。</summary>
    private sealed record QuickLink(string Url, string Icon, string Title, string PermissionKey);

    /// <summary>
    /// ⚠️ 圖示名稱必須是 <b>classic Material Icons</b>（App.razor 載入的是 Material Icons，
    /// 不是 Material Symbols）。用 Symbols 專有名稱會渲染失敗 —— 可能是破圖方塊，也可能被拆成
    /// 數個子字的圖示（例如 shield_person 會畫出「盾」與「人」兩個圖示並撐破容器）。
    /// 新增卡片前請先實際開頁確認。見開發慣例速查 §6.1。
    /// </summary>
    private static readonly FeatureCard[] FeatureCards =
    [
        new("security", "權限與角色控管",
            "以角色為單位授予動作級權限（檢視／新增／修改／刪除／匯出），一位使用者可掛多個角色並取權限聯集。"),
        new("work", "專案項目管理",
            "專案建檔、狀態與優先序追蹤、附件上傳與標籤歸類，是可直接沿用的 CRUD 範本模組。"),
        new("category", "分類與團隊定義",
            "以分類與團隊描述資料歸屬，並據此做到列級的資料可見性控管，讓不同團隊只看得到自己的紀錄。"),
        new("analytics", "日誌檢視與 AI 分析",
            "線上檢視系統日誌、即時調整日誌等級，並可把日誌交給 AI 整理成可下載的分析報告。"),
        new("data_usage", "健康監控與資料庫用量",
            "以紅黃綠燈號呈現系統健康度，內建部署探針，並統計各資料表的筆數與磁碟用量。"),
        new("cloud_upload", "檔案上傳與保管",
            "附件依年月目錄存放，刪除紀錄時同步清除實體檔案，並以副檔名白名單與容量上限把關。"),
    ];

    private static readonly QuickLink[] AllQuickLinks =
    [
        new("/projects", "workspaces", "專案項目", MagicObjectHelper.角色_專案項目),
        new("/categories", "category", "分類清單", MagicObjectHelper.角色_分類清單),
        new("/teams", "groups", "團隊清單", MagicObjectHelper.角色_團隊清單),
    ];

    [Inject]
    public AuthenticationStateProvider authStateProvider { get; set; } = default!;

    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    public ILogger<HomeWelcomeView> Logger { get; set; } = default!;

    [Inject]
    public IOptions<SystemSettings> SystemSettingsOptions { get; set; } = default!;

    [Inject]
    public IWebHostEnvironment WebHostEnvironment { get; set; } = default!;

    /// <summary>系統名稱，統一取自 appsettings.json 的 SystemSettings:SystemInformation:SystemName。</summary>
    private string SystemName => SystemSettingsOptions.Value.SystemInformation.SystemName;

    /// <summary>系統簡短說明，統一取自 appsettings.json 的 SystemSettings:SystemInformation:SystemDescription。</summary>
    private string SystemDescription => SystemSettingsOptions.Value.SystemInformation.SystemDescription;

    /// <summary>系統版本，唯一來源同上（SystemVersion）。</summary>
    private string SystemVersion => SystemSettingsOptions.Value.SystemInformation.SystemVersion;

    private string RoleMessage = string.Empty;
    private bool isAccessChecked;

    private IReadOnlyList<QuickLink> quickLinks = [];

    protected override async Task OnInitializedAsync()
    {
        Logger.LogDebug("Initializing home welcome view.");

        var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            Logger.LogWarning("Home welcome view initialization stopped because authentication check failed.");
            return;
        }

        isAccessChecked = true;

        if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_首頁) == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            Logger.LogWarning("Home welcome view denied because current user has not this role permission.");
            return;
        }

        quickLinks = [.. AllQuickLinks.Where(x => AuthenticationStateHelper.CheckAccessPage(x.PermissionKey))];
    }
}
