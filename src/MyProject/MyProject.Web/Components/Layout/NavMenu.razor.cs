using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.Options;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Components.Layout;

public partial class NavMenu : ComponentBase, IDisposable
{
    [Parameter]
    public bool IsSidebarCollapsed { get; set; }

    [Parameter]
    public EventCallback OnSidebarToggle { get; set; }

    [Inject]
    private AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;

    [Inject]
    private ILogger<NavMenu> Logger { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private SidebarMenuService SidebarMenuService { get; set; } = default!;

    [Inject]
    private ModalService ModalService { get; set; } = default!;

    /// <summary>側邊欄收合狀態的登出入口。行為與使用者選單、展開側邊欄一致。</summary>
    private Task OnLogoutClickAsync()
        => LogoutConfirm.RequestAsync(ModalService, NavigationManager);

    /// <summary>
    /// 使用者取消登出後，把選中狀態拉回目前路由。
    ///
    /// ⚠️ AntDesign 的 Menu 預設 Selectable=true，點下登出項它會立刻標成選中；
    /// 取消登出不會產生 LocationChanged，所以那個選中樣式會一直留著不消失。
    /// </summary>
    private void RestoreMenuSelection()
    {
        SyncMenuStateFromRoute();
        StateHasChanged();
    }

    [Inject]
    private IOptions<SystemSettings> SystemSettingsOptions { get; set; } = default!;

    /// <summary>
    /// 側邊欄品牌文字，統一取自 appsettings.json 的 SystemSettings:SystemInformation:SystemName。
    ///
    /// 0.9.13 之前這裡是硬編的 "MyProject.Web" —— 對腳手架而言每個複製出去的新專案都得記得手改，
    /// 忘了就會在側邊欄掛著別人的專案代號。改讀設定後，SystemName 才真正是單一來源
    /// （啟動頁、登入頁、登入後首頁、「關於」對話窗、側邊欄）。
    /// </summary>
    private string SystemName => SystemSettingsOptions.Value.SystemInformation.SystemName;

    private IReadOnlyList<SidebarMenuItemModel> MenuItems { get; set; } = [];
    private string? ActiveMenuPath { get; set; }
    private string[] OpenKeys { get; set; } = [];
    private string[] SelectedKeys { get; set; } = [];
    private bool previousSidebarCollapsed;

    protected override async Task OnInitializedAsync()
    {
        Logger.LogDebug("Initializing navigation menu.");

        var checkResult = await AuthenticationStateHelper.Check(AuthenticationStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            MenuItems = [];
            return;
        }

        MenuItems = await SidebarMenuService.LoadAuthorizedMenuItemsAsync(AuthenticationStateHelper);
        UpdateActiveMenuPath();
        SyncMenuStateFromRoute();
        NavigationManager.LocationChanged += OnLocationChanged;
    }

    protected override void OnParametersSet()
    {
        if (previousSidebarCollapsed == IsSidebarCollapsed)
        {
            return;
        }

        previousSidebarCollapsed = IsSidebarCollapsed;
        SyncOpenKeysForSidebarState();
    }

    private void UpdateActiveMenuPath()
    {
        var currentPath = NavigationManager.ToBaseRelativePath(NavigationManager.Uri).Trim('/');
        var normalizedCurrentPath = string.IsNullOrEmpty(currentPath) ? "/" : $"/{currentPath}";

        ActiveMenuPath = TryFindActiveMenuPath(MenuItems, normalizedCurrentPath, "root", out var activePath)
            ? activePath
            : null;

        Logger.LogDebug("Updated active menu path. Path={Path}, ActiveMenuPath={ActiveMenuPath}", normalizedCurrentPath, ActiveMenuPath);
    }

    private void SyncMenuStateFromRoute()
    {
        SelectedKeys = string.IsNullOrWhiteSpace(ActiveMenuPath)
            ? []
            : [ActiveMenuPath];

        SyncOpenKeysForSidebarState();
    }

    private void SyncOpenKeysForSidebarState()
    {
        OpenKeys = IsSidebarCollapsed
            ? []
            : GetAncestorKeys(ActiveMenuPath);
    }

    private static string[] GetAncestorKeys(string? activeMenuPath)
    {
        if (string.IsNullOrWhiteSpace(activeMenuPath))
        {
            return [];
        }

        var segments = activeMenuPath.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= 2)
        {
            return [];
        }

        var ancestors = new List<string>(segments.Length - 2);
        for (var index = 2; index < segments.Length; index++)
        {
            ancestors.Add(string.Join('-', segments[..index]));
        }

        return [.. ancestors];
    }

