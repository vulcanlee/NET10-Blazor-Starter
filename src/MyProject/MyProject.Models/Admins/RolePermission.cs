namespace MyProject.Models.Admins;

public class RolePermission
{
    public List<RolePermissionGroup> Groups { get; set; } = new();

    public RolePermission Clone()
    {
        return new RolePermission
        {
            Groups = Groups.Select(group => group.Clone()).ToList()
        };
    }
}

public class RolePermissionGroup
{
    public string Name { get; set; } = string.Empty;
    public bool Enable { get; set; } = false;
    public List<RolePermissionNode> Permissions { get; set; } = new();

    public RolePermissionGroup Clone()
    {
        return new RolePermissionGroup
        {
            Name = Name,
            Enable = Enable,
            Permissions = Permissions.Select(permission => permission.Clone()).ToList()
        };
    }
}

public class RolePermissionNode
{
    public string Name { get; set; } = string.Empty;
    public bool Enable { get; set; } = false;

    public RolePermissionNode Clone()
    {
        return new RolePermissionNode
        {
            Name = Name,
            Enable = Enable
        };
    }
}
