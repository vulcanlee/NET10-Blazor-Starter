using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;
using MyProject.Models.AdapterModel;

namespace MyProject.Web.Client.Api;

/// <summary>稽核紀錄頁的 API 用戶端（0.10.4 起，WASM 遷移 M5；只限管理員）。</summary>
public sealed class AuditLogApi
{
    private const string BaseUrl = "api/v1/audit-logs";
    private readonly ApiClient api;

    public AuditLogApi(ApiClient api)
    {
        this.api = api;
    }

    public Task<ApiResult<PagedResult<AuditLogItemDto>>> SearchAsync(AuditLogSearchRequestDto request) => api.PostAsync<PagedResult<AuditLogItemDto>>($"{BaseUrl}/search", request);

    public Task<ApiResult<AuditLogOptionsDto>> OptionsAsync() => api.GetAsync<AuditLogOptionsDto>($"{BaseUrl}/options");

    /// <summary>明細與詳細內容（伺服器寫一筆查看稽核）。</summary>
    public Task<ApiResult<AuditLogDetailDto>> DetailAsync(int id) => api.GetAsync<AuditLogDetailDto>($"{BaseUrl}/{id}");

    public Task<ApiDownload> ExportAsync(AuditLogSearchRequestDto request) => api.DownloadAsync($"{BaseUrl}/export", request);

    public Task<ApiResult<AuditLogPurgeResultDto>> PurgeAsync() => api.PostAsync<AuditLogPurgeResultDto>($"{BaseUrl}/purge");

    public Task<ApiResult<object>> ClearAllAsync() => api.PostAsync<object>($"{BaseUrl}/clear-all");

    /// <summary>完整性驗證：紀錄多時要一點時間，不受一般請求的逾時限制。</summary>
    public Task<ApiResult<AuditIntegrityReportDto>> VerifyAsync() => api.PostLongRunningAsync<AuditIntegrityReportDto>($"{BaseUrl}/verify");

    /// <summary>清單的一列轉成畫面資料（與異動歷程對話窗相同：只帶「有沒有詳細內容」，不帶伺服器路徑）。</summary>
    public static AuditLogAdapterModel ToModel(AuditLogItemDto dto) => ApiAuditHistorySource.ToModel(dto);
}

/// <summary>寄信紀錄頁的 API 用戶端（0.10.4 起；只限管理員、唯讀）。</summary>
public sealed class EmailLogApi
{
    private readonly ApiClient api;

    public EmailLogApi(ApiClient api)
    {
        this.api = api;
    }

    public Task<ApiResult<PagedResult<EmailLogItemDto>>> SearchAsync(EmailLogSearchRequestDto request) => api.PostAsync<PagedResult<EmailLogItemDto>>("api/v1/email-logs/search", request);
}
