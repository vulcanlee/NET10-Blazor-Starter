using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace MyProject.Web.Components.Layout;

public partial class SidebarMenuNode : ComponentBase, IDisposable
{
    [Inject]
    private NavigationManager NavigationManager { get; set; } = default!;

    [Parameter, EditorRequired]
    public SidebarMenuItemModel Item { get; set; } = default!;

    [Parameter]
    public int Level { get; set; }

    private bool _isExpanded;

    protected override void OnInitialized()
    {
        NavigationManager.LocationChanged += OnLocationChanged;
        _isExpanded = ShouldAutoExpand();
    }

    protected override void OnParametersSet()
    {
        if (ShouldAutoExpand())
        {
            _isExpanded = true;
        }
    }

    private string GetIndentStyle()
    {
        var paddingLeft = 1 + (Level * 1.25m);
        return $"padding-left: {paddingLeft}rem;";
    }

    private NavLinkMatch GetMatchMode()
    {
        return string.Equals(Item.Url, "/", StringComparison.Ordinal) ? NavLinkMatch.All : NavLinkMatch.Prefix;
    }

    private void ToggleExpand()
    {
        _isExpanded = !_isExpanded;
    }

    private string GetExpandedCssClass()
    {
        return _isExpanded ? "expanded" : string.Empty;
    }

    private string GetIconType()
    {
        return Item.Icon switch
        {
            "home" => "home",
            "dashboard" => "dashboard",
            "admin" => "appstore",
            "users" => "team",
            "roles" => "safety-certificate",
            "setting" => "setting",
            _ when Item.HasChildren => "folder-open",
            _ => "file-text"
        };
    }

    private bool ShouldAutoExpand()
    {
        return Item.HasChildren && HasActiveDescendant(Item);
    }

    private bool HasActiveDescendant(SidebarMenuItemModel menuItem)
    {
        foreach (var child in menuItem.SubMenu)
        {
            if (IsCurrentUrl(child.Url) || (child.HasChildren && HasActiveDescendant(child)))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsCurrentUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var currentPath = NavigationManager.ToBaseRelativePath(NavigationManager.Uri).Trim('/');
        var targetPath = NavigationManager.ToBaseRelativePath(NavigationManager.ToAbsoluteUri(url).ToString()).Trim('/');

        if (string.IsNullOrEmpty(targetPath))
        {
            return string.IsNullOrEmpty(currentPath);
        }

        return string.Equals(currentPath, targetPath, StringComparison.OrdinalIgnoreCase)
            || currentPath.StartsWith($"{targetPath}/", StringComparison.OrdinalIgnoreCase);
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        _isExpanded = ShouldAutoExpand();
        InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        NavigationManager.LocationChanged -= OnLocationChanged;
    }
}
