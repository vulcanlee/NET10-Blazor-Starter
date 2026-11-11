using MyProject.Dtos.Admins;
using MyProject.Models.AdapterModel;

namespace MyProject.Web.Client.Api;

/// <summary>
/// 系統例外紀錄的 API 資料（<see cref="ExceptionLogItemDto"/>）與畫面資料（<see cref="ExceptionLogAdapterModel"/>）互轉（0.10.5 起，WASM 遷移 M6）。
/// WASM 頁面與伺服器的 <c>ExceptionLogsController</c> 共用這一份。簽章與堆疊檔名是伺服器內部鍵值，不送出。
/// </summary>
public static class ExceptionLogDtoMapper
{
    /// <summary>伺服器用：本地時間以「不帶時差」送出（帶了 +08:00，WASM 會換成瀏覽器的時區）。</summary>
    public static T ToDto<T>(ExceptionLogAdapterModel model, T dto)
        where T : ExceptionLogItemDto
    {
        dto.Id = model.Id;
        dto.ExceptionType = model.ExceptionType;
        dto.Message = model.Message;
        dto.Source = model.Source;
        dto.Page = model.Page;
        dto.Operation = model.Operation;
        dto.LoggerName = model.LoggerName;
        dto.Account = model.Account;
        dto.UserId = model.UserId;
        dto.OccurrenceCount = model.OccurrenceCount;
        dto.FirstOccurredAt = DateTime.SpecifyKind(model.FirstOccurredAt, DateTimeKind.Unspecified);
        dto.LastOccurredAt = DateTime.SpecifyKind(model.LastOccurredAt, DateTimeKind.Unspecified);
        dto.LastTraceId = model.LastTraceId;
        return dto;
    }

    public static ExceptionLogItemDto ToDto(ExceptionLogAdapterModel model) => ToDto(model, new ExceptionLogItemDto());

    public static ExceptionLogAdapterModel ToModel(ExceptionLogItemDto dto) => new()
    {
        Id = dto.Id,
        ExceptionType = dto.ExceptionType,
        Message = dto.Message,
        Source = dto.Source,
        Page = dto.Page,
        Operation = dto.Operation,
        LoggerName = dto.LoggerName,
        Account = dto.Account,
        UserId = dto.UserId,
        OccurrenceCount = dto.OccurrenceCount,
        FirstOccurredAt = dto.FirstOccurredAt,
        LastOccurredAt = dto.LastOccurredAt,
        LastTraceId = dto.LastTraceId,
    };
}
