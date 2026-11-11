using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;
using MyProject.Models.Systems;

namespace MyProject.Web.Client.Api;

/// <summary>角色的 API 用戶端（0.10.4 起，WASM 版角色頁；只限管理員）。寫入回 <see cref="VerifyRecordResult"/>，訊息是伺服器給的那一句。</summary>
public sealed class RoleApi
{
    private const string BaseUrl = "api/v1/roles";
    private readonly ApiClient api;

    public RoleApi(ApiClient api)
    {
        this.api = api;
    }

    public Task<ApiResult<PagedResult<RoleDto>>> SearchAsync(RoleSearchRequestDto request) => api.PostAsync<PagedResult<RoleDto>>($"{BaseUrl}/search", request);

    /// <summary>單筆（伺服器寫一筆「查看」稽核）。</summary>
    public Task<ApiResult<RoleDto>> GetAsync(int id) => api.GetAsync<RoleDto>($"{BaseUrl}/{id}");

    public Task<ApiResult<RoleFormOptionsDto>> OptionsAsync() => api.GetAsync<RoleFormOptionsDto>($"{BaseUrl}/options");

    public async Task<VerifyRecordResult> CreateAsync(RoleDto dto) => (await api.PostAsync<RoleDto>(BaseUrl, dto)).ToVerify();

    public async Task<VerifyRecordResult> UpdateAsync(RoleDto dto) => (await api.PutAsync<object>($"{BaseUrl}/{dto.Id}", dto)).ToVerify();

    public async Task<VerifyRecordResult> DeleteAsync(int id) => (await api.DeleteAsync<object>($"{BaseUrl}/{id}")).ToVerify();

    public async Task<VerifyRecordResult> RestoreAsync(int id) => (await api.PostAsync<object>($"{BaseUrl}/{id}/restore")).ToVerify();

    public async Task<VerifyRecordResult> PurgeAsync(int id) => (await api.DeleteAsync<object>($"{BaseUrl}/{id}/purge")).ToVerify();
}
