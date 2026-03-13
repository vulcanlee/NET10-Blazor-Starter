using MyProject.Models.Admins;
using MyProject.Share.Helpers;
using System;
using System.Collections.Generic;
using System.Text;

namespace MyProject.Business.Services.Other;

public class RolePermissionService
{
    public List<List<string>> GetRoleListPermissionAllName()
    {
        return new List<List<string>>()
        {
            new List<string>()
            {
                MagicObjectHelper.角色_首頁,
            },
            new List<string>()
            {
                MagicObjectHelper.角色_專案管理,
                MagicObjectHelper.角色_專案項目,
                MagicObjectHelper.角色_工作項目,
                MagicObjectHelper.角色_會議項目,
            },
            new List<string>()
            {
                MagicObjectHelper.角色_系統管理,
                MagicObjectHelper.角色_使用者管理,
                MagicObjectHelper.角色_角色管理,
                MagicObjectHelper.角色_會議項目,
            },
            new List<string>()
            {
                MagicObjectHelper.角色_登出,
            },
        };
    }

    public List<string> GetRolePermissionAllName()
    {
        return new List<string>
            {
                MagicObjectHelper.使用者角色,
            };
    }

    public List<string> GetGet預設新建帳號角色ToJsonPermissionAllName()
    {
        return new List<string>
            {
                MagicObjectHelper.使用者角色,
            };
    }

    public RolePermission InitializePermissionSetting()
    {
        var allPermisssionName = GetRolePermissionAllName();
        var result = new RolePermission();
        foreach (var item in allPermisssionName)
        {
            result.Permissions.Add(new RolePermissionNode
            {
                Name = item,
                Enable = false,
            });
        }
        return result;
    }

    public void SetPermissionInput(RolePermission rolePermission, List<string> permissions)
    {
        foreach (var item in rolePermission.Permissions)
        {
            item.Enable = permissions.Contains(item.Name);
        }
    }

    public List<string> GetPermissionInput(RolePermission rolePermission)
    {
        return rolePermission.Permissions.Where(x => x.Enable).Select(x => x.Name).ToList();
    }

    public string GetPermissionInputToJson(RolePermission rolePermission)
    {
        var items = GetPermissionInput(rolePermission);
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(items);
        return json;
    }
}
