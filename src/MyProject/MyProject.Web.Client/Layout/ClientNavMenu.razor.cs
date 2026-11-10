using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.Logging;
using MyProject.Web.Client.Auth;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Client.Layout;

/// <summary>
/// WASM 外框的側邊欄（0.10.1 起，WASM 遷移 M2）。標記、樣式與互動和 Server 版 <c>NavMenu</c> 相同；
/// 差別只在資料來源：選單由版面以 <c>/api/v1/shell/menu</c> 取得（權限已在伺服器端過濾）後傳進來，登出走 <see cref="ClientSession"/>。
/// ⚠️ M10 移除 Server 模式前兩份並存：改了 Server 版的側邊欄，這裡要一起改（截圖比對見各里程碑測試報告）。
/// </summary>
public partial class ClientNavMenu : ComponentBase, IDisposable
{
    [Parameter]
    public bool IsSidebarCollapsed { get; set; }

    [Parameter]
    public EventCallback OnSidebarToggle { get; set; }

    [Parameter]
    public IReadOnlyList<ClientMenuItem> MenuItems { get; set; } = [];

    /// <summary>側邊欄品牌文字（系統名稱，可在「系統參數」頁修改）。</summary>
    [Parameter]
    public string SystemName { get; set; } = string.Empty;

    [Inject]
    private ILogger<ClientNavMenu> Logger { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private ModalService ModalService { get; set; } = default!;

    [Inject]
    private ClientSession Session { get; set; } = default!;

    private string? ActiveMenuPath { get; set; }

    private string[] OpenKeys { get; set; } = [];

    private string[] SelectedKeys { get; set; } = [];

    private bool previousSidebarCollapsed;
    private bool initialOpenKeysApplied;
    private IReadOnlyList<ClientMenuItem>? appliedMenuItems;

    protected override void OnInitialized()
    {
        NavigationManager.LocationChanged += OnLocationChanged;
    }

    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(appliedMenuItems, MenuItems))
        {
            appliedMenuItems = MenuItems;
            UpdateActiveMenuPath();
            SyncMenuStateFromRoute();

            // 展開的群組留到選單第一次渲染後才套用（AntDesign 的 SubMenu 註冊時機，原因同 Server 版）。
            OpenKeys = [];
            initialOpenKeysApplied = false;
        }

        if (previousSidebarCollapsed != IsSidebarCollapsed)
        {
            previousSidebarCollapsed = IsSidebarCollapsed;
            SyncOpenKeysForSidebarState();
        }
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (initialOpenKeysApplied || MenuItems.Count == 0)
        {
            return;
        }

        initialOpenKeysApplied = true;
        SyncOpenKeysForSidebarState();
        if (OpenKeys.Length > 0)
        {
            StateHasChanged();
        }
    }

    private Task OnLogoutClickAsync() => LogoutConfirm.RequestAsync(ModalService, Session.LogoutAsync);

    private void RestoreMenuSelection()
    {
        SyncMenuStateFromRoute();
        StateHasChanged();
    }

    private void UpdateActiveMenuPath()
    {
        var relativePath = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
        var currentPath = relativePath.Split('?', '#')[0].Trim('/');
        var normalizedCurrentPath = string.IsNullOrEmpty(currentPath) ? "/" : $"/{currentPath}";
        ActiveMenuPath = TryFindActiveMenuPath(MenuItems, normalizedCurrentPath, "root", out var activePath) ? activePath : null;
        Logger.LogDebug("Updated active menu path. Path={Path}, ActiveMenuPath={ActiveMenuPath}", normalizedCurrentPath, ActiveMenuPath);
    }

    private void SyncMenuStateFromRoute()
    {
        SelectedKeys = string.IsNullOrWhiteSpace(ActiveMenuPath) ? [] : [ActiveMenuPath];
        SyncOpenKeysForSidebarState();
    }

    private void SyncOpenKeysForSidebarState()
    {
        OpenKeys = IsSidebarCollapsed ? [] : GetAncestorKeys(ActiveMenuPath);
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
        => IsMenuKeyActive(itemKey) ? "collapsed-menu-item collapsed-menu-item-active" : "collapsed-menu-item";

    private string GetCollapsedFlyoutItemClass(string itemKey)
        => string.Equals(ActiveMenuPath, itemKey, StringComparison.Ordinal) ? "collapsed-flyout-item collapsed-flyout-item-active" : "collapsed-flyout-item";

    private string GetCollapsedFlyoutGroupClass(string itemKey)
        => IsMenuKeyActive(itemKey)
            ? "collapsed-flyout-item collapsed-flyout-group-trigger collapsed-flyout-item-active"
            : "collapsed-flyout-item collapsed-flyout-group-trigger";

    private bool IsMenuKeyActive(string itemKey)
        => string.Equals(ActiveMenuPath, itemKey, StringComparison.Ordinal)
            || (ActiveMenuPath?.StartsWith($"{itemKey}-", StringComparison.Ordinal) ?? false);

    private static string GetMaterialIconKind(ClientMenuItem item) => item.IconKind;

    private static bool TryFindActiveMenuPath(IReadOnlyList<ClientMenuItem> items, string currentPath, string parentKey, out string? activePath)
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

    internal static bool IsMatchingUrl(string url, string currentPath)
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

    private void HandleOpenKeysChanged(string[] openKeys) => OpenKeys = openKeys;

    private Task ToggleSidebar() => OnSidebarToggle.InvokeAsync();

    public void Dispose()
    {
        NavigationManager.LocationChanged -= OnLocationChanged;
        GC.SuppressFinalize(this);
    }
}
