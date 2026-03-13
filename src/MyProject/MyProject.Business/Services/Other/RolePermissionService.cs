using MyProject.Models.Admins;
using MyProject.Share.Helpers;

namespace MyProject.Business.Services.Other;

public class RolePermissionService
{
    public List<List<string>> GetRoleListPermissionAllName()
    {
        return
        [
            [MagicObjectHelper.角色_首頁],
            [
                MagicObjectHelper.角色_專案管理,
                MagicObjectHelper.角色_專案項目,
                MagicObjectHelper.角色_工作項目,
                MagicObjectHelper.角色_會議項目,
            ],
            [
                MagicObjectHelper.角色_系統管理,
                MagicObjectHelper.角色_使用者管理,
                MagicObjectHelper.角色_角色管理,
            ],
            [MagicObjectHelper.角色_登出],
        ];
    }

    public RolePermission InitializePermissionSetting()
    {
        var result = new RolePermission();

        foreach (var permissionNames in GetRoleListPermissionAllName())
        {
            if (permissionNames.Count == 0)
            {
                continue;
            }

            var group = new RolePermissionGroup
            {
                Name = permissionNames[0],
                Enable = false,
            };

            foreach (var item in permissionNames.Skip(1))
            {
                group.Permissions.Add(new RolePermissionNode
                {
                    Name = item,
                    Enable = false,
                });
            }

            result.Groups.Add(group);
        }

        return result;
    }

    public void SetPermissionInput(RolePermission rolePermission, List<string> permissions)
    {
        var permissionLookup = permissions.ToHashSet(StringComparer.Ordinal);

        foreach (var group in rolePermission.Groups)
        {
            group.Enable = permissionLookup.Contains(group.Name);

            foreach (var item in group.Permissions)
            {
                item.Enable = permissionLookup.Contains(item.Name);
            }
        }
    }

    public List<string> GetPermissionInput(RolePermission rolePermission)
    {
        return rolePermission.Groups
            .SelectMany(group => group.Enable
                ? new[] { group.Name }.Concat(group.Permissions.Where(x => x.Enable).Select(x => x.Name))
                : group.Permissions.Where(x => x.Enable).Select(x => x.Name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public string GetPermissionInputToJson(RolePermission rolePermission)
    {
        var items = GetPermissionInput(rolePermission);
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(items);
        return json;
    }
}
