using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;
using MyProject.Business.Services.Other;
using MyProject.Web.Components.Commons;
using MyProject.Web.Diagnostics;
using MyProject.Web.Health;
using Microsoft.JSInterop;

namespace MyProject.Web.Components.Layout;

public partial class MainLayout : LayoutComponentBase, IDisposable
{
    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;

    [Inject]
    private CurrentUserService CurrentUserService { get; set; } = default!;

    [Inject]
    private ILogger<MainLayout> Logger { get; set; } = default!;

    [Inject]
    private SidebarMenuService SidebarMenuService { get; set; } = default!;

    [Inject]
    private ModalService ModalService { get; set; } = default!;

    [Inject]
    private ISystemIdentity SystemIdentity { get; set; } = default!;

    [Inject]
    private IWebHostEnvironment WebHostEnvironment { get; set; } = default!;

    [Inject]
    private SystemStartupState SystemStartupState { get; set; } = default!;

    private const string DefaultPageTitle = "系統首頁";
    private const string DefaultUserDisplayName = "使用者";

    private IReadOnlyList<SidebarMenuItemModel> MenuItems { get; set; } = [];
    private string CurrentPageTitle { get; set; } = DefaultPageTitle;
    private string CurrentUserDisplayName { get; set; } = DefaultUserDisplayName;
    private bool CurrentUserIsAdmin { get; set; }
    private bool isAuthenticated;
    private bool isSidebarCollapsed = true;
    private bool isUserMenuOpen;

    private bool aboutVisible = false;

    [Inject]
    private BrowserErrorReporter BrowserErrorReporter { get; set; } = default!;

    [Inject]
    private IJSRuntime JSRuntime { get; set; } = default!;

    private DotNetObjectReference<BrowserErrorReporter>? browserErrorReporterReference;
    private IReadOnlyList<KeyValuePair<string, string>> aboutItems = [];

    protected override async Task OnInitializedAsync()
    {
        Logger.LogDebug("Initializing main layout.");

        var checkResult = await AuthenticationStateHelper.Check(AuthenticationStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            MenuItems = [];
            return;
        }

        MenuItems = await SidebarMenuService.LoadAuthorizedMenuItemsAsync(AuthenticationStateHelper);
        UpdateCurrentUserStatus();
        UpdateCurrentPageTitle();
        NavigationManager.LocationChanged += OnLocationChanged;
        isAuthenticated = true;
    }

    private void UpdateCurrentUserStatus()
    {
        var currentUser = CurrentUserService.CurrentUser;

        CurrentUserDisplayName = !string.IsNullOrWhiteSpace(currentUser.Name)
            ? currentUser.Name
            : !string.IsNullOrWhiteSpace(currentUser.Account)
                ? currentUser.Account
                : DefaultUserDisplayName;

        CurrentUserIsAdmin = currentUser.IsAdmin;
    }

    private void UpdateCurrentPageTitle()
    {
        // 查詢字串與錨點不參與比對（例如 /ai-call-logs?callId=… 的深連結，0.9.72 起）。
        var relativePath = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
        var currentPath = relativePath.Split('?', '#')[0].Trim('/');
        var normalizedCurrentPath = string.IsNullOrEmpty(currentPath) ? "/" : $"/{currentPath}";

        CurrentPageTitle = TryFindMenuTitle(MenuItems, normalizedCurrentPath, out var pageTitle)
            ? pageTitle
            : DefaultPageTitle;

        Logger.LogDebug("Updated page title. Path={Path}, Title={Title}", normalizedCurrentPath, CurrentPageTitle);
    }

    private static bool TryFindMenuTitle(IEnumerable<SidebarMenuItemModel> items, string currentPath, out string pageTitle)
    {
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.Url) && IsMatchingUrl(item.Url, currentPath))
            {
                pageTitle = item.Name;
                return true;
            }

            if (item.HasChildren && TryFindMenuTitle(item.SubMenu, currentPath, out pageTitle))
            {
                return true;
            }
        }

        pageTitle = string.Empty;
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
        isUserMenuOpen = false;
        UpdateCurrentPageTitle();
        InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// 使用者主動按下登出。先收起選單再問，否則確認窗後面還浮著一個展開的下拉。
    /// ⚠️ 只有這裡（與側邊欄的兩個登出入口）走確認；
    /// AuthenticationStateHelper 的強制登出不經過這條路。
    /// </summary>
    private async Task OnLogoutClickAsync()
    {
        isUserMenuOpen = false;
        await LogoutConfirm.RequestAsync(ModalService, NavigationManager);
    }

    /// <summary>
    /// 0.9.101 起變更密碼只有一個入口：/ChangePassword 頁（右上角原本的對話窗沒有套用密碼原則與歷史，已移除）。
    /// </summary>
    private void OnChangePasswordClick()
    {
        isUserMenuOpen = false;
        NavigationManager.NavigateTo("/ChangePassword");
    }

    /// <summary>
    /// 開啟「關於」對話窗。已運作時間需在開啟當下計算，Blazor Server 不會自動刷新已渲染的值。
    /// </summary>
    private void OnAboutClick()
    {
        Logger.LogDebug("Opening about dialog.");

        var uptime = DateTimeOffset.Now - SystemStartupState.StartedAt;

        aboutItems =
        [
            new("系統名稱", SystemIdentity.Name),
            new("系統描述", SystemIdentity.Description),
            new("系統版本", SystemIdentity.Version),
            new("執行環境", WebHostEnvironment.EnvironmentName),
            new("啟動時間", SystemStartupState.StartedAt.ToString("yyyy/MM/dd HH:mm:ss")),
            new("已運作時間", uptime.ToString(@"dd\.hh\:mm\:ss")),
        ];

        isUserMenuOpen = false;
        aboutVisible = true;
    }

    private void OnAboutCancel()
    {
        aboutVisible = false;
    }

    private void ToggleSidebar()
    {
        isSidebarCollapsed = !isSidebarCollapsed;
    }

    private void ToggleUserMenu()
    {
        isUserMenuOpen = !isUserMenuOpen;
    }

    /// <summary>
    /// 登入後才註冊瀏覽器錯誤回報（LOG-20）：只有已登入、有 circuit 的頁面回報，匿名者無從灌資料。
    /// isAuthenticated 在非同步初始化後才成立，所以不能只看 firstRender。
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (isAuthenticated == false || browserErrorReporterReference is not null || BrowserErrorReporter.IsEnabled == false)
        {
            return;
        }

        browserErrorReporterReference = DotNetObjectReference.Create(BrowserErrorReporter);
        try
        {
            await JSRuntime.InvokeVoidAsync("appClientErrors.register", browserErrorReporterReference);
        }
        catch (JSDisconnectedException)
        {
            // circuit 已斷線，沒有東西可註冊。
        }
        catch (JSException ex)
        {
            // 腳本沒載入（例如被瀏覽器外掛擋掉）：少了前端錯誤回報，頁面照常可用。
            Logger.LogWarning(ex, "Failed to register the browser error reporter.");
        }
    }

    public void Dispose()
    {
        Logger.LogDebug("Disposing main layout.");
        NavigationManager.LocationChanged -= OnLocationChanged;
        browserErrorReporterReference?.Dispose();
    }
}
