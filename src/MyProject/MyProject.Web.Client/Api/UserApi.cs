using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;
using MyProject.Models.Systems;

namespace MyProject.Web.Client.Api;

/// <summary>使用者的 API 用戶端（0.10.4 起，WASM 版使用者頁；只限管理員）。寫入回 <see cref="VerifyRecordResult"/>，訊息是伺服器給的那一句。</summary>
public sealed class UserApi
{
    private const string BaseUrl = "api/v1/users";
    private readonly ApiClient api;

    public UserApi(ApiClient api)
    {
        this.api = api;
    }

    public Task<ApiResult<PagedResult<UserDto>>> SearchAsync(UserSearchRequestDto request) => api.PostAsync<PagedResult<UserDto>>($"{BaseUrl}/search", request);

    /// <summary>單筆（含額外角色與團隊；伺服器寫一筆「查看」稽核）。</summary>
    public Task<ApiResult<UserDto>> GetAsync(int id) => api.GetAsync<UserDto>($"{BaseUrl}/{id}");

    public Task<ApiResult<UserFormOptionsDto>> OptionsAsync() => api.GetAsync<UserFormOptionsDto>($"{BaseUrl}/options");

    public Task<ApiResult<string>> GeneratePasswordAsync() => api.GetAsync<string>($"{BaseUrl}/password/generate");

    public Task<ApiResult<UserCopyDraftDto>> CopyDraftAsync(int id) => api.GetAsync<UserCopyDraftDto>($"{BaseUrl}/{id}/copy-draft");

    public Task<ApiResult<List<string>>> AccountsUsingEmailAsync(string email)
        => api.PostAsync<List<string>>($"{BaseUrl}/accounts-using-email", new EmailLookupRequestDto { Email = email });

    /// <summary>新增前的檢查（帳號是否已存在等），「複製」建立時先檢查帳號再問 Email。</summary>
    public async Task<VerifyRecordResult> CheckAsync(UserDto dto) => (await api.PostAsync<object>($"{BaseUrl}/check", dto)).ToVerify();

    /// <summary>帳號是否已被使用（0.10.15 起，表單欄位的非同步檢查 <c>AsyncFieldCheck</c>）：只回「已存在」（409）的訊息，其他失敗留給存檔時的完整檢查。</summary>
    public async Task<string?> AccountConflictAsync(UserDto dto)
    {
        var result = await api.PostAsync<object>($"{BaseUrl}/check", dto);
        return !result.Success && result.StatusCode == 409 ? result.UserMessage() : null;
    }

    public async Task<VerifyRecordResult> CreateAsync(UserDto dto) => (await api.PostAsync<UserDto>(BaseUrl, dto)).ToVerify();

    /// <summary>修改；成功時 <c>Data.Tokens</c> 有值代表改到自己：存下新 Token 後整頁重新載入。</summary>
    public Task<ApiResult<UserSaveResultDto>> UpdateAsync(UserDto dto) => api.PutAsync<UserSaveResultDto>($"{BaseUrl}/{dto.Id}", dto);

    public async Task<VerifyRecordResult> SetEnabledAsync(int id, bool enabled)
        => (await api.PutAsync<object>($"{BaseUrl}/{id}/enabled", new SetEnabledRequestDto { Enabled = enabled })).ToVerify();

    public async Task<VerifyRecordResult> UnlockAsync(int id) => (await api.PostAsync<object>($"{BaseUrl}/{id}/unlock")).ToVerify();

    public async Task<VerifyRecordResult> ForceLogoutAsync(int id) => (await api.PostAsync<object>($"{BaseUrl}/{id}/force-logout")).ToVerify();

    public async Task<VerifyRecordResult> ResetTwoFactorAsync(int id) => (await api.PostAsync<object>($"{BaseUrl}/{id}/two-factor/reset")).ToVerify();

    public async Task<VerifyRecordResult> DeleteAsync(int id) => (await api.DeleteAsync<object>($"{BaseUrl}/{id}")).ToVerify();

    public async Task<VerifyRecordResult> RestoreAsync(int id) => (await api.PostAsync<object>($"{BaseUrl}/{id}/restore")).ToVerify();

    public async Task<VerifyRecordResult> PurgeAsync(int id) => (await api.DeleteAsync<object>($"{BaseUrl}/{id}/purge")).ToVerify();

    /// <summary>匯出 Excel（條件與清單相同）；超過上限時 <c>StatusCode</c> 為 400、訊息說明原因。</summary>
    public Task<ApiDownload> ExportAsync(UserSearchRequestDto request) => api.DownloadAsync($"{BaseUrl}/export", request);
}
