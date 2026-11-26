using MyProject.Dtos.Admins;
using MyProject.Dtos.Ai;
using MyProject.Dtos.Commons;

namespace MyProject.Web.Client.Api;

/// <summary>
/// 系統管理助理（<c>api/v1/admin-assistant</c>，只限管理員）的 API 用戶端（0.10.7 起，WASM 遷移 M8）：開頁資訊、我的對話（清單、打開、改名、刪除）、
/// 送訊息與回覆核准（NDJSON 串流，每一行交給 <c>onFrame</c>）。巡檢報告 0.10.39 起改由 <see cref="SystemReportApi"/> 讀。
/// 0.10.43 起併入原本給多個 Agent 頁共用的基底（<c>AgentConversationApi</c>；0.10.22 起只剩系統管理助理）。
/// </summary>
public sealed class AdminAssistantApi
{
    private const string BaseUrl = "api/v1/admin-assistant";

    private readonly ApiClient api;

    public AdminAssistantApi(ApiClient api)
    {
        this.api = api;
    }

    public Task<ApiResult<AgentPageInfoDto>> InfoAsync() => api.GetAsync<AgentPageInfoDto>($"{BaseUrl}/info");

    public Task<ApiResult<List<AgentConversationSummaryDto>>> ListAsync() => api.GetAsync<List<AgentConversationSummaryDto>>($"{BaseUrl}/conversations");

    public Task<ApiResult<AgentConversationDto>> GetAsync(Guid conversationId) => api.GetAsync<AgentConversationDto>($"{BaseUrl}/conversations/{conversationId}");

    public Task<ApiResult<object>> RenameAsync(Guid conversationId, string title)
        => api.PutAsync<object>($"{BaseUrl}/conversations/{conversationId}/title", new AgentRenameRequestDto { Title = title });

    public Task<ApiResult<object>> DeleteAsync(Guid conversationId) => api.DeleteAsync<object>($"{BaseUrl}/conversations/{conversationId}");

    /// <summary>送出訊息（<see cref="AgentMessageRequestDto.ConversationId"/> 為 null 時開新對話）。取消＝中斷請求，伺服器取消這次執行並存檔。</summary>
    public Task<ApiResult<object>> SendAsync(AgentMessageRequestDto request, Func<AgentStreamFrameDto, Task> onFrame, CancellationToken cancellationToken)
        => api.StreamAsync($"{BaseUrl}/messages", request, onFrame, cancellationToken);

    /// <summary>回覆待核准的工具。</summary>
    public Task<ApiResult<object>> RespondAsync(Guid conversationId, AgentApprovalResponseRequestDto request, Func<AgentStreamFrameDto, Task> onFrame, CancellationToken cancellationToken)
        => api.StreamAsync($"{BaseUrl}/conversations/{conversationId}/approvals", request, onFrame, cancellationToken);
}
