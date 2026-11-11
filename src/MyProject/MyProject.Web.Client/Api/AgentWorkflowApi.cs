using MyProject.Dtos.Ai;
using MyProject.Dtos.Commons;

namespace MyProject.Web.Client.Api;

/// <summary>Agent 工作流程頁的 API 用戶端（0.10.7 起，WASM 遷移 M8；需要「Agent 工作流程」頁面權限）。</summary>
public sealed class AgentWorkflowApi
{
    private const string BaseUrl = "api/v1/agent-workflows";
    private readonly ApiClient api;

    public AgentWorkflowApi(ApiClient api)
    {
        this.api = api;
    }

    public Task<ApiResult<AgentWorkflowPageInfoDto>> InfoAsync() => api.GetAsync<AgentWorkflowPageInfoDto>($"{BaseUrl}/info");

    public Task<ApiResult<List<AgentWorkflowRecordDto>>> RecordsAsync(string workflow)
        => api.GetAsync<List<AgentWorkflowRecordDto>>($"{BaseUrl}/records?workflow={Uri.EscapeDataString(workflow)}");

    public Task<ApiResult<AgentWorkflowRecordDetailDto>> RecordAsync(Guid id) => api.GetAsync<AgentWorkflowRecordDetailDto>($"{BaseUrl}/records/{id}");

    public Task<ApiResult<object>> RenameAsync(Guid id, string title)
        => api.PutAsync<object>($"{BaseUrl}/records/{id}/title", new AgentWorkflowRenameRequestDto { Title = title });

    /// <summary>刪除紀錄（伺服器寫稽核；這次執行還沒結束的話伺服器先放棄它）。</summary>
    public Task<ApiResult<object>> DeleteAsync(Guid id) => api.DeleteAsync<object>($"{BaseUrl}/records/{id}");

    /// <summary>自己停下來等回覆的那一次執行；沒有時資料是 null。</summary>
    public Task<ApiResult<AgentWorkflowRunDto?>> ActiveAsync() => api.GetAsync<AgentWorkflowRunDto?>($"{BaseUrl}/runs/active");

    /// <summary>開始一次執行（NDJSON 串流）。取消 <paramref name="cancellationToken"/> 會中斷請求，伺服器把這次執行存成「中斷」。</summary>
    public Task<ApiResult<object>> StartAsync(AgentWorkflowStartRequestDto request, Func<AgentWorkflowFrameDto, Task> onFrame, CancellationToken cancellationToken)
        => api.StreamAsync($"{BaseUrl}/runs", request, onFrame, cancellationToken);

    /// <summary>回覆一筆等待中的請求並繼續（NDJSON 串流）。</summary>
    public Task<ApiResult<object>> RespondAsync(Guid runId, AgentWorkflowRespondRequestDto request, Func<AgentWorkflowFrameDto, Task> onFrame, CancellationToken cancellationToken)
        => api.StreamAsync($"{BaseUrl}/runs/{runId}/responses", request, onFrame, cancellationToken);

    /// <summary>「停止」：串流最後會收到 Canceled 的結果。</summary>
    public Task<ApiResult<object>> StopAsync(Guid runId) => api.PostAsync<object>($"{BaseUrl}/runs/{runId}/stop");

    /// <summary>放棄這次執行（存成「中斷」）。</summary>
    /// <summary>放棄；<paramref name="onlyIfIdle"/>＝離開頁面時，有別的請求正在用這次執行就不放棄。</summary>
    public Task<ApiResult<object>> AbandonAsync(Guid runId, bool onlyIfIdle = false)
        => api.PostAsync<object>($"{BaseUrl}/runs/{runId}/abandon{(onlyIfIdle ? "?onlyIfIdle=true" : string.Empty)}");
}
