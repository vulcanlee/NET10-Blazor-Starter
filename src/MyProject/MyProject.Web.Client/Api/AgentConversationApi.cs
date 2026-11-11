using MyProject.Dtos.Admins;
using MyProject.Dtos.Ai;
using MyProject.Dtos.Commons;

namespace MyProject.Web.Client.Api;

/// <summary>
/// Agent 對話頁共用的 API 用戶端（0.10.7 起，WASM 遷移 M8）：開頁資訊、我的對話（清單、打開、改名、刪除）、
/// 送訊息與回覆核准（NDJSON 串流，每一行交給 <c>onFrame</c>）。兩頁只差網址。
/// </summary>
public abstract class AgentConversationApi
{
    private readonly ApiClient api;
    private readonly string baseUrl;

    protected AgentConversationApi(ApiClient api, string baseUrl)
    {
        this.api = api;
        this.baseUrl = baseUrl;
    }

    protected ApiClient Api => api;

    protected string BaseUrl => baseUrl;

    public Task<ApiResult<AgentPageInfoDto>> InfoAsync() => api.GetAsync<AgentPageInfoDto>($"{baseUrl}/info");

    public Task<ApiResult<List<AgentConversationSummaryDto>>> ListAsync() => api.GetAsync<List<AgentConversationSummaryDto>>($"{baseUrl}/conversations");

    public Task<ApiResult<AgentConversationDto>> GetAsync(Guid conversationId) => api.GetAsync<AgentConversationDto>($"{baseUrl}/conversations/{conversationId}");

    public Task<ApiResult<object>> RenameAsync(Guid conversationId, string title)
        => api.PutAsync<object>($"{baseUrl}/conversations/{conversationId}/title", new AgentRenameRequestDto { Title = title });

    public Task<ApiResult<object>> DeleteAsync(Guid conversationId) => api.DeleteAsync<object>($"{baseUrl}/conversations/{conversationId}");

    /// <summary>送出訊息（<see cref="AgentMessageRequestDto.ConversationId"/> 為 null 時開新對話）。取消＝中斷請求，伺服器取消這次執行並存檔。</summary>
    public Task<ApiResult<object>> SendAsync(AgentMessageRequestDto request, Func<AgentStreamFrameDto, Task> onFrame, CancellationToken cancellationToken)
        => api.StreamAsync($"{baseUrl}/messages", request, onFrame, cancellationToken);

    /// <summary>回覆待核准的工具。</summary>
    public Task<ApiResult<object>> RespondAsync(Guid conversationId, AgentApprovalResponseRequestDto request, Func<AgentStreamFrameDto, Task> onFrame, CancellationToken cancellationToken)
        => api.StreamAsync($"{baseUrl}/conversations/{conversationId}/approvals", request, onFrame, cancellationToken);
}

/// <summary>Agent 測試台（<c>api/v1/agent-playground</c>）：另有產生／清除測試專案。</summary>
public sealed class AgentPlaygroundApi : AgentConversationApi
{
    public AgentPlaygroundApi(ApiClient api)
        : base(api, "api/v1/agent-playground")
    {
    }

    public Task<ApiResult<AgentSampleResultDto>> GenerateSamplesAsync() => Api.PostAsync<AgentSampleResultDto>($"{BaseUrl}/samples");

    public Task<ApiResult<object>> ClearSamplesAsync() => Api.DeleteAsync<object>($"{BaseUrl}/samples");
}

/// <summary>系統管理助理（<c>api/v1/admin-assistant</c>，只限管理員）：另有巡檢報告。</summary>
public sealed class AdminAssistantApi : AgentConversationApi
{
    public AdminAssistantApi(ApiClient api)
        : base(api, "api/v1/admin-assistant")
    {
    }

    public Task<ApiResult<AdminPatrolReportListDto>> PatrolReportsAsync() => Api.GetAsync<AdminPatrolReportListDto>($"{BaseUrl}/patrol-reports");

    public Task<ApiResult<AdminPatrolReportDetailDto>> PatrolReportAsync(Guid reportId) => Api.GetAsync<AdminPatrolReportDetailDto>($"{BaseUrl}/patrol-reports/{reportId}");
}
