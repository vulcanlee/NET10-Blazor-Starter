using MyProject.Dtos.Auths;
using MyProject.Web.Client.Api;

namespace MyProject.Web.Client.Auth;

/// <summary>
/// 登入相關頁面與啟動頁要顯示的公開設定（0.10.8 起，<c>GET api/v1/Auth/login-settings</c>，不需登入）：
/// 系統名稱與簡介、Google／寄信是否啟用、鎖定規則、密碼規則。同一次載入只讀一次；讀失敗下次再讀。
/// </summary>
public class LoginPageSettingsSource
{
    private readonly ApiClient api;
    private LoginPageSettingsDto? cached;

    public LoginPageSettingsSource(ApiClient api)
    {
        this.api = api;
    }

    public virtual async Task<LoginPageSettingsDto> GetAsync()
    {
        if (cached is not null)
        {
            return cached;
        }

        var result = await api.GetAnonymousAsync<LoginPageSettingsDto>("api/v1/Auth/login-settings");
        if (result.Success && result.Data is { } settings)
        {
            cached = settings;
            return settings;
        }

        return new LoginPageSettingsDto();
    }
}
