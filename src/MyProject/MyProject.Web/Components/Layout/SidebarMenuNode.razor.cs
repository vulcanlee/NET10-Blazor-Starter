using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
namespace MyProject.Web.Components.Layout;

public partial class SidebarMenuNode : ComponentBase
{
    [Parameter, EditorRequired]
    public SidebarMenuItemModel Item { get; set; } = default!;

    [Parameter]
    public int Level { get; set; }

    [Parameter, EditorRequired]
    public string ItemKey { get; set; } = default!;

    private string GetMenuKey()
    {
        return ItemKey;
    }

    private NavLinkMatch GetMatchMode()
    {
        return string.Equals(Item.Url, "/", StringComparison.Ordinal) ? NavLinkMatch.All : NavLinkMatch.Prefix;
    }

    private string GetMaterialIconKind()
    {
        return Item.Icon switch
        {
            "home" => "home",
            "dashboard" => "dashboard",
            "admin" => "admin_panel_settings",
            "users" => "group",
            "roles" => "verified_user",
            "setting" => "settings",
            _ when Item.HasChildren => "folder_open",
            _ => "article"
        };
    }
}
