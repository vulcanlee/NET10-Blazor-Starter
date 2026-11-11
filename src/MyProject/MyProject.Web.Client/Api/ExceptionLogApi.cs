using MyProject.Dtos.Admins;
using MyProject.Dtos.Ai;
using MyProject.Dtos.Commons;
using MyProject.Models.Systems;

namespace MyProject.Web.Client.Api;

/// <summary>系統例外紀錄頁與 AI 例外分析的 API 用戶端（0.10.5 起，WASM 遷移 M6；只限管理員）。</summary>
public sealed class ExceptionLogApi
{
    private const string BaseUrl = "api/v1/exception-logs";
    private readonly ApiClient api;

    public ExceptionLogApi(ApiClient api)
    {
        this.api = api;
    }

    public Task<ApiResult<PagedResult<ExceptionLogItemDto>>> SearchAsync(ExceptionLogSearchRequestDto request) => api.PostAsync<PagedResult<ExceptionLogItemDto>>($"{BaseUrl}/search", request);

    public Task<ApiResult<ExceptionLogOptionsDto>> OptionsAsync() => api.GetAsync<ExceptionLogOptionsDto>($"{BaseUrl}/options");

    /// <summary>明細與完整堆疊（伺服器寫一筆查看稽核）。</summary>
    public Task<ApiResult<ExceptionLogDetailDto>> DetailAsync(int id) => api.GetAsync<ExceptionLogDetailDto>($"{BaseUrl}/{id}");

    /// <summary>點列複製用的堆疊（不寫稽核）。</summary>
    public Task<ApiResult<ExceptionLogStackTraceDto>> StackTraceAsync(int id) => api.GetAsync<ExceptionLogStackTraceDto>($"{BaseUrl}/{id}/stack-trace");

    /// <summary>目前查詢條件下全部資料的複製文字（含完整堆疊）。資料可能很多，不受一般請求的逾時限制。</summary>
    public Task<ApiResult<ExceptionLogCopyTextDto>> CopyTextAsync(ExceptionLogSearchRequestDto request) => api.PostLongRunningAsync<ExceptionLogCopyTextDto>($"{BaseUrl}/copy-text", request);

    public Task<ApiDownload> ExportAsync(ExceptionLogSearchRequestDto request) => api.DownloadAsync($"{BaseUrl}/export", request);

    public async Task<VerifyRecordResult> DeleteAsync(int id) => (await api.DeleteAsync<object>($"{BaseUrl}/{id}")).ToVerify();

    /// <summary>成功時 <see cref="ApiResult{T}.Message"/> 是畫面要顯示的結果（例如「已清除 3 筆紀錄。」）。</summary>
    public Task<ApiResult<object>> PurgeAsync() => api.PostAsync<object>($"{BaseUrl}/purge");

    public Task<ApiResult<object>> ClearAllAsync() => api.PostAsync<object>($"{BaseUrl}/clear-all");

    /// <summary>AI 例外分析（NDJSON 串流）。取消 <paramref name="cancellationToken"/> 會中斷請求，伺服器同步取消 AI 呼叫。</summary>
    public Task<ApiResult<object>> AnalyzeAsync(int id, ExceptionAiRequestDto request, Func<AiStreamFrameDto, Task> onFrame, CancellationToken cancellationToken)
        => api.StreamAsync($"{BaseUrl}/{id}/ai", request, onFrame, cancellationToken);

    public Task<ApiDownload> ExportAiPdfAsync(int id, ExceptionAiPdfRequestDto request) => api.DownloadAsync($"{BaseUrl}/{id}/ai/pdf", request);
}
