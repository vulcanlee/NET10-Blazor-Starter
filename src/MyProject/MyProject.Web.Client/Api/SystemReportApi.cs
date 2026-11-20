using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;

namespace MyProject.Web.Client.Api;

/// <summary>「系統巡檢報告」頁的 API 用戶端（0.10.39 起；<c>api/v1/system-reports</c>，只限管理員）。舊版巡檢報告也從這裡讀。</summary>
public sealed class SystemReportApi
{
    private const string BaseUrl = "api/v1/system-reports";

    private readonly ApiClient api;

    public SystemReportApi(ApiClient api)
    {
        this.api = api;
    }

    public Task<ApiResult<SystemReportListDto>> SearchAsync(SystemReportSearchRequestDto request) => api.PostAsync<SystemReportListDto>($"{BaseUrl}/search", request);

    public Task<ApiResult<SystemReportDetailDto>> GetAsync(Guid reportId) => api.GetAsync<SystemReportDetailDto>($"{BaseUrl}/{reportId}");

    /// <summary>放進背景產生（完成時伺服器以站內通知告訴產生者）；已經有一份在產生時回 409。</summary>
    public Task<ApiResult<object>> GenerateAsync(SystemReportGenerateRequestDto request) => api.PostAsync<object>($"{BaseUrl}/generate", request);

    public Task<ApiResult<AdminPatrolReportListDto>> LegacyListAsync() => api.GetAsync<AdminPatrolReportListDto>($"{BaseUrl}/legacy");

    public Task<ApiResult<AdminPatrolReportDetailDto>> LegacyGetAsync(Guid reportId) => api.GetAsync<AdminPatrolReportDetailDto>($"{BaseUrl}/legacy/{reportId}");
}
