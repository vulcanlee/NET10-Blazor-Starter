using MyProject.Dtos.Ai;
using MyProject.Dtos.Commons;
using MyProject.Models.Systems;

namespace MyProject.Web.Client.Api;

/// <summary>AI 對話紀錄頁的 API 用戶端（0.10.5 起，WASM 遷移 M6；只限管理員）。</summary>
public sealed class AiCallLogApi
{
    private const string BaseUrl = "api/v1/ai-call-logs";
    private readonly ApiClient api;

    public AiCallLogApi(ApiClient api)
    {
        this.api = api;
    }

    public Task<ApiResult<AiCallLogPageDto>> SearchAsync(AiCallLogSearchRequestDto request) => api.PostAsync<AiCallLogPageDto>($"{BaseUrl}/search", request);

    /// <summary>帶內文關鍵字的查詢：伺服器逐檔掃描，可能很久，不受一般請求的逾時限制；離開頁面時以 <paramref name="cancellationToken"/> 停止。</summary>
    public Task<ApiResult<AiCallLogPageDto>> ScanAsync(AiCallLogSearchRequestDto request, CancellationToken cancellationToken)
        => api.PostLongRunningAsync<AiCallLogPageDto>($"{BaseUrl}/search", request, cancellationToken);

    public Task<ApiResult<AiCallLogOptionsDto>> OptionsAsync() => api.GetAsync<AiCallLogOptionsDto>($"{BaseUrl}/options");

    /// <summary>深連結：以呼叫識別碼找紀錄 Id（找不到回 404）。</summary>
    public Task<ApiResult<int>> FindByCallIdAsync(Guid callId) => api.GetAsync<int>($"{BaseUrl}/by-call/{callId:D}");

    /// <summary>明細（伺服器寫一筆查看稽核）。</summary>
    public Task<ApiResult<AiCallLogDetailDto>> DetailAsync(int id) => api.GetAsync<AiCallLogDetailDto>($"{BaseUrl}/{id}");

    /// <summary>某一段的完整內容（複製被截斷的段落）。</summary>
    public Task<ApiResult<AiCallLogTextDto>> TextAsync(int id, string part) => api.GetAsync<AiCallLogTextDto>($"{BaseUrl}/{id}/text?part={Uri.EscapeDataString(part)}");

    public Task<ApiDownload> RawAsync(int id) => api.DownloadAsync($"{BaseUrl}/{id}/raw");

    public Task<ApiDownload> PdfAsync(int id) => api.DownloadAsync($"{BaseUrl}/{id}/pdf");

    public Task<ApiDownload> MergedPdfAsync(AiCallLogMergedPdfRequestDto request) => api.DownloadAsync($"{BaseUrl}/merged-pdf", request);

    public Task<ApiDownload> ExportAsync(AiCallLogSearchRequestDto request) => api.DownloadAsync($"{BaseUrl}/export", request);

    public async Task<VerifyRecordResult> DeleteAsync(int id) => (await api.DeleteAsync<object>($"{BaseUrl}/{id}")).ToVerify();

    public Task<ApiResult<object>> PurgeAsync(DateTime beforeDate) => api.PostAsync<object>($"{BaseUrl}/purge", new AiCallLogPurgeRequestDto { BeforeDate = beforeDate });

    public Task<ApiResult<object>> ClearAllAsync() => api.PostAsync<object>($"{BaseUrl}/clear-all");
}
