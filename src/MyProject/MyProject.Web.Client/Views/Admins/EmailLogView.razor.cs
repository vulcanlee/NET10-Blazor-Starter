using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MyProject.Dtos.Admins;
using MyProject.Share.Helpers;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Client.Commons;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Client.Views.Admins
{
    /// <summary>
    /// 寄信紀錄（0.9.167 起；0.10.4 起在 WASM 執行，WASM 遷移 M5）：資料表 <c>EmailOutbox</c> 同時是寄信佇列，這一頁唯讀列出每封信的收件者、主旨與結果。
    /// 內文加密且寄出或放棄後就清除，所以沒有「查看內容」也沒有「重新寄送」。舊紀錄由排程作業與「資料保留與清理」頁清除。
    /// 資料走 <see cref="EmailLogApi"/>（只限管理員）；與 Server 版相同不寫稽核。
    /// </summary>
    public partial class EmailLogView
    {
        internal sealed record Option(string Value, string Label, string Color = "default");

        /// <summary>狀態與種類的代碼與伺服器的 <c>EmailOutboxStatus</c>、<c>EmailKinds</c> 相同（Client 不參考 AccessDatas，所以寫在這裡）。</summary>
        internal static readonly IReadOnlyList<Option> Statuses =
        [
            new("Pending", "待寄", "blue"),
            new("Sent", "已寄出", "green"),
            new("Failed", "失敗", "red"),
            new("Skipped", "未寄出（寄信停用）"),
        ];

        internal static readonly IReadOnlyList<Option> Kinds =
        [
            new("Notification", "站內通知"),
            new("PasswordReset", "重設密碼"),
            new("PasswordChanged", "密碼已變更"),
            new("ExceptionAlert", "例外告警"),
            new("Test", "測試信"),
        ];

        [Inject]
        public EmailLogApi Api { get; set; } = default!;

        [Inject]
        public PagePermissions Permissions { get; set; } = default!;

        [Inject]
        public NotificationService NotificationService { get; set; } = default!;

        [Inject]
        public ILogger<EmailLogView> Logger { get; set; } = default!;

        private bool isAccessChecked;
        private bool isLoading;
        private List<EmailLogItemDto> items = [];
        private int _pageIndex = 1;
        private int _pageSize = MagicObjectHelper.PageSize;
        private int _total;
        private DateTime? startTime;
        private DateTime? endTime;
        private string selectedStatus = string.Empty;
        private string selectedKind = string.Empty;
        private string keyword = string.Empty;

        public string RoleMessage { get; set; } = string.Empty;

        protected override async Task OnInitializedAsync()
        {
            isAccessChecked = true;

            // 管理員專屬（收件者是個資）：權限未通過前不讀任何資料；沒有權限的開頁稽核由伺服器在開頁紀錄時寫（shell/page-view）。
            if (!Permissions.IsAdmin)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                Logger.LogWarning("Email log view denied because the current user is not an administrator.");
                return;
            }

            await ReloadAsync();
        }

        /// <summary>重新讀取；回傳是否成功（失敗時已顯示通知）。</summary>
        private async Task<bool> ReloadAsync()
        {
            isLoading = true;
            StateHasChanged();
            try
            {
                var result = await Api.SearchAsync(new EmailLogSearchRequestDto
                {
                    // 起訖時間當成伺服器的本地時間送出（不帶時差），理由同稽核紀錄頁。
                    FromLocal = ClientCulture.Unzoned(startTime),
                    ToLocal = ClientCulture.Unzoned(endTime),
                    Status = selectedStatus,
                    Kind = selectedKind,
                    Keyword = keyword,
                    PageIndex = _pageIndex,
                    PageSize = _pageSize,
                });
                if (result.Success && result.Data is { } page)
                {
                    items = page.Items;
                    _total = page.TotalCount;
                    return true;
                }

                ViewNotification.Error(NotificationService, $"載入寄信紀錄失敗：{result.UserMessage()}");
                items = [];
                _total = 0;
                return false;
            }
            finally
            {
                isLoading = false;
            }
        }

        private async Task OnTableChange(QueryModel<EmailLogItemDto> args)
        {
            _pageIndex = args.PageIndex;
            _pageSize = args.PageSize;
            await ReloadAsync();
        }

        private async Task OnSearchAsync()
        {
            _pageIndex = 1;
            Logger.LogInformation("Email logs searched. HasSearch={HasSearch}, SearchLength={SearchLength}",
                string.IsNullOrWhiteSpace(keyword) == false, keyword.Length);
            await ReloadAsync();
        }

        private async Task OnRefreshAsync()
        {
            if (await ReloadAsync())
            {
                ViewNotification.Warning(NotificationService, "已更新最新資料");
            }
        }

        private static string KindLabel(string kind) => Kinds.FirstOrDefault(x => x.Value == kind)?.Label ?? kind;

        private static string StatusLabel(string status) => Statuses.FirstOrDefault(x => x.Value == status)?.Label ?? status;

        private static string StatusColor(string status) => Statuses.FirstOrDefault(x => x.Value == status)?.Color ?? "default";
    }
}
