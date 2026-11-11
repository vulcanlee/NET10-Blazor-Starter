using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MyProject.Dtos.Admins;
using MyProject.Share.Helpers;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;

namespace MyProject.Web.Client.Views.Analytics
{
    /// <summary>
    /// 資料庫用量（0.10.6 起在 WASM 執行，WASM 遷移 M7）。畫面照 Server 版，資料改走 <see cref="DatabaseUsageApi"/>（只限管理員，不送資料庫的完整路徑）。
    /// </summary>
    public partial class DatabaseUsageView
    {
        [Inject]
        public DatabaseUsageApi Api { get; set; } = default!;

        [Inject]
        public PagePermissions Permissions { get; set; } = default!;

        [Inject]
        public ILogger<DatabaseUsageView> Logger { get; set; } = default!;

        private DatabaseUsageDto? report;
        private string RoleMessage = string.Empty;
        private bool isAccessChecked;

        protected override async Task OnInitializedAsync()
        {
            isAccessChecked = true;

            // 此頁為管理員專屬：權限鍵刻意未上架角色矩陣，因此以 IsAdmin 直接判斷。權限未通過前不讀取任何資料庫資訊；
            // 沒有權限的開頁稽核由伺服器在開頁紀錄時寫（shell/page-view）。
            if (!Permissions.IsAdmin)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                Logger.LogWarning("Database usage page denied because the current user is not an administrator.");
                return;
            }

            var result = await Api.GetAsync();
            report = result.Success && result.Data is { } data
                ? data
                : new DatabaseUsageDto { ErrorMessage = $"讀取資料庫用量失敗：{result.UserMessage()}" };
        }
    }
}
