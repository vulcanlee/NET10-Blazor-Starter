using System.Text.Json;
using MyProject.Business.Services.Other;
using MyProject.Share.Helpers;

namespace MyProject.Web.Components.Layout;

public sealed class SidebarMenuService
{
    private readonly IWebHostEnvironment environment;
    private readonly ILogger<SidebarMenuService> logger;
    private readonly RolePermissionService rolePermissionService;

    public SidebarMenuService(
        IWebHostEnvironment environment,
        ILogger<SidebarMenuService> logger,
        RolePermissionService rolePermissionService)
    {
        this.environment = environment;
        this.logger = logger;
        this.rolePermissionService = rolePermissionService;
    }

    public IReadOnlyList<SidebarMenuItemModel> LoadAuthorizedMenuItems(AuthenticationStateHelper authenticationStateHelper)
    {
        var items = LoadMenuItems();
        var permissionMappedItems = ApplyPermissionStructure(items);
        var authorizedItems = FilterAuthorizedMenuItems(permissionMappedItems, authenticationStateHelper);

        logger.LogInformation("Loaded authorized sidebar menu successfully. ItemCount={ItemCount}", authorizedItems.Count);
        return authorizedItems;
    }

    private IReadOnlyList<SidebarMenuItemModel> LoadMenuItems()
    {
        var menuFilePath = Path.Combine(environment.ContentRootPath, MagicObjectHelper.Menu結構定義);
        if (!File.Exists(menuFilePath))
        {
            logger.LogWarning("Sidebar menu file not found: {MenuFilePath}", menuFilePath);
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
            logger.LogError(ex, "Failed to load sidebar menu from {MenuFilePath}", menuFilePath);
            return [];
        }
    }

    private IReadOnlyList<SidebarMenuItemModel> ApplyPermissionStructure(IReadOnlyList<SidebarMenuItemModel> items)
    {
        var permissionGroups = rolePermissionService.GetRoleListPermissionAllName();
        var result = new List<SidebarMenuItemModel>(items.Count);

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var permissionGroup = index < permissionGroups.Count ? permissionGroups[index] : [];
            var permissionName = permissionGroup.FirstOrDefault() ?? item.Name;
            var childPermissions = permissionGroup.Skip(1).ToList();
            var subMenu = ApplyChildPermissionStructure(item.SubMenu, childPermissions);

            result.Add(item.CloneWith(subMenu, permissionName));
        }

        return result;
    }

    private List<SidebarMenuItemModel> ApplyChildPermissionStructure(
        IReadOnlyList<SidebarMenuItemModel> items,
        IReadOnlyList<string> permissionNames)
    {
        var result = new List<SidebarMenuItemModel>(items.Count);

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var permissionName = index < permissionNames.Count ? permissionNames[index] : item.Name;

            result.Add(item.CloneWith([], permissionName));
        }

        return result;
    }

    private List<SidebarMenuItemModel> FilterAuthorizedMenuItems(
        IReadOnlyList<SidebarMenuItemModel> items,
        AuthenticationStateHelper authenticationStateHelper)
    {
        var result = new List<SidebarMenuItemModel>(items.Count);

        foreach (var item in items)
        {
            var filteredChildren = item.HasChildren
                ? FilterAuthorizedMenuItems(item.SubMenu, authenticationStateHelper)
                : [];

            var permissionNames = GetPermissionNames(item);
            var hasPermission = permissionNames.Count == 0
                || permissionNames.Any(authenticationStateHelper.CheckAccessPage);

            if (!hasPermission && filteredChildren.Count == 0)
            {
                continue;
            }

            result.Add(item.CloneWith(filteredChildren));
        }

        return result;
    }

    private static List<string> GetPermissionNames(SidebarMenuItemModel item)
    {
        var result = new List<string>();

        if (!string.IsNullOrWhiteSpace(item.Name))
        {
            result.Add(item.Name);
        }

        if (!string.IsNullOrWhiteSpace(item.PermissionName)
            && result.Contains(item.PermissionName, StringComparer.Ordinal) == false)
        {
            result.Add(item.PermissionName);
        }

        return result;
    }
}
