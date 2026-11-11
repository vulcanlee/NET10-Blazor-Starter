using MyProject.Dtos.Admins;
using MyProject.Models.AdapterModel;
using MyProject.Models.Admins;

namespace MyProject.Web.Client.Api;

/// <summary>
/// 角色的 API 資料（<see cref="RoleDto"/>）與畫面資料（<see cref="RoleViewAdapterModel"/>）互轉（0.10.4 起，WASM 遷移 M5）。
/// WASM 頁面與伺服器的 <c>RolesController</c> 共用這一份；權限矩陣逐層複製（不共用參考，修改窗的勾選不會動到清單上的資料）。
/// </summary>
public static class RoleDtoMapper
{
    public static RoleDto ToDto(RoleViewAdapterModel model) => new()
    {
        Id = model.Id,
        Name = model.Name,
        RequireTwoFactor = model.RequireTwoFactor,
        DefaultTeams = [.. model.DefaultTeams],
        Permission = ToDto(model.RolePermission),
        CreateAt = model.CreateAt,
        UpdateAt = model.UpdateAt,
        ConcurrencyStamp = model.ConcurrencyStamp,
        IsDeleted = model.IsDeleted,
        DeletedAt = model.DeletedAt,
        DeletedBy = model.DeletedBy,
    };

    public static RoleViewAdapterModel ToModel(RoleDto dto) => new()
    {
        Id = dto.Id,
        Name = dto.Name,
        RequireTwoFactor = dto.RequireTwoFactor,
        DefaultTeams = [.. dto.DefaultTeams],
        RolePermission = ToModel(dto.Permission),
        CreateAt = dto.CreateAt,
        UpdateAt = dto.UpdateAt,
        ConcurrencyStamp = dto.ConcurrencyStamp,
        IsDeleted = dto.IsDeleted,
        DeletedAt = dto.DeletedAt,
        DeletedBy = dto.DeletedBy,
    };

    public static RolePermissionDto ToDto(RolePermission permission) => new()
    {
        Groups = permission.Groups.Select(group => new RolePermissionGroupDto
        {
            Name = group.Name,
            Enable = group.Enable,
            Permissions = group.Permissions.Select(node => new RolePermissionNodeDto
            {
                Name = node.Name,
                Enable = node.Enable,
                Actions = new Dictionary<string, bool>(node.Actions ?? []),
            }).ToList(),
        }).ToList(),
    };

    public static RolePermission ToModel(RolePermissionDto? permission) => new()
    {
        Groups = (permission?.Groups ?? []).Select(group => new RolePermissionGroup
        {
            Name = group.Name,
            Enable = group.Enable,
            Permissions = (group.Permissions ?? []).Select(node => new RolePermissionNode
            {
                Name = node.Name,
                Enable = node.Enable,
                Actions = new Dictionary<string, bool>(node.Actions ?? []),
            }).ToList(),
        }).ToList(),
    };
}
