using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;
using MyProject.Models.Systems;

namespace MyProject.Web.Client.Api;

/// <summary>AI 提示詞頁的 API 用戶端（0.10.5 起；只限管理員）。</summary>
public sealed class PromptTemplateApi
{
    private const string BaseUrl = "api/v1/prompt-templates";
    private readonly ApiClient api;

    public PromptTemplateApi(ApiClient api)
    {
        this.api = api;
    }

    public Task<ApiResult<List<PromptTemplateDto>>> GetAllAsync() => api.GetAsync<List<PromptTemplateDto>>(BaseUrl);

    public Task<ApiResult<PromptTemplateSaveResultDto>> SaveAsync(string key, PromptTemplateSaveRequestDto request)
        => api.PostAsync<PromptTemplateSaveResultDto>($"{BaseUrl}/{Uri.EscapeDataString(key)}/versions", request);

    public async Task<VerifyRecordResult> ActivateAsync(string key, int? version)
        => (await api.PostAsync<object>($"{BaseUrl}/{Uri.EscapeDataString(key)}/activate", new PromptTemplateActivateRequestDto { Version = version })).ToVerify();
}
