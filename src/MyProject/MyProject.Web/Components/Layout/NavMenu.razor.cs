using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;
using MyProject.Business.Services.Other;

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

    private IReadOnlyList<SidebarMenuItemModel> MenuItems { get; set; } = [];
    private string? ActiveMenuPath { get; set; }
    private string[] OpenKeys { get; set; } = [];
    private string[] SelectedKeys { get; set; } = [];
    private bool previousSidebarCollapsed;

    protected override async Task OnInitializedAsync()
    {
        Logger.LogDebug("Initializing navigation menu.");

        var checkResult = await AuthenticationStateHelper.Check(AuthenticationStateProvider, NavigationManager);
        if (!checkResult)
        {
            MenuItems = [];
            return;
        }

        MenuItems = SidebarMenuService.LoadAuthorizedMenuItems(AuthenticationStateHelper);
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
        Logger.LogDebug("Navigation menu location changed. Uri={Uri}", e.Location);
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
