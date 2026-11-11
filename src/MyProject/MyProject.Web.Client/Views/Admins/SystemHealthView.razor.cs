using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MyProject.Dtos.Admins;
using MyProject.Share.Helpers;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Client.Layout;
using MyProject.Web.Components.Commons;
using MyProject.Web.Diagnostics;

namespace MyProject.Web.Client.Views.Admins
{
    /// <summary>
    /// 系統健康監控（0.10.6 起在 WASM 執行，WASM 遷移 M7；之前是 Server 版的 <c>SystemHealthPage</c>）：健康報告、寄信測試、站內通知測試、
    /// 最近 24 小時警告以上日誌。管理員專屬；每個動作各有 API（<see cref="SystemHealthApi"/>），稽核由伺服器的服務寫。
    /// ⚠️ 開頁讀報告會執行全部檢查，AI 已設定時「LLM API」一項會真的呼叫模型（產生費用）。
    /// </summary>
    public partial class SystemHealthView
    {
        private SystemHealthReportDto? report;
        private string? roleMessage;
        private bool isAccessChecked;
        private string? testEmailTo;
        private bool isEmailEnabled;
        private string emailProviderText = string.Empty;
        private bool isSendingTestEmail;
        private SystemHealthTestResultDto? testEmailResult;
        private bool isSendingTestNotification;
        private SystemHealthTestResultDto? testNotificationResult;

        /// <summary>最近 24 小時 WARN 以上日誌，新到舊；null 表示讀取中。</summary>
        private List<LogEntryDto>? warnLogEntries;
        private string warnLogMessage = string.Empty;
        private string warnLogEmptyText = string.Empty;
        private List<string> warnLogWarnings = new();

        [Inject]
        public SystemHealthApi Api { get; set; } = default!;

        [Inject]
        public PagePermissions Permissions { get; set; } = default!;

        [Inject]
        public NotificationService NotificationService { get; set; } = default!;

        [Inject]
        public ClientNotificationSignal NotificationSignal { get; set; } = default!;

        [Inject]
        public ILogger<SystemHealthView> Logger { get; set; } = default!;

        protected override async Task OnInitializedAsync()
        {
            Logger.LogInformation("Initializing system health page.");
            isAccessChecked = true;

            // 沒有權限的開頁稽核由伺服器在開頁紀錄時寫（shell/page-view），這裡不讀任何資料。
            if (!Permissions.IsAdmin)
            {
                roleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                Logger.LogWarning("System health page denied because current user is not an administrator.");
                return;
            }

            var result = await Api.ReportAsync();
            if (!result.Success || result.Data is not { } data)
            {
                // 讀不到報告時畫面停在「正在讀取…」與 Server 版頁面丟例外時不同；用通知告訴使用者原因。
                ViewNotification.Error(NotificationService, $"讀取系統健康狀態失敗：{result.UserMessage()}");
                return;
            }

            isEmailEnabled = data.IsEmailEnabled;
            emailProviderText = data.EmailProviderText;
            testEmailTo = data.DefaultTestEmailTo;
            report = data;
            StateHasChanged();

            await LoadWarnLogsAsync();
        }

        /// <summary>與健康報告分開讀取：日誌讀取失敗只影響本區塊（伺服器已把原因放進空白文字）。</summary>
        private async Task LoadWarnLogsAsync()
        {
            var result = await Api.WarnLogsAsync();
            if (result.Success && result.Data is { } data)
            {
                warnLogEntries = data.Entries;
                warnLogWarnings = data.Warnings;
                warnLogMessage = data.Message;
                warnLogEmptyText = data.EmptyText;
                return;
            }

            warnLogEntries = new();
            warnLogMessage = string.Empty;
            warnLogEmptyText = $"讀取日誌失敗：{result.UserMessage()}";
        }

        private async Task SendTestEmailAsync()
        {
            isSendingTestEmail = true;
            testEmailResult = null;

            try
            {
                var result = await Api.TestEmailAsync(testEmailTo);
                testEmailResult = result.Success && result.Data is { } data
                    ? data
                    : new SystemHealthTestResultDto { Success = false, Message = result.UserMessage() };
            }
            finally
            {
                isSendingTestEmail = false;
            }
        }

        private async Task SendTestNotificationAsync()
        {
            isSendingTestNotification = true;
            testNotificationResult = null;

            try
            {
                var result = await Api.TestNotificationAsync();
                testNotificationResult = result.Success && result.Data is { } data
                    ? data
                    : new SystemHealthTestResultDto { Success = false, Message = result.UserMessage() };

                // 通知發給全體管理員（含自己）：外框的鈴鐺平常 60 秒輪詢一次，這裡請它立即更新。
                if (testNotificationResult.Success)
                {
                    NotificationSignal.RequestRefresh();
                }
            }
            finally
            {
                isSendingTestNotification = false;
            }
        }

        private static LogLevelRank RankOf(LogEntryDto entry) => LogLevelRankHelper.FromLevelText(entry.Level, LogLevelRank.Unknown);

        private static string GetLightClass(string light)
        {
            return light switch
            {
                "Green" => "light-green",
                "Yellow" => "light-yellow",
                _ => "light-red"
            };
        }

        private static string GetStatusText(string status)
        {
            return status switch
            {
                "Healthy" => "正常",
                "Degraded" => "警示",
                _ => "異常"
            };
        }
    }
}
