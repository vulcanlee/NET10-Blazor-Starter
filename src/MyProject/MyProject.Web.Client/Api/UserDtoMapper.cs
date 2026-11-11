using MyProject.Dtos.Admins;
using MyProject.Models.AdapterModel;

namespace MyProject.Web.Client.Api;

/// <summary>
/// 使用者的 API 資料（<see cref="UserDto"/>）與畫面資料（<see cref="MyUserAdapterModel"/>）互轉（0.10.4 起，WASM 遷移 M5）。
/// WASM 頁面與伺服器的 <c>UsersController</c> 共用這一份。讀取的資料一律不帶密碼；寫入時密碼留白表示不改。
/// </summary>
public static class UserDtoMapper
{
    public static MyUserAdapterModel ToModel(UserDto dto) => new()
    {
        Id = dto.Id,
        Account = dto.Account,
        Name = dto.Name,
        Email = dto.Email,
        Password = dto.Password ?? string.Empty,
        Status = dto.Status,
        IsAdmin = dto.IsAdmin,
        MustChangePassword = dto.MustChangePassword,
        RoleViewId = dto.RoleViewId,
        RoleView = dto.RoleViewId is { } roleId ? new RoleViewAdapterModel { Id = roleId, Name = dto.RoleViewName } : null,
        AdditionalRoleIds = [.. dto.AdditionalRoleIds],
        TeamNames = [.. dto.TeamNames],
        TwoFactorEnabled = dto.TwoFactorEnabled,
        LockoutEndUtc = dto.LockoutEndUtc,
        OAuthProvider = dto.OAuthProvider,
        CreateAt = dto.CreateAt,
        UpdateAt = dto.UpdateAt,
        ConcurrencyStamp = dto.ConcurrencyStamp,
        IsDeleted = dto.IsDeleted,
        DeletedAt = dto.DeletedAt,
        DeletedBy = dto.DeletedBy,
    };

    /// <summary>畫面資料轉成要送出的 DTO（只帶欄位，<c>Can*</c> 與鎖定文字由伺服器算）。</summary>
    public static UserDto ToDto(MyUserAdapterModel model) => new()
    {
        Id = model.Id,
        Account = model.Account,
        Name = model.Name,
        Email = model.Email,
        Password = string.IsNullOrEmpty(model.Password) ? null : model.Password,
        Status = model.Status,
        IsAdmin = model.IsAdmin,
        MustChangePassword = model.MustChangePassword,
        RoleViewId = model.RoleViewId,
        RoleViewName = model.RoleViewName,
        AdditionalRoleIds = [.. model.AdditionalRoleIds],
        TeamNames = [.. model.TeamNames],
        TwoFactorEnabled = model.TwoFactorEnabled,
        LockoutEndUtc = model.LockoutEndUtc,
        OAuthProvider = model.OAuthProvider,
        CreateAt = model.CreateAt,
        UpdateAt = model.UpdateAt,
        ConcurrencyStamp = model.ConcurrencyStamp,
        IsDeleted = model.IsDeleted,
        DeletedAt = model.DeletedAt,
        DeletedBy = model.DeletedBy,
    };
}
