namespace MyProject.Models.Admins;

public class RolePermission
{
    public List<RolePermissionNode> Permissions { get; set; } = new();
}

public class RolePermissionNode
{
    public string Name { get; set; }
    public bool Enable { get; set; } = false;
}
