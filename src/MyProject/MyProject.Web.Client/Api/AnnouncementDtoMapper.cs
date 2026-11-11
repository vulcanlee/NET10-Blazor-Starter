using MyProject.Dtos.Admins;
using MyProject.Models.AdapterModel;

namespace MyProject.Web.Client.Api;

/// <summary>公告的 DTO 與畫面模型互轉（0.10.6 起；WASM 頁面與伺服器的 <c>AnnouncementsController</c> 共用同一份）。</summary>
public static class AnnouncementDtoMapper
{
    /// <summary>起訖與更新時間是伺服器的本地時間，以不帶時差送出（帶了 +08:00，WASM 會換成瀏覽器的時區）。</summary>
    public static AnnouncementDto ToDto(AnnouncementAdapterModel model) => new()
    {
        Id = model.Id,
        Title = model.Title,
        Content = model.Content,
        StartAt = Unzoned(model.StartAt),
        EndAt = model.EndAt is { } end ? Unzoned(end) : null,
        TargetKind = model.TargetKind,
        TargetId = model.TargetId,
        TargetName = model.TargetName,
        CreatedByAccount = model.CreatedByAccount,
        UpdatedAt = Unzoned(model.UpdatedAt),
        ConcurrencyStamp = model.ConcurrencyStamp,
    };

    public static AnnouncementAdapterModel ToModel(AnnouncementDto dto) => new()
    {
        Id = dto.Id,
        Title = dto.Title,
        Content = dto.Content,
        StartAt = Unzoned(dto.StartAt),
        EndAt = dto.EndAt is { } end ? Unzoned(end) : null,
        TargetKind = dto.TargetKind,
        TargetId = dto.TargetId,
        TargetName = dto.TargetName,
        CreatedByAccount = dto.CreatedByAccount,
        UpdatedAt = Unzoned(dto.UpdatedAt),
        ConcurrencyStamp = dto.ConcurrencyStamp,
    };

    private static DateTime Unzoned(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
}
