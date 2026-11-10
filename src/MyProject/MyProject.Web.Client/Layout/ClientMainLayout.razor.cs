using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MyProject.Dtos.Shell;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Client.Commons;
using MyProject.Web.Components.Commons;
using MyProject.Web.Components.Layout;

namespace MyProject.Web.Client.Layout;

/// <summary>
/// WASM 頁面的版面（0.10.1 起，WASM 遷移 M2）。外觀與互動和 Server 版 <c>MainLayout</c> 相同，差別在資料來源：
/// <list type="bullet">
/// <item>登入檢查：<see cref="ClientSession.InitializeAsync"/>（Token 與 <c>/me</c>）；被要求改密碼或設定兩步驟驗證時整頁導走。</item>
/// <item>選單、系統資訊、說明主題：<c>/api/v1/shell/menu</c>、<c>/api/v1/shell/info</c>。</item>
/// <item>開頁稽核：每次換頁呼叫 <c>/api/v1/shell/page-view</c>（Server 版由電路處理）。</item>
/// </list>
/// </summary>
public partial class ClientMainLayout : LayoutComponentBase, IDisposable
{
    private const string DefaultPageTitle = "系統首頁";
    private const string DefaultUserDisplayName = "使用者";

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private ClientSession Session { get; set; } = default!;

    [Inject]
    private ApiClient Api { get; set; } = default!;

    [Inject]
    private ModalService ModalService { get; set; } = default!;

    [Inject]
    private IJSRuntime JSRuntime { get; set; } = default!;

    [Inject]
    private ClientErrorReporter ErrorReporter { get; set; } = default!;

    [Inject]
    private ILogger<ClientMainLayout> Logger { get; set; } = default!;

    private IReadOnlyList<ClientMenuItem> MenuItems { get; set; } = [];

    private IReadOnlyList<HelpTopicDto> HelpTopics { get; set; } = [];

    private string SystemName { get; set; } = string.Empty;

    private string CurrentPageTitle { get; set; } = DefaultPageTitle;

    private string CurrentUserDisplayName { get; set; } = DefaultUserDisplayName;

    private string CurrentUserInitials { get; set; } = "?";

    private bool CurrentUserIsAdmin { get; set; }

    private ShellInfoDto? shellInfo;
    private bool isAuthenticated;
    private bool isSidebarCollapsed;
    private bool isUserMenuOpen;
    private bool aboutVisible;
    private string currentPath = string.Empty;
    private DotNetObjectReference<ClientErrorReporter>? errorReporterReference;
    private IReadOnlyList<KeyValuePair<string, string>> aboutItems = [];

    protected override async Task OnInitializedAsync()
    {
        if (!await Session.InitializeAsync())
        {
            return;
        }

        var info = await Api.GetAsync<ShellInfoDto>("api/v1/shell/info");
        var menu = await Api.GetAsync<ShellMenuDto>("api/v1/shell/menu");
        shellInfo = info.Data;
        SystemName = shellInfo?.SystemName ?? string.Empty;
        HelpTopics = shellInfo?.HelpTopics ?? [];
        MenuItems = menu.Data?.Items.Select(ClientMenuItem.From).ToList() ?? [];

        UpdateCurrentUserStatus();
        currentPath = CurrentPath();
        UpdateCurrentPageTitle();
        NavigationManager.LocationChanged += OnLocationChanged;
        Session.Changed += OnSessionChanged;
        isSidebarCollapsed = await ReadInitialSidebarCollapsedAsync();
        isAuthenticated = true;
        _ = RecordPageViewAsync(currentPath, "entry");
    }

    /// <summary>頂欄的頁名：先找選單，不在選單裡的頁面（例如個人資料）用說明主題的標題（與 Server 版相同）。</summary>
    internal static string ResolvePageTitle(IEnumerable<ClientMenuItem> menuItems, IReadOnlyList<HelpTopicDto> helpTopics, string normalizedPath)
        => TryFindMenuTitle(menuItems, normalizedPath, out var pageTitle)
            ? pageTitle
            : ClientPageHelpDialog.MatchTopic(helpTopics, normalizedPath)?.Title ?? DefaultPageTitle;

