using MyProject.Models.Admins;
using MyProject.Share.Helpers;
using System;
using System.Collections.Generic;
using System.Text;

namespace MyProject.Business.Services.Other;

public class RolePermissionService
{
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
    public string GetRolePermissionAllNameToJson()
    {
        var items = GetRolePermissionAllName();
        return Newtonsoft.Json.JsonConvert.SerializeObject(items);
    }

    public string Get預設新建帳號角色ToJson()
    {
        var items = GetGet預設新建帳號角色ToJsonPermissionAllName();
        return Newtonsoft.Json.JsonConvert.SerializeObject(items);
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
