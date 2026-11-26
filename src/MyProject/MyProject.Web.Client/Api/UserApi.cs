using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;

namespace MyProject.Web.Client.Api;

/// <summary>
/// 使用者的 API 用戶端（0.10.4 起，WASM 版使用者頁；只限管理員）。寫入回 <see cref="VerifyRecordResult"/>，訊息是伺服器給的那一句。
/// 固定端點（清單、單筆〔含額外角色與團隊〕、檢查、新增、啟用、刪除、還原、永久刪除、匯出）見 <see cref="CrudApi{TDto, TSearch}"/>（0.10.45 起繼承），
/// 這裡只放使用者特有的；修改另有自己的回傳（改到自己時帶新 Token）。
/// 新增前的檢查 <c>CheckAsync</c>（帳號是否已存在等）：「複製」建立時先檢查帳號再問 Email。
/// </summary>
public sealed class UserApi : CrudApi<UserDto, UserSearchRequestDto>
{
    public UserApi(ApiClient api)
        : base(api, "users")
    {
    }

    public Task<ApiResult<UserFormOptionsDto>> OptionsAsync() => Api.GetAsync<UserFormOptionsDto>($"{BaseUrl}/options");

    public Task<ApiResult<string>> GeneratePasswordAsync() => Api.GetAsync<string>($"{BaseUrl}/password/generate");

    public Task<ApiResult<UserCopyDraftDto>> CopyDraftAsync(int id) => Api.GetAsync<UserCopyDraftDto>($"{BaseUrl}/{id}/copy-draft");

    public Task<ApiResult<List<string>>> AccountsUsingEmailAsync(string email)
        => Api.PostAsync<List<string>>($"{BaseUrl}/accounts-using-email", new EmailLookupRequestDto { Email = email });

    /// <summary>
    /// 帳號欄位的非同步檢查（0.10.15 起，<c>AsyncFieldCheck</c>）：回「已存在」（409）或帳號格式不符（0.10.17 起）的訊息，
    /// 其他失敗（例如選的團隊剛被停用）留給存檔時的完整檢查。
    /// </summary>
    public async Task<string?> AccountConflictAsync(UserDto dto)
    {
        var result = await Api.PostAsync<object>($"{BaseUrl}/check", dto);
        if (result.Success)
        {
            return null;
        }

        var message = result.UserMessage();
        return result.StatusCode == 409 || message == AccountRules.FormatMessage ? message : null;
    }

    /// <summary>修改；成功時 <c>Data.Tokens</c> 有值代表改到自己：存下新 Token 後整頁重新載入。</summary>
    public Task<ApiResult<UserSaveResultDto>> UpdateAsync(UserDto dto) => Api.PutAsync<UserSaveResultDto>($"{BaseUrl}/{dto.Id}", dto);

    public async Task<VerifyRecordResult> UnlockAsync(int id) => (await Api.PostAsync<object>($"{BaseUrl}/{id}/unlock")).ToVerify();

    public async Task<VerifyRecordResult> ForceLogoutAsync(int id) => (await Api.PostAsync<object>($"{BaseUrl}/{id}/force-logout")).ToVerify();

    public async Task<VerifyRecordResult> ResetTwoFactorAsync(int id) => (await Api.PostAsync<object>($"{BaseUrl}/{id}/two-factor/reset")).ToVerify();
}
