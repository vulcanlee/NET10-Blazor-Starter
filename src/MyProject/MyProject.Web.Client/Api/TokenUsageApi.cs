using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;
using MyProject.Models.Systems;

namespace MyProject.Web.Client.Api;

/// <summary>Token 用量頁的 API 用戶端（0.10.5 起，WASM 遷移 M6；只限管理員）。</summary>
public sealed class TokenUsageApi
{
    private const string BaseUrl = "api/v1/token-usage";
    private readonly ApiClient api;

    public TokenUsageApi(ApiClient api)
    {
        this.api = api;
    }

    /// <summary>同一份條件一次取回明細一頁、合計、分組、趨勢、摘要卡與用量上限（帶 RecordSearch 時伺服器寫一筆搜尋稽核）。</summary>
    public Task<ApiResult<TokenUsageDashboardDto>> DashboardAsync(TokenUsageSearchRequestDto request) => api.PostAsync<TokenUsageDashboardDto>($"{BaseUrl}/dashboard", request);

    public Task<ApiResult<TokenUsageOptionsDto>> OptionsAsync() => api.GetAsync<TokenUsageOptionsDto>($"{BaseUrl}/options");

    /// <summary>明細窗的原始明細、費率快照與對話紀錄是否存在（伺服器寫一筆查看稽核）。</summary>
    public Task<ApiResult<TokenUsageDetailDto>> DetailAsync(int id) => api.GetAsync<TokenUsageDetailDto>($"{BaseUrl}/{id}");

    public Task<ApiDownload> ExportCsvAsync(TokenUsageSearchRequestDto request) => api.DownloadAsync($"{BaseUrl}/export", request);

    /// <summary>PDF 報表：紀錄多時產生需要一點時間，仍走一般逾時（與 CSV 相同）。</summary>
    public Task<ApiDownload> ExportPdfAsync(TokenUsagePdfExportRequestDto request) => api.DownloadAsync($"{BaseUrl}/export-pdf", request);

    public async Task<VerifyRecordResult> DeleteAsync(int id) => (await api.DeleteAsync<object>($"{BaseUrl}/{id}")).ToVerify();

    /// <summary>批次清除；成功時 <see cref="ApiResult{T}.Message"/> 是「已清除 N 筆紀錄。」這類訊息。</summary>
    public Task<ApiResult<object>> PurgeAsync(DateTime beforeDate) => api.PostAsync<object>($"{BaseUrl}/purge", new TokenUsagePurgeRequestDto { BeforeDate = beforeDate });

    public Task<ApiResult<object>> ClearAllAsync() => api.PostAsync<object>($"{BaseUrl}/clear-all");
}
