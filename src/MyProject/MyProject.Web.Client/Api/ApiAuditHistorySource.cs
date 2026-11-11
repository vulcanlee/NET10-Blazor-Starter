using MyProject.Dtos.Commons;
using MyProject.Models.AdapterModel;
using MyProject.Web.Components.Commons;
using MyProject.Web.Diagnostics;

namespace MyProject.Web.Client.Api;

/// <summary>
/// WASM 頁面的異動歷程資料來源（0.10.2 起，WASM 遷移 M3）：<c>/api/v1/audit-logs</c>（只限管理員）。
/// 稽核由伺服器寫，寫法與 Server 頁面相同（打開時一筆搜尋、點一筆時一筆查看）。
/// </summary>
public sealed class ApiAuditHistorySource : IAuditHistorySource
{
    /// <summary>
    /// 對話窗只看「有沒有詳細內容」（<see cref="AuditLogAdapterModel.ContentFile"/> 是否為 null）；
    /// 伺服器上的檔案路徑不外流，WASM 端以這個標記代替。
    /// </summary>
    internal const string ContentOnServer = "(server)";

    private readonly ApiClient api;

    public ApiAuditHistorySource(ApiClient api) => this.api = api;

    public async Task<AuditHistoryPage> LoadAsync(string targetType, string targetId, int pageIndex, int pageSize, bool opening)
    {
        var url = $"api/v1/audit-logs/history?targetType={Uri.EscapeDataString(targetType)}&targetId={Uri.EscapeDataString(targetId)}"
            + $"&page={pageIndex}&pageSize={pageSize}&opened={(opening ? "true" : "false")}";
        var result = await api.GetAsync<PagedResult<AuditHistoryItemDto>>(url);
        if (!result.Success || result.Data is null)
        {
            throw new AuditHistoryUnavailableException(MessageOf(result, "載入異動歷程失敗。"));
        }

        return new AuditHistoryPage(result.Data.Items.Select(ToModel).ToList(), result.Data.TotalCount);
    }

    public async Task<string> ReadContentAsync(AuditLogAdapterModel item)
    {
        var result = await api.GetAsync<AuditContentDto>($"api/v1/audit-logs/{item.Id}/content");
        return result.Success && result.Data is not null
            ? result.Data.Text
            : "讀取詳細內容失敗。" + TraceCodeText.Suffix(result.TraceId);
    }

    internal static AuditLogAdapterModel ToModel(AuditHistoryItemDto dto) => new()
    {
        Id = dto.Id,
        OccurredAt = dto.OccurredAt,
        ActorUserId = dto.ActorUserId,
        ActorAccount = dto.ActorAccount,
        Action = dto.Action,
        TargetType = dto.TargetType,
        TargetId = dto.TargetId,
        Detail = dto.Detail,
        Success = dto.Success,
        ClientIp = dto.ClientIp,
        ContentFile = dto.HasContent ? ContentOnServer : null,
    };

    private static string MessageOf<T>(ApiResult<T> result, string fallback)
        => !string.IsNullOrWhiteSpace(result.ErrorMessage) ? result.ErrorMessage
            : !string.IsNullOrWhiteSpace(result.Message) ? result.Message
            : fallback;
}
