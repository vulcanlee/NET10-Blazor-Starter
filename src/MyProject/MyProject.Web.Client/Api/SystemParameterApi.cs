using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;

namespace MyProject.Web.Client.Api;

/// <summary>系統參數頁的 API 用戶端（0.10.6 起；只限管理員）。驗證、套用與稽核都在伺服器。</summary>
public sealed class SystemParameterApi
{
    private const string BaseUrl = "api/v1/system-parameters";
    private readonly ApiClient api;

    public SystemParameterApi(ApiClient api)
    {
        this.api = api;
    }

    public Task<ApiResult<SystemParameterOverviewDto>> GetOverviewAsync() => api.GetAsync<SystemParameterOverviewDto>(BaseUrl);

    /// <summary>存檔；成功時 <see cref="ApiResult{T}.Message"/> 是給使用者看的結果（例：「已更新「X」為 30 天。」）。</summary>
    public Task<ApiResult<object>> SaveAsync(string key, string? value, string? expectedStamp)
        => api.PostAsync<object>($"{BaseUrl}/save", new SystemParameterSaveRequestDto { Key = key, Value = value, ExpectedStamp = expectedStamp });

    /// <summary>還原為設定檔的值，或移除不認得的參數。</summary>
    public Task<ApiResult<object>> ResetAsync(string key, string expectedStamp)
        => api.PostAsync<object>($"{BaseUrl}/reset", new SystemParameterResetRequestDto { Key = key, ExpectedStamp = expectedStamp });
}