    private static bool TryFindMenuTitle(IEnumerable<ClientMenuItem> items, string currentPath, out string pageTitle)
    {
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.Url) && ClientNavMenu.IsMatchingUrl(item.Url, currentPath))
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

    private string CurrentPath()
    {
        var relativePath = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
        var path = relativePath.Split('?', '#')[0].Trim('/');
        return string.IsNullOrEmpty(path) ? "/" : $"/{path}";
    }

    private void UpdateCurrentPageTitle() => CurrentPageTitle = ResolvePageTitle(MenuItems, HelpTopics, currentPath);

    private void UpdateCurrentUserStatus()
    {
        var info = Session.Info;
        CurrentUserDisplayName = !string.IsNullOrWhiteSpace(info?.Name)
            ? info.Name
            : !string.IsNullOrWhiteSpace(info?.Account)
                ? info.Account
                : DefaultUserDisplayName;
        CurrentUserInitials = UserInitials.From(info?.Name, info?.Account);
        CurrentUserIsAdmin = info?.IsAdmin == true;
    }

    private void OnSessionChanged()
    {
        UpdateCurrentUserStatus();
        InvokeAsync(StateHasChanged);
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        var previous = currentPath;
        currentPath = CurrentPath();
        isUserMenuOpen = false;
        UpdateCurrentPageTitle();
        InvokeAsync(StateHasChanged);
        // 導向 Server 頁面時不記：接著會整頁載入，由 Server 的電路記一筆（避免同一次換頁兩筆）。
        if (!string.Equals(previous, currentPath, StringComparison.OrdinalIgnoreCase) && ClientRouteTable.IsClientRoute(currentPath))
        {
            _ = RecordPageViewAsync(currentPath, previous);
        }
    }

    /// <summary>開頁稽核：射後不理，失敗不影響換頁（與 Server 版相同的原則）。</summary>
    private async Task RecordPageViewAsync(string path, string from)
    {
        var result = await Api.PostAsync<object>("api/v1/shell/page-view", new PageViewRequestDto { Path = path, From = from });
        if (!result.Success)
        {
            Logger.LogDebug("Page view audit was not recorded. Path={Path}", path);
        }
    }

    private async Task<bool> ReadInitialSidebarCollapsedAsync()
    {
        try
        {
            return await JSRuntime.InvokeAsync<bool>("appNavState.isInitiallyCollapsed");
        }
        catch (JSException ex)
        {
            Logger.LogDebug(ex, "Failed to read the sidebar state; using the default.");
            return false;
        }
    }

    private async Task OnLogoutClickAsync()
    {
        isUserMenuOpen = false;
        await LogoutConfirm.RequestAsync(ModalService, Session.LogoutAsync);
    }

    private void OnProfileClick()
    {
        isUserMenuOpen = false;
        NavigationManager.NavigateTo("/Profile");
    }

    private void OnChangePasswordClick()
    {
        isUserMenuOpen = false;
        NavigationManager.NavigateTo("/ChangePassword");
    }

    private void OnAboutClick()
    {
        var startedAt = shellInfo?.StartedAtLocal ?? DateTime.Now;
        var uptime = DateTime.Now - startedAt;
        aboutItems =
        [
            new("系統名稱", shellInfo?.SystemName ?? string.Empty),
            new("系統描述", shellInfo?.SystemDescription ?? string.Empty),
            new("系統版本", shellInfo?.SystemVersion ?? string.Empty),
            new("執行環境", shellInfo?.EnvironmentName ?? string.Empty),
            new("啟動時間", startedAt.ToString("yyyy/MM/dd HH:mm:ss")),
            new("已運作時間", (uptime < TimeSpan.Zero ? TimeSpan.Zero : uptime).ToString(@"dd\.hh\:mm\:ss")),
        ];
        isUserMenuOpen = false;
        aboutVisible = true;
    }

    private void OnAboutCancel() => aboutVisible = false;

    private async Task ToggleSidebar()
    {
        isSidebarCollapsed = !isSidebarCollapsed;
        try
        {
            await JSRuntime.InvokeVoidAsync("appNavState.save", isSidebarCollapsed);
        }
        catch (JSException ex)
        {
            Logger.LogDebug(ex, "Failed to save the sidebar state.");
        }
    }

    private void ToggleUserMenu() => isUserMenuOpen = !isUserMenuOpen;

    /// <summary>登入後才註冊瀏覽器錯誤回報（與 Server 版相同：匿名者無從灌資料）。</summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!isAuthenticated || errorReporterReference is not null)
        {
            return;
        }

        errorReporterReference = DotNetObjectReference.Create(ErrorReporter);
        try
        {
            await JSRuntime.InvokeVoidAsync("appClientErrors.register", errorReporterReference);
        }
        catch (JSException ex)
        {
            Logger.LogWarning(ex, "Failed to register the browser error reporter.");
        }
    }

    public void Dispose()
    {
        NavigationManager.LocationChanged -= OnLocationChanged;
        Session.Changed -= OnSessionChanged;
        errorReporterReference?.Dispose();
        GC.SuppressFinalize(this);
    }
}
