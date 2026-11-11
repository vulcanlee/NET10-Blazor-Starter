using AntDesign;
using Microsoft.AspNetCore.Components;
using MyProject.Dtos.Shell;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Client.Commons;
using MyProject.Web.Components.Commons;
using MyProject.Web.Components.Layout;

namespace MyProject.Web.Client.Views.Profiles;

/// <summary>
/// 個人資料（0.9.102 起；0.10.1 起在 WASM 執行，WASM 遷移的試點頁）：只能改自己的姓名；Email、角色、團隊由管理員設定（唯讀顯示）；
/// 密碼與兩步驟驗證狀態；最近 20 筆自己的登入紀錄。資料來自 <c>/api/v1/profile</c>，存檔後通知右上角立即更新。
/// </summary>
public partial class ProfileView
{
    private const int HistorySize = 20;

    private bool isAccessChecked;
    private bool isSaving;
    private ProfileDto? profile;
    private List<LoginRecordDto> logins = [];
    private string nameInput = string.Empty;

    [Inject]
    public ApiClient Api { get; set; } = default!;

    [Inject]
    public ClientSession Session { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    public NotificationService NotificationService { get; set; } = default!;

    private string Initials => UserInitials.From(profile?.Name, profile?.Account);

    protected override async Task OnInitializedAsync()
    {
        // 版面已完成登入檢查；這裡等到資料讀回來才渲染（與搬遷前相同，不先閃出空白框架）。
        await LoadAsync();
        isAccessChecked = true;
    }

    private async Task LoadAsync()
    {
        var result = await Api.GetAsync<ProfileDto>("api/v1/profile");
        profile = result.Success ? result.Data : null;
        logins = profile?.RecentLogins ?? [];
        nameInput = profile?.Name ?? string.Empty;
    }

    private async Task SaveAsync()
    {
        if (profile is null || isSaving)
        {
            return;
        }

        isSaving = true;
        try
        {
            var result = await Api.PutAsync<object>("api/v1/profile/name", new UpdateProfileNameDto { Name = nameInput, ConcurrencyStamp = profile.ConcurrencyStamp });
            if (!result.Success)
            {
                ViewNotification.Error(NotificationService, string.IsNullOrWhiteSpace(result.ErrorMessage) ? result.Message : result.ErrorMessage);
                return;
            }

            await LoadAsync();
            Session.UpdateName(profile?.Name ?? nameInput);
            ViewNotification.Warning(NotificationService, "已更新姓名");
        }
        finally
        {
            isSaving = false;
        }
    }

    private static string ToText(DateTime local) => local.ToString("yyyy-MM-dd HH:mm");
}
