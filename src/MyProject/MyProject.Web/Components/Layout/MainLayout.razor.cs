using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;
using MyProject.Business.Services.Other;

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
    private ILogger<MainLayout> Logger { get; set; } = default!;

    [Inject]
    private SidebarMenuService SidebarMenuService { get; set; } = default!;

    private const string DefaultPageTitle = "蝟餌絞擐?";
    private IReadOnlyList<SidebarMenuItemModel> MenuItems { get; set; } = [];
    private string CurrentPageTitle { get; set; } = DefaultPageTitle;
    private bool isSidebarCollapsed;

    protected override async Task OnInitializedAsync()
    {
        Logger.LogDebug("Initializing main layout.");

        var checkResult = await AuthenticationStateHelper.Check(AuthenticationStateProvider, NavigationManager);
        if (!checkResult)
        {
            MenuItems = [];
            return;
        }

        MenuItems = SidebarMenuService.LoadAuthorizedMenuItems(AuthenticationStateHelper);
        UpdateCurrentPageTitle();
        NavigationManager.LocationChanged += OnLocationChanged;
    }

    private void UpdateCurrentPageTitle()
    {
        var currentPath = NavigationManager.ToBaseRelativePath(NavigationManager.Uri).Trim('/');
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
        Logger.LogDebug("Location changed in main layout. Uri={Uri}", e.Location);
        UpdateCurrentPageTitle();
        InvokeAsync(StateHasChanged);
    }

    private void ToggleSidebar()
    {
        isSidebarCollapsed = !isSidebarCollapsed;
    }

    public void Dispose()
    {
        Logger.LogDebug("Disposing main layout.");
        NavigationManager.LocationChanged -= OnLocationChanged;
    }
}
