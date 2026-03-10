using System.Text.Json;
using Microsoft.AspNetCore.Components;
using MyProject.Share.Helpers;

namespace MyProject.Web.Components.Layout;

public partial class NavMenu : ComponentBase
{
    [Inject]
    private IWebHostEnvironment Environment { get; set; } = default!;

    [Inject]
    private ILogger<NavMenu> Logger { get; set; } = default!;

    private IReadOnlyList<SidebarMenuItemModel> MenuItems { get; set; } = [];

    protected override void OnInitialized()
    {
        MenuItems = LoadMenuItems();
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
}
