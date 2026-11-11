using MyProject.Business.Helpers;
using MyProject.Dtos.Models;
using MyProject.Models.AdapterModel;

namespace MyProject.Web.Client.Api;

/// <summary>
/// 專案的 API 資料（<see cref="ProjectDto"/>）與畫面資料（<see cref="ProjectAdapterModel"/>）互轉（0.10.3 起，WASM 遷移 M4）。
/// WASM 頁面與伺服器的 <c>ProjectController</c> 共用這一份。分類、團隊在 API 上是儲存格式的字串（以換行包夾，見 <see cref="TagStringHelper"/>），畫面上是清單。
/// 附件只帶給畫面需要的欄位：實體檔的儲存位置（<c>RelativePath</c>、<c>StoredFileName</c>）不出伺服器。
/// </summary>
public static class ProjectDtoMapper
{
    public static ProjectDto ToDto(ProjectAdapterModel model) => new()
    {
        Id = model.Id,
        Title = model.Title,
        Description = model.Description,
        StartDate = model.StartDate,
        EndDate = model.EndDate,
        Status = model.Status,
        Priority = model.Priority,
        CompletionPercentage = model.CompletionPercentage,
        Owner = model.Owner,
        Categories = TagStringHelper.ToStored(model.Categories),
        Teams = TagStringHelper.ToStored(model.Teams),
        CreatedAt = model.CreatedAt,
        UpdatedAt = model.UpdatedAt,
        ConcurrencyStamp = model.ConcurrencyStamp,
        IsDeleted = model.IsDeleted,
        DeletedAt = model.DeletedAt,
        DeletedBy = model.DeletedBy,
        Files = model.Files.Select(x => new ProjectFileDto
        {
            Id = x.Id,
            OriginalFileName = x.OriginalFileName,
            ContentType = x.ContentType,
            FileSize = x.FileSize,
            CreatedAt = x.CreatedAt,
        }).ToList(),
    };

    public static ProjectAdapterModel ToModel(ProjectCreateUpdateDto dto)
    {
        var model = new ProjectAdapterModel
        {
            Id = dto.Id ?? 0,
            Title = dto.Title,
            Description = dto.Description,
            StartDate = dto.StartDate,
            EndDate = dto.EndDate,
            Status = dto.Status,
            Priority = dto.Priority,
            CompletionPercentage = dto.CompletionPercentage,
            Owner = dto.Owner ?? string.Empty,
            Categories = TagStringHelper.ToList(dto.Categories),
            Teams = TagStringHelper.ToList(dto.Teams),
            ConcurrencyStamp = dto.ConcurrencyStamp ?? string.Empty,
            CreatedAt = dto.CreatedAt ?? default,
            UpdatedAt = dto.UpdatedAt ?? default,
        };

        if (dto is ProjectDto full)
        {
            model.IsDeleted = full.IsDeleted;
            model.DeletedAt = full.DeletedAt;
            model.DeletedBy = full.DeletedBy;
            model.Files = full.Files.Select(x => new ProjectFileAdapterModel
            {
                Id = x.Id,
                ProjectId = model.Id,
                OriginalFileName = x.OriginalFileName,
                ContentType = x.ContentType,
                FileSize = x.FileSize,
                CreatedAt = x.CreatedAt,
            }).ToList();
        }

        return model;
    }
}
