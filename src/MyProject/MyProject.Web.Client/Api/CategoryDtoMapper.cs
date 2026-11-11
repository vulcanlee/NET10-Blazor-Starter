using MyProject.Business.Helpers;
using MyProject.Dtos.Models;
using MyProject.Models.AdapterModel;

namespace MyProject.Web.Client.Api;

/// <summary>
/// 分類的 API 資料（<see cref="CategoryDto"/>）與畫面資料（<see cref="CategoryAdapterModel"/>）互轉（0.10.2 起，WASM 遷移 M3）。
/// WASM 頁面與伺服器的 <c>CategoryController</c> 共用這一份，兩邊不會各寫一套而走樣。
/// 「適用團隊」在 API 上是儲存格式的字串（以換行包夾，見 <see cref="TagStringHelper"/>），畫面上是清單。
/// </summary>
public static class CategoryDtoMapper
{
    public static CategoryDto ToDto(CategoryAdapterModel model) => new()
    {
        Id = model.Id,
        Name = model.Name,
        Description = model.Description,
        Teams = TagStringHelper.ToStored(model.Teams),
        IsEnabled = model.IsEnabled,
        CreatedAt = model.CreatedAt,
        UpdatedAt = model.UpdatedAt,
        ConcurrencyStamp = model.ConcurrencyStamp,
        IsDeleted = model.IsDeleted,
        DeletedAt = model.DeletedAt,
        DeletedBy = model.DeletedBy,
    };

    public static CategoryAdapterModel ToModel(CategoryCreateUpdateDto dto)
    {
        var model = new CategoryAdapterModel
        {
            Id = dto.Id ?? 0,
            Name = dto.Name,
            Description = dto.Description,
            Teams = TagStringHelper.ToList(dto.Teams),
            IsEnabled = dto.IsEnabled,
            ConcurrencyStamp = dto.ConcurrencyStamp ?? string.Empty,
            CreatedAt = dto.CreatedAt ?? default,
            UpdatedAt = dto.UpdatedAt ?? default,
        };

        if (dto is CategoryDto full)
        {
            model.IsDeleted = full.IsDeleted;
            model.DeletedAt = full.DeletedAt;
            model.DeletedBy = full.DeletedBy;
        }

        return model;
    }
}
