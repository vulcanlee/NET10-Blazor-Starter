using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Components.Views.Admins
{
    /// <summary>
    /// 寄信紀錄（0.9.167 起）：資料表 <c>EmailOutbox</c> 同時是寄信佇列，這一頁唯讀列出每封信的收件者、主旨與結果。
    /// 內文加密且寄出或放棄後就清除，所以沒有「查看內容」也沒有「重新寄送」。舊紀錄由排程作業與「資料保留與清理」頁清除。
    /// </summary>
    public partial class EmailLogView
    {
        internal sealed record Option(string Value, string Label, string Color = "default");

        internal static readonly IReadOnlyList<Option> Statuses =
        [
            new(EmailOutboxStatus.Pending, "待寄", "blue"),
            new(EmailOutboxStatus.Sent, "已寄出", "green"),
            new(EmailOutboxStatus.Failed, "失敗", "red"),
            new(EmailOutboxStatus.Skipped, "未寄出（寄信停用）"),
        ];

        internal static readonly IReadOnlyList<Option> Kinds =
        [
            new(EmailKinds.Notification, "站內通知"),
            new(EmailKinds.PasswordReset, "重設密碼"),
            new(EmailKinds.PasswordChanged, "密碼已變更"),
            new(EmailKinds.ExceptionAlert, "例外告警"),
            new(EmailKinds.Test, "測試信"),
        ];

        private readonly ILogger<EmailLogView> logger;
        private readonly EmailOutboxQueryService queryService;
        private readonly NotificationService notificationService;

        private bool isAccessChecked;
        private bool isLoading;
        private List<EmailLogItem> items = [];
        private int _pageIndex = 1;
        private int _pageSize = MagicObjectHelper.PageSize;
        private int _total;
        private DateTime? startTime;
        private DateTime? endTime;
        private string selectedStatus = string.Empty;
        private string selectedKind = string.Empty;
        private string keyword = string.Empty;

        public string RoleMessage { get; set; } = string.Empty;

        [Inject]
        public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

        [Inject]
        public AuthenticationStateProvider authStateProvider { get; set; } = default!;

        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        public EmailLogView(ILogger<EmailLogView> logger, EmailOutboxQueryService queryService, NotificationService notificationService)
        {
            this.logger = logger;
            this.queryService = queryService;
            this.notificationService = notificationService;
        }

        protected override async Task OnInitializedAsync()
        {
            var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
            if (checkResult != AuthenticationCheckResult.Succeeded)
            {
                return;
            }

            isAccessChecked = true;

            // 管理員專屬：權限鍵刻意未上架角色矩陣，以 CheckIsAdmin 直接判斷（同「監控與診斷」的其他頁）。
            // 收件者是個資，權限未通過前不讀任何資料。
            if (AuthenticationStateHelper.CheckIsAdmin() == false)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/email-logs");
                logger.LogWarning("Email log view denied because the current user is not an administrator.");
                return;
            }

            await ReloadAsync();
        }

        private async Task ReloadAsync()
        {
            isLoading = true;
            StateHasChanged();
            try
            {
                var (rows, total) = await queryService.GetPageAsync(new EmailLogQuery(
                    startTime, endTime, selectedStatus, selectedKind, keyword, _pageIndex, _pageSize));
                items = rows.ToList();
                _total = total;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load email logs.");
                ViewNotification.UnexpectedError(notificationService, $"載入寄信紀錄失敗：{ex.GetType().Name}。", ex);
                items = [];
                _total = 0;
            }
            finally
            {
                isLoading = false;
            }
        }

        private async Task OnTableChange(QueryModel<EmailLogItem> args)
        {
            _pageIndex = args.PageIndex;
            _pageSize = args.PageSize;
            await ReloadAsync();
        }

        private async Task OnSearchAsync()
        {
            _pageIndex = 1;
            logger.LogInformation("Email logs searched. HasSearch={HasSearch}, SearchLength={SearchLength}",
                string.IsNullOrWhiteSpace(keyword) == false, keyword.Length);
            await ReloadAsync();
        }

        private async Task OnRefreshAsync()
        {
            await ReloadAsync();
            ViewNotification.Warning(notificationService, "已更新最新資料");
        }

        private static string KindLabel(string kind) => Kinds.FirstOrDefault(x => x.Value == kind)?.Label ?? kind;

        private static string StatusLabel(string status) => Statuses.FirstOrDefault(x => x.Value == status)?.Label ?? status;

        private static string StatusColor(string status) => Statuses.FirstOrDefault(x => x.Value == status)?.Color ?? "default";
    }
}
