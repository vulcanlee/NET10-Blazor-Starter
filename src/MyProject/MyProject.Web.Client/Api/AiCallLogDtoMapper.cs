using MyProject.Dtos.Ai;
using MyProject.Models.AdapterModel;

namespace MyProject.Web.Client.Api;

/// <summary>AI 對話紀錄的 DTO 與畫面模型互轉（0.10.5 起；WASM 頁面與伺服器的 <c>AiCallLogsController</c> 共用同一份）。</summary>
public static class AiCallLogDtoMapper
{
    /// <summary>送出時間是伺服器的本地時間，以不帶時差送出（帶了 +08:00，WASM 會換成瀏覽器的時區）。</summary>
    public static AiCallLogItemDto ToDto(AiCallLogAdapterModel item) => new()
    {
        Id = item.Id,
        CallId = item.CallId,
        OccurredAt = DateTime.SpecifyKind(item.OccurredAt, DateTimeKind.Unspecified),
        Operation = item.Operation,
        Provider = item.Provider,
        Model = item.Model,
        Account = item.Account,
        UserId = item.UserId,
        Success = item.Success,
        FailureReason = item.FailureReason,
        HttpStatus = item.HttpStatus,
        FinishReason = item.FinishReason,
        ElapsedMilliseconds = item.ElapsedMilliseconds,
        RequestCharacters = item.RequestCharacters,
        ResponseCharacters = item.ResponseCharacters,
        RelatedInfo = item.RelatedInfo,
        ConversationId = item.ConversationId,
        RunId = item.RunId,
        HasContent = item.HasContent,
    };

    public static AiCallLogAdapterModel ToModel(AiCallLogItemDto dto) => new()
    {
        Id = dto.Id,
        CallId = dto.CallId,
        OccurredAt = dto.OccurredAt,
        Operation = dto.Operation,
        Provider = dto.Provider,
        Model = dto.Model,
        Account = dto.Account,
        UserId = dto.UserId,
        Success = dto.Success,
        FailureReason = dto.FailureReason,
        HttpStatus = dto.HttpStatus,
        FinishReason = dto.FinishReason,
        ElapsedMilliseconds = dto.ElapsedMilliseconds,
        RequestCharacters = dto.RequestCharacters,
        ResponseCharacters = dto.ResponseCharacters,
        RelatedInfo = dto.RelatedInfo,
        ConversationId = dto.ConversationId,
        RunId = dto.RunId,
        HasContent = dto.HasContent,
    };
}
