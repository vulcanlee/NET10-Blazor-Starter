using MyProject.Dtos.Models;
using MyProject.Models.AdapterModel;

namespace MyProject.Web.Client.Api;

/// <summary>
/// 團隊的 API 資料（<see cref="TeamDto"/>）與畫面資料（<see cref="TeamAdapterModel"/>）互轉（0.10.2 起，WASM 遷移 M3）。
/// WASM 頁面與伺服器的 <c>TeamController</c> 共用這一份。
/// </summary>
public static class TeamDtoMapper
{
    public static TeamDto ToDto(TeamAdapterModel model) => new()
    {
        Id = model.Id,
        Name = model.Name,
        Code = model.Code,
        Description = model.Description,
        ParentId = model.ParentId,
        IsEnabled = model.IsEnabled,
        CreatedAt = model.CreatedAt,
        UpdatedAt = model.UpdatedAt,
        ConcurrencyStamp = model.ConcurrencyStamp,
        IsDeleted = model.IsDeleted,
        DeletedAt = model.DeletedAt,
        DeletedBy = model.DeletedBy,
    };

    public static TeamAdapterModel ToModel(TeamCreateUpdateDto dto)
    {
        var model = new TeamAdapterModel
        {
            Id = dto.Id ?? 0,
            Name = dto.Name,
            Code = dto.Code,
            Description = dto.Description,
            ParentId = dto.ParentId,
            IsEnabled = dto.IsEnabled,
            ConcurrencyStamp = dto.ConcurrencyStamp ?? string.Empty,
            CreatedAt = dto.CreatedAt ?? default,
            UpdatedAt = dto.UpdatedAt ?? default,
        };

        if (dto is TeamDto full)
        {
            model.IsDeleted = full.IsDeleted;
            model.DeletedAt = full.DeletedAt;
            model.DeletedBy = full.DeletedBy;
        }

        return model;
    }
}
