using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MyProject.Dtos.Admins;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Client.Commons;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Client.Views.Admins
{
    /// <summary>
    /// 系統巡檢報告（0.10.39 起；週報、月報 0.10.41 起）：報告清單、查看報告、立即產生，以及舊版巡檢報告（0.9.161～0.10.38 的 AI 巡檢）。
    /// 資料走 <see cref="SystemReportApi"/>（只限管理員；看報告不寫稽核，立即產生由伺服器寫 Job.Trigger 稽核）。
    /// 燈號用 AntDesign Tag 的顏色＋文字表示，不用 emoji（速查表 §6.1）。
    /// </summary>
    public partial class SystemReportView
    {
        internal sealed record Option(string Value, string Label);

        internal static readonly IReadOnlyList<Option> Frequencies =
        [
            new(SystemReportFrequencies.Daily, "日報"),
            new(SystemReportFrequencies.Weekly, "週報"),
            new(SystemReportFrequencies.Monthly, "月報"),
            new(SystemReportFrequencies.Custom, "指定區間"),
        ];

        /// <summary>「立即產生」可選的種類（指定區間只由系統管理助理產生）。</summary>
        internal static readonly IReadOnlyList<Option> GenerateFrequencies =
        [
            new(SystemReportFrequencies.Daily, "日報"),
            new(SystemReportFrequencies.Weekly, "週報"),
            new(SystemReportFrequencies.Monthly, "月報"),
        ];

        internal static readonly IReadOnlyList<Option> Levels =
        [
            new(SystemReportLevels.Red, "紅燈（已發生問題）"),
            new(SystemReportLevels.Orange, "橙燈（即將發生）"),
            new(SystemReportLevels.Yellow, "黃燈（注意）"),
            new(SystemReportLevels.Green, "綠燈（正常）"),
        ];

        [Inject]
        public SystemReportApi Api { get; set; } = default!;

        [Inject]
        public PagePermissions Permissions { get; set; } = default!;

        [Inject]
        public NotificationService NotificationService { get; set; } = default!;

        [Inject]
        public ILogger<SystemReportView> Logger { get; set; } = default!;

        /// <summary>網址帶的報告（通知與 Email 的連結）；畫面渲染完自動打開。</summary>
        [Parameter]
        public Guid? InitialReportId { get; set; }

        /// <summary>網址帶的舊版巡檢報告（0.10.38 以前的通知連結）。</summary>
        [Parameter]
        public Guid? InitialLegacyReportId { get; set; }

        private bool isAccessChecked;
        private bool isLoading;
        private List<SystemReportSummaryDto> items = [];
        private int _pageIndex = 1;
        private int _pageSize = MagicObjectHelper.PageSize;
        private int _total;
        private int retentionDays;
        private int weeklyRetentionDays;
        private int monthlyRetentionDays;
        private string selectedFrequency = string.Empty;
        private string selectedLevel = string.Empty;
        private DateTime? fromDate;
        private DateTime? toDate;

        private SystemReportDetailModal? detailModal;
        private AdminPatrolReportsModal? legacyModal;

        private bool generateVisible;
        private bool isGenerating;
        private DateTime? generateDate;
        private string generateFrequency = SystemReportFrequencies.Daily;
        private bool generateSendToAdmins;

        public string RoleMessage { get; set; } = string.Empty;

        protected override async Task OnInitializedAsync()
        {
            isAccessChecked = true;

            // 管理員專屬：權限未通過前不讀任何資料；沒有權限的開頁稽核由伺服器在開頁紀錄時寫（shell/page-view）。
            if (!Permissions.IsAdmin)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                Logger.LogWarning("System report view denied because the current user is not an administrator.");
                return;
            }

            await ReloadAsync();
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            // 深連結：等畫面渲染完才開，視窗的 @ref 才有值；只開一次。
            if (InitialReportId is { } reportId && detailModal is not null)
            {
                InitialReportId = null;
                await detailModal.OpenAsync(reportId);
            }

            if (InitialLegacyReportId is { } legacyId && legacyModal is not null)
            {
                InitialLegacyReportId = null;
                if (await legacyModal.OpenAsync(legacyId) == false)
                {
                    ViewNotification.Caution(NotificationService, "找不到這份巡檢報告，可能已超過保存期限被清除。");
                }
            }
        }

        /// <summary>重新讀取；回傳是否成功（失敗時已顯示通知）。</summary>
        private async Task<bool> ReloadAsync()
        {
            isLoading = true;
            StateHasChanged();
            try
            {
                var result = await Api.SearchAsync(new SystemReportSearchRequestDto
                {
                    Frequency = selectedFrequency,
                    Level = selectedLevel,
                    From = ClientCulture.Unzoned(fromDate),
                    To = ClientCulture.Unzoned(toDate),
                    PageIndex = _pageIndex,
                    PageSize = _pageSize,
                });
                if (result.Success && result.Data is { } page)
                {
                    items = page.Items;
                    _total = page.TotalCount;
                    retentionDays = page.DailyRetentionDays;
                    weeklyRetentionDays = page.WeeklyRetentionDays;
                    monthlyRetentionDays = page.MonthlyRetentionDays;
                    return true;
                }

                ViewNotification.Error(NotificationService, $"載入系統巡檢報告失敗：{result.UserMessage()}");
                items = [];
                _total = 0;
                return false;
            }
            finally
            {
                isLoading = false;
            }
        }

        private async Task OnTableChange(QueryModel<SystemReportSummaryDto> args)
        {
            _pageIndex = args.PageIndex;
            _pageSize = args.PageSize;
            await ReloadAsync();
        }

        private async Task OnSearchAsync()
        {
            _pageIndex = 1;
            Logger.LogDebug("System reports searched. HasFrequency={HasFrequency}, HasLevel={HasLevel}",
                string.IsNullOrEmpty(selectedFrequency) == false, string.IsNullOrEmpty(selectedLevel) == false);
            await ReloadAsync();
        }

        private async Task OnRefreshAsync()
        {
            if (await ReloadAsync())
            {
                ViewNotification.Warning(NotificationService, "已更新最新資料");
            }
        }

        private async Task OnViewAsync(Guid reportId)
        {
            if (detailModal is not null)
            {
                await detailModal.OpenAsync(reportId);
            }
        }

        private async Task OnOpenLegacyAsync()
        {
            if (legacyModal is not null)
            {
                await legacyModal.OpenAsync();
            }
        }

        private void OnOpenGenerate()
        {
            generateDate = DateTime.Today.AddDays(-1);
            generateFrequency = SystemReportFrequencies.Daily;
            generateSendToAdmins = false;
            generateVisible = true;
            Logger.LogDebug("System report generate dialog opened.");
        }

        private void OnGenerateCancel() => generateVisible = false;

        private async Task OnGenerateOkAsync()
        {
            if (generateDate is null)
            {
                ViewNotification.Caution(NotificationService, "請選擇日期。");
                return;
            }

            isGenerating = true;
            try
            {
                var result = await Api.GenerateAsync(new SystemReportGenerateRequestDto
                {
                    Frequency = generateFrequency,
                    Date = ClientCulture.Unzoned(generateDate),
                    SendToAdmins = generateSendToAdmins,
                });
                if (!result.Success)
                {
                    ViewNotification.Error(NotificationService, result.UserMessage());
                    return;
                }

                generateVisible = false;
                Logger.LogInformation("System report generation requested. Frequency={Frequency}, SendToAdmins={SendToAdmins}", generateFrequency, generateSendToAdmins);
                ViewNotification.Warning(NotificationService, result.Message);
            }
            finally
            {
                isGenerating = false;
            }
        }

        /// <summary>日期欄的說明：週報、月報選區間內任一天即可。</summary>
        private string GenerateDateLabel => generateFrequency switch
        {
            SystemReportFrequencies.Weekly => "日期（選那一週的任一天）",
            SystemReportFrequencies.Monthly => "日期（選那個月的任一天）",
            _ => "日期",
        };

        private static string PeriodText(SystemReportSummaryDto item)
        {
            var last = item.PeriodEnd.AddDays(-1);
            return last.Date == item.PeriodStart.Date
                ? item.PeriodStart.ToString("yyyy-MM-dd")
                : $"{item.PeriodStart:yyyy-MM-dd}～{last:yyyy-MM-dd}";
        }

        internal static string LevelColor(string level) => level switch
        {
            SystemReportLevels.Red => "red",
            SystemReportLevels.Orange => "orange",
            SystemReportLevels.Yellow => "gold",
            SystemReportLevels.Green => "green",
            _ => "default",
        };

        internal static string LevelText(string level) => $"{SystemReportLevels.ShortName(level)}・{SystemReportLevels.Meaning(level)}";

        private static string TriggerText(SystemReportSummaryDto item) => item.Trigger switch
        {
            SystemReportTriggers.Manual => $"手動（{item.TriggeredByAccount}）",
            SystemReportTriggers.CatchUp => "補跑",
            SystemReportTriggers.Assistant => $"系統管理助理（{item.TriggeredByAccount}）",
            _ => "排程",
        };

        private static string DeliveryText(string status) => status switch
        {
            SystemReportDeliveryStatuses.Sent => "已通知",
            SystemReportDeliveryStatuses.Failed => "通知失敗",
            _ => "不需通知",
        };

        private static string NarrativeText(string status) => status switch
        {
            SystemReportNarrativeStatuses.Written => "有",
            SystemReportNarrativeStatuses.Fallback => "產生失敗",
            _ => "未使用",
        };
    }
}
