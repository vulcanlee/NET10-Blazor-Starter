using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Shell;

namespace MyProject.Web.Client.Api;

/// <summary>排程作業頁的 API 用戶端（0.10.6 起，WASM 遷移 M7；只限管理員）。</summary>
public sealed class ScheduledJobApi
{
    private const string BaseUrl = "api/v1/scheduled-jobs";
    private readonly ApiClient api;

    public ScheduledJobApi(ApiClient api)
    {
        this.api = api;
    }

    public Task<ApiResult<ScheduledJobOverviewDto>> OverviewAsync() => api.GetAsync<ScheduledJobOverviewDto>(BaseUrl);

    /// <summary>立即執行；已在佇列或執行中回 409。</summary>
    public Task<ApiResult<object>> TriggerAsync(string name) => api.PostAsync<object>($"{BaseUrl}/{Uri.EscapeDataString(name)}/trigger");

    public Task<ApiResult<object>> SetEnabledAsync(string name, bool enabled)
        => api.PutAsync<object>($"{BaseUrl}/{Uri.EscapeDataString(name)}/enabled", new ScheduledJobEnabledRequestDto { Enabled = enabled });

    public Task<ApiResult<List<ScheduledJobRunDto>>> RunsAsync(string name) => api.GetAsync<List<ScheduledJobRunDto>>($"{BaseUrl}/{Uri.EscapeDataString(name)}/runs");
}

/// <summary>系統健康監控頁的 API 用戶端（0.10.6 起；只限管理員）。⚠️ 讀報告會執行全部檢查，AI 已設定時會呼叫模型（產生費用）。</summary>
public sealed class SystemHealthApi
{
    private const string BaseUrl = "api/v1/system-health";
    private readonly ApiClient api;

    public SystemHealthApi(ApiClient api)
    {
        this.api = api;
    }

    public Task<ApiResult<SystemHealthReportDto>> ReportAsync() => api.GetAsync<SystemHealthReportDto>(BaseUrl);

    public Task<ApiResult<SystemHealthWarnLogsDto>> WarnLogsAsync() => api.GetAsync<SystemHealthWarnLogsDto>($"{BaseUrl}/warn-logs");

    public Task<ApiResult<SystemHealthTestResultDto>> TestEmailAsync(string? to)
        => api.PostAsync<SystemHealthTestResultDto>($"{BaseUrl}/test-email", new SystemHealthTestEmailRequestDto { To = to });

    public Task<ApiResult<SystemHealthTestResultDto>> TestNotificationAsync() => api.PostAsync<SystemHealthTestResultDto>($"{BaseUrl}/test-notification");
}

/// <summary>登入後首頁的 API 用戶端（0.10.6 起）：看得到什麼由伺服器決定，小工具各自讀自己的資料。</summary>
public sealed class DashboardApi
{
    private const string BaseUrl = "api/v1/dashboard";
    private readonly ApiClient api;

    public DashboardApi(ApiClient api)
    {
        this.api = api;
    }

    public Task<ApiResult<DashboardDto>> GetAsync() => api.GetAsync<DashboardDto>(BaseUrl);

    public Task<ApiResult<DashboardAccountDto>> AccountAsync() => api.GetAsync<DashboardAccountDto>($"{BaseUrl}/account");

    public Task<ApiResult<DashboardNotificationsDto>> NotificationsAsync() => api.GetAsync<DashboardNotificationsDto>($"{BaseUrl}/notifications");

    public Task<ApiResult<DashboardScheduledJobsDto>> ScheduledJobsAsync() => api.GetAsync<DashboardScheduledJobsDto>($"{BaseUrl}/scheduled-jobs");

    /// <summary>標為已讀（與通知鈴鐺同一個端點，只能標自己的）。</summary>
    public Task<ApiResult<object>> MarkNotificationReadAsync(int id) => api.PostAsync<object>($"api/v1/notifications/{id}/read");
}