    private string GetCollapsedItemClass(string itemKey)
    {
        var className = "collapsed-menu-item";

        if (IsMenuKeyActive(itemKey))
        {
            className += " collapsed-menu-item-active";
        }

        return className;
    }

    private string GetCollapsedFlyoutItemClass(string itemKey)
    {
        var className = "collapsed-flyout-item";

        if (string.Equals(ActiveMenuPath, itemKey, StringComparison.Ordinal))
        {
            className += " collapsed-flyout-item-active";
        }

        return className;
    }

    /// <summary>
    /// 收合 flyout 內「群組列」的樣式：以前綴比對標示目前頁面所屬的群組。
    ///
    /// 第三層改為巢狀 flyout 後預設是收著的，若群組列不標示，使用者會完全失去
    /// 「現在這頁屬於哪個群組」的線索（展平版本因七項同時可見而沒有這個問題）。
    /// </summary>
    private string GetCollapsedFlyoutGroupClass(string itemKey)
    {
        var className = "collapsed-flyout-item collapsed-flyout-group-trigger";

        if (IsMenuKeyActive(itemKey))
        {
            className += " collapsed-flyout-item-active";
        }

        return className;
    }

    private bool IsMenuKeyActive(string itemKey)
    {
        return string.Equals(ActiveMenuPath, itemKey, StringComparison.Ordinal)
            || (ActiveMenuPath?.StartsWith($"{itemKey}-", StringComparison.Ordinal) ?? false);
    }

    private static string GetMaterialIconKind(SidebarMenuItemModel item)
    {
        var icon = item.Icon?.Trim();

        if (string.IsNullOrWhiteSpace(icon))
        {
            return item.HasChildren ? "folder_open" : "article";
        }

        return icon switch
        {
            "home" => "home",
            "dashboard" => "space_dashboard",
            "admin" => "admin_panel_settings",
            "users" => "group",
            "roles" => "security",
            "shield_person" => "security",
            "setting" => "settings",
            "ProjectFilled" => "workspaces",
            "CarryOutFilled" => "checklist",
            "EnterOutlined" => "event",
            _ => icon
        };
    }

    private static bool TryFindActiveMenuPath(IReadOnlyList<SidebarMenuItemModel> items, string currentPath, string parentKey, out string? activePath)
    {
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var currentKey = $"{parentKey}-{index}";

            if (!string.IsNullOrWhiteSpace(item.Url) && IsMatchingUrl(item.Url, currentPath))
            {
                activePath = currentKey;
                return true;
            }

            if (item.HasChildren && TryFindActiveMenuPath(item.SubMenu, currentPath, currentKey, out activePath))
            {
                return true;
            }
        }

        activePath = null;
        return false;
    }

    private static bool IsMatchingUrl(string url, string currentPath)
    {
        var normalizedTargetPath = url.Trim();
        if (string.IsNullOrEmpty(normalizedTargetPath))
        {
            return false;
        }

        normalizedTargetPath = normalizedTargetPath.StartsWith('/') ? normalizedTargetPath : $"/{normalizedTargetPath}";
        if (string.Equals(normalizedTargetPath, "/", StringComparison.Ordinal))
        {
            return string.Equals(currentPath, "/", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(currentPath, normalizedTargetPath, StringComparison.OrdinalIgnoreCase)
            || currentPath.StartsWith($"{normalizedTargetPath}/", StringComparison.OrdinalIgnoreCase);
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        UpdateActiveMenuPath();
        SyncMenuStateFromRoute();
        InvokeAsync(StateHasChanged);
    }

    private void HandleOpenKeysChanged(string[] openKeys)
    {
        OpenKeys = openKeys;
    }

    private Task ToggleSidebar()
    {
        return OnSidebarToggle.InvokeAsync();
    }

    public void Dispose()
    {
        Logger.LogDebug("Disposing navigation menu.");
        NavigationManager.LocationChanged -= OnLocationChanged;
    }
}
