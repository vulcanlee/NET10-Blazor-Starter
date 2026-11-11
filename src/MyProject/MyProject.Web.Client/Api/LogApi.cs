using MyProject.Dtos.Admins;
using MyProject.Dtos.Ai;
using MyProject.Dtos.Commons;

namespace MyProject.Web.Client.Api;

/// <summary>日誌檢視頁的 API 用戶端（0.10.5 起，WASM 遷移 M6；只限管理員）。</summary>
public sealed class LogViewerApi
{
    private const string BaseUrl = "api/v1/logs";
    private readonly ApiClient api;

    public LogViewerApi(ApiClient api)
    {
        this.api = api;
    }

    /// <summary>查詢（<see cref="LogSearchRequestDto.RecordSearch"/> 為 true 時伺服器寫一筆搜尋稽核）。</summary>
    public Task<ApiResult<LogSearchResultDto>> SearchAsync(LogSearchRequestDto request) => api.PostAsync<LogSearchResultDto>($"{BaseUrl}/search", request);

    public Task<ApiResult<LogViewerOptionsDto>> OptionsAsync() => api.GetAsync<LogViewerOptionsDto>($"{BaseUrl}/options");

    /// <summary>匯出 .log（伺服器依同一份條件重查並寫匯出稽核）。</summary>
    public Task<ApiDownload> ExportAsync(LogSearchRequestDto request) => api.DownloadAsync($"{BaseUrl}/export", request);

    /// <summary>AI 分析（NDJSON 串流）：每收到一行呼叫一次 <paramref name="onFrame"/>；取消時中斷請求，伺服器跟著取消 AI 呼叫。</summary>
    public Task<ApiResult<object>> AnalyzeAsync(LogSearchRequestDto request, Func<AiStreamFrameDto, Task> onFrame, CancellationToken cancellationToken)
        => api.StreamAsync($"{BaseUrl}/ai", request, onFrame, cancellationToken);

    /// <summary>AI 報告匯出 PDF（伺服器重建附錄、用伺服器時間，寫一筆稽核）。</summary>
    public Task<ApiDownload> ExportAiReportPdfAsync(LogAiReportPdfRequestDto request) => api.DownloadAsync($"{BaseUrl}/ai/pdf", request);
}

/// <summary>日誌等級設定頁的 API 用戶端（0.10.5 起；只限管理員）。</summary>
public sealed class LogLevelApi
{
    private const string BaseUrl = "api/v1/log-level";
    private readonly ApiClient api;

    public LogLevelApi(ApiClient api)
    {
        this.api = api;
    }

    public Task<ApiResult<LogLevelStateDto>> GetAsync() => api.GetAsync<LogLevelStateDto>(BaseUrl);

    public Task<ApiResult<LogLevelStateDto>> ApplyAsync(string level) => api.PostAsync<LogLevelStateDto>($"{BaseUrl}/apply", new LogLevelApplyRequestDto { Level = level });

    public Task<ApiResult<LogLevelStateDto>> RestoreAsync() => api.PostAsync<LogLevelStateDto>($"{BaseUrl}/restore");
}
