namespace MyProject.Web.Components.Layout;

public sealed class SidebarMenuItemModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public string? Url { get; set; }
    public List<SidebarMenuItemModel> SubMenu { get; set; } = [];

    public bool HasChildren => SubMenu.Count > 0;
}
