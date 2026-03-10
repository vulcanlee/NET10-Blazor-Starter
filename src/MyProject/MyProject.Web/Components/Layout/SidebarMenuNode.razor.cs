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

    [Parameter]
    public string? ActiveMenuPath { get; set; }

    [Parameter]
    public int RouteVersion { get; set; }

    private bool _isExpanded;
    private int _lastAppliedRouteVersion;

    protected override void OnParametersSet()
    {
        if (_lastAppliedRouteVersion == RouteVersion)
        {
            return;
        }

        _lastAppliedRouteVersion = RouteVersion;
        _isExpanded = ShouldAutoExpand();
    }

    private string GetMenuKey()
    {
        return ItemKey;
    }

    private NavLinkMatch GetMatchMode()
    {
        return string.Equals(Item.Url, "/", StringComparison.Ordinal) ? NavLinkMatch.All : NavLinkMatch.Prefix;
    }

    private void ToggleExpand()
    {
        _isExpanded = !_isExpanded;
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

    private bool ShouldAutoExpand()
    {
        return Item.HasChildren
            && !string.IsNullOrWhiteSpace(ActiveMenuPath)
            && ActiveMenuPath.StartsWith($"{ItemKey}-", StringComparison.Ordinal);
    }
}
