using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using MyProject.Share.Helpers;

namespace MyProject.Web.Components.Layout;

public partial class MainLayout : LayoutComponentBase, IDisposable
{
    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    private IWebHostEnvironment Environment { get; set; } = default!;

    [Inject]
    private ILogger<MainLayout> Logger { get; set; } = default!;

    private const string DefaultPageTitle = "系統首頁";
    private IReadOnlyList<SidebarMenuItemModel> MenuItems { get; set; } = [];
    private string CurrentPageTitle { get; set; } = DefaultPageTitle;

    protected override void OnInitialized()
    {
        Logger.LogDebug("Initializing main layout.");
        MenuItems = LoadMenuItems();
        UpdateCurrentPageTitle();
        NavigationManager.LocationChanged += OnLocationChanged;
    }

    private IReadOnlyList<SidebarMenuItemModel> LoadMenuItems()
    {
        var menuFilePath = Path.Combine(Environment.ContentRootPath, MagicObjectHelper.Menu結構定義);
        if (!File.Exists(menuFilePath))
        {
            Logger.LogWarning("Layout menu file not found: {MenuFilePath}", menuFilePath);
            return [];
        }

        try
        {
            using var stream = File.OpenRead(menuFilePath);
            var items = JsonSerializer.Deserialize<List<SidebarMenuItemModel>>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? [];

            Logger.LogInformation("Loaded layout menu successfully. ItemCount={ItemCount}", items.Count);
            return items;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load layout menu from {MenuFilePath}", menuFilePath);
            return [];
        }
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

    public void Dispose()
    {
        Logger.LogDebug("Disposing main layout.");
        NavigationManager.LocationChanged -= OnLocationChanged;
    }
}
