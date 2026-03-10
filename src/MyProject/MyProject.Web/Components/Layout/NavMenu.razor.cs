using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using MyProject.Share.Helpers;

namespace MyProject.Web.Components.Layout;

public partial class NavMenu : ComponentBase, IDisposable
{
    [Inject]
    private IWebHostEnvironment Environment { get; set; } = default!;

    [Inject]
    private ILogger<NavMenu> Logger { get; set; } = default!;

    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    private IReadOnlyList<SidebarMenuItemModel> MenuItems { get; set; } = [];
    private string? ActiveMenuPath { get; set; }
    private int RouteVersion { get; set; }

    protected override void OnInitialized()
    {
        MenuItems = LoadMenuItems();
        RouteVersion = 1;
        UpdateActiveMenuPath();
        NavigationManager.LocationChanged += OnLocationChanged;
    }

    private IReadOnlyList<SidebarMenuItemModel> LoadMenuItems()
    {
        var menuFilePath = Path.Combine(Environment.ContentRootPath, MagicObjectHelper.Menu結構定義);
        if (!File.Exists(menuFilePath))
        {
            Logger.LogWarning("Sidebar menu file not found: {MenuFilePath}", menuFilePath);
            return [];
        }

        try
        {
            using var stream = File.OpenRead(menuFilePath);
            return JsonSerializer.Deserialize<List<SidebarMenuItemModel>>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? [];
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load sidebar menu from {MenuFilePath}", menuFilePath);
            return [];
        }
    }

    private void UpdateActiveMenuPath()
    {
        var currentPath = NavigationManager.ToBaseRelativePath(NavigationManager.Uri).Trim('/');
        var normalizedCurrentPath = string.IsNullOrEmpty(currentPath) ? "/" : $"/{currentPath}";

        ActiveMenuPath = TryFindActiveMenuPath(MenuItems, normalizedCurrentPath, "root", out var activePath)
            ? activePath
            : null;
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
        RouteVersion++;
        UpdateActiveMenuPath();
        InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        NavigationManager.LocationChanged -= OnLocationChanged;
    }
}
