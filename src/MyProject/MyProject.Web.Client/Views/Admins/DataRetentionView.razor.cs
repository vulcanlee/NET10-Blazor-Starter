using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MyProject.Dtos.Admins;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Client.Views.Admins
{
    /// <summary>
    /// 資料保留與清理（0.9.133 起；0.10.6 起在 WASM 執行，WASM 遷移 M7）：每種資料的數量、檔案空間、最舊一筆、保留規則、下次會清掉幾筆與負責的排程；
    /// 「立即清理」走「排程作業」頁的同一個佇列，「前往」到細部刪除的頁面。管理員專屬：權限鍵刻意不進角色矩陣，以 IsAdmin 判斷。
    /// 畫面照 Server 版，資料改走 <see cref="DataRetentionApi"/>（只限管理員），稽核由伺服器寫；「今天」用伺服器的日期。
    /// 有清理作業在佇列或執行中時每 5 秒重新讀一次（Server 版用計時器，這裡改成輪詢 API，離開頁面就停）。
    /// </summary>
    public partial class DataRetentionView : IDisposable
    {
        /// <summary>有清理作業在佇列或執行中時，每隔這麼久自動更新一次。</summary>
        private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(5);

        /// <summary>與伺服器的 <c>SoftDeletePurgeJob.JobName</c> 相同（Client 不參考 Web）。</summary>
        internal const string SoftDeletePurgeJobName = "SoftDeletePurge";

        /// <summary>與伺服器的 <c>CustomPurgeCutoff.MaxKeepDays</c> 相同（超過由伺服器擋）。</summary>
        private const int MaxKeepDays = 3650;

        [Inject]
        public DataRetentionApi Api { get; set; } = default!;

        [Inject]
        public PagePermissions Permissions { get; set; } = default!;

        [Inject]
        public ModalService ModalService { get; set; } = default!;

        [Inject]
        public NotificationService NotificationService { get; set; } = default!;

        [Inject]
        public ILogger<DataRetentionView> Logger { get; set; } = default!;

        private DataRetentionOverviewDto? overview;
        private bool isLoading;
        private string RoleMessage = string.Empty;
        private bool isAccessChecked;
        private CancellationTokenSource? autoRefresh;
        private bool disposed;

        // 自訂清除窗（0.9.134 起）。
        private const string KeepDaysMode = "keepDays";
        private const string BeforeDateMode = "beforeDate";
        private bool customVisible;
        private string customTitle = "自訂清除";
        private DataRetentionRowDto? customRow;
        private string customMode = KeepDaysMode;
        private int? customDays = 30;
        private DateTime? customDate;
        private int? customPreview;
        private string? customError;

        /// <summary>預覽是非同步的：使用者連續改值時，只採用最後一次的結果。</summary>
        private int previewVersion;

        protected override async Task OnInitializedAsync()
        {
            isAccessChecked = true;

            // 沒有權限的開頁稽核由伺服器在開頁紀錄時寫（shell/page-view），這裡不讀任何資料。
            if (!Permissions.IsAdmin)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                Logger.LogWarning("Data retention view denied because the current user is not an administrator.");
                return;
            }

            await ReloadAsync();
        }

        /// <summary>
        /// 重新讀取。<paramref name="background"/>＝執行中自動輪詢（0.10.6 獨立審查）：失敗只記日誌、不跳通知 ——
        /// 錯誤通知不會自動關閉，伺服器重啟或斷線時每次輪詢都疊一則；下一次輪詢或使用者按重新整理時再試。
        /// </summary>
        private async Task ReloadAsync(bool background = false)
        {
            isLoading = true;
            try
            {
                var result = await Api.OverviewAsync();
                if (result.Success && result.Data is { } data)
                {
                    overview = data;
                }
                else if (background)
                {
                    Logger.LogWarning("Polling data retention failed. Message={Message}", result.UserMessage());
                }
                else
                {
                    ViewNotification.Error(NotificationService, "讀取資料保留資訊時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", result.UserMessage());
                }
            }
            finally
            {
                isLoading = false;
            }

            UpdateAutoRefresh();
        }

        private async Task OnRefreshAsync()
        {
            await ReloadAsync();
            ViewNotification.Info(NotificationService, "已更新最新資料");
        }

        /// <summary>
        /// 立即清理：先重抓一次數字（確認窗顯示的要是最新的），再請伺服器放進「立即執行」佇列（伺服器再確認一次能不能清、寫 <c>Job.Trigger</c> 稽核）。
        /// 已刪除資料的五列共用一個作業，按任何一列都會五種一起清，確認窗逐項列出。
        /// </summary>
        private async Task OnPurgeNowAsync(DataRetentionRowDto row)
        {
            try
            {
                if (row.JobName is not { } jobName)
                {
                    return;
                }

                await ReloadAsync();
                var related = overview?.Rows.Where(x => x.JobName == jobName && x.Error is null).ToList() ?? [];
                var current = related.FirstOrDefault(x => x.Key == row.Key) ?? row;
                if (!current.CanPurgeNow)
                {
                    ViewNotification.Caution(NotificationService, $"現在不能清理「{row.Label}」：{current.PurgeDisabledReason}。");
                    return;
                }

                var expected = related.Sum(x => x.Expected ?? 0);
                if (!await ConfirmDialog.AskDestructiveAsync(ModalService, "立即清理", BuildConfirmContent(current, related, expected), "立即清理"))
                {
                    return;
                }

                var result = await Api.PurgeNowAsync(row.Key);
                if (!result.Success)
                {
                    // 已在執行中、現在不能清理：與 Server 版同一句，由伺服器判斷。
                    ViewNotification.Caution(NotificationService, result.UserMessage());
                    return;
                }

                Logger.LogInformation("Data retention purge triggered manually. JobName={JobName}, Expected={Expected}", jobName, expected);
                ViewNotification.Warning(NotificationService, result.Message);
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unhandled exception while triggering a data retention purge.");
                ViewNotification.UnexpectedError(NotificationService, "啟動清理時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        /// <summary>已刪除的五種共用一個排程，除了第一列（專案）以外不重複顯示執行訊息。</summary>
        private static bool IsSoftDeleteFollower(DataRetentionRowDto row)
            => row.JobName == SoftDeletePurgeJobName && row.Key != "deleted-project";

        private bool CanRunCustomPurge => customError is null && customPreview > 0;

        private CustomPurgeRequestDto CurrentCutoff => customMode == KeepDaysMode
            ? new CustomPurgeRequestDto { KeepDays = customDays }
            : new CustomPurgeRequestDto { BeforeDate = customDate is { } date ? DateOnly.FromDateTime(date) : null };

        private async Task OnOpenCustomPurgeAsync(DataRetentionRowDto row)
        {
            customRow = row;
            customTitle = $"自訂清除：{row.Label}";
            customMode = KeepDaysMode;
            customDays = 30;
            // 預設日期由伺服器的「今天」往前推（不用瀏覽器的日期）。
            customDate = (overview?.TodayLocal ?? DateTime.Today).Date.AddDays(-30);
            customVisible = true;
            await UpdatePreviewAsync();
        }

        /// <summary>預覽會刪幾筆：條件由伺服器以自己的「今天」驗證，不合格（400）時把原因顯示在窗裡。</summary>
        private async Task UpdatePreviewAsync()
        {
            if (customRow is null)
            {
                return;
            }

            var version = ++previewVersion;
            customPreview = null;
            customError = null;
            var result = await Api.PreviewAsync(customRow.Key, CurrentCutoff);
            if (version != previewVersion)
            {
                return;
            }

            if (result.Success)
            {
                customPreview = result.Data;
            }
            else if (result.StatusCode == 400)
            {
                customError = result.UserMessage();
            }
            else
            {
                Logger.LogWarning("Failed to preview a custom purge. RowKey={RowKey}, StatusCode={StatusCode}", customRow.Key, result.StatusCode);
                customError = $"無法計算會刪除幾筆：{result.UserMessage()}";
            }
        }

        /// <summary>
        /// 「清除」。⚠️ 第一行的 customVisible = true 不可移動（同 FormModalFlow 的說明）：AntDesign 已先送出關窗，
        /// 這裡搶回來，之後由 RunOkAsync 的結果決定開關 —— 確認窗按取消時留在這個窗。
        /// </summary>
        private async Task OnCustomPurgeOkAsync()
        {
            customVisible = true;
            customVisible = await FormModalFlow.RunOkAsync(
                RunCustomPurgeAsync,
                Logger,
                NotificationService,
                "清除資料時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
        }

        /// <summary>實際清除（伺服器刪資料、寫稽核）；回傳 true 表示完成、可以關窗。</summary>
        private async Task<bool> RunCustomPurgeAsync()
        {
            if (customRow is not { } row || !CanRunCustomPurge)
            {
                return false;
            }

            var cutoff = CurrentCutoff;
            var preview = await Api.PreviewAsync(row.Key, cutoff);
            if (!preview.Success)
            {
                ViewNotification.Error(NotificationService, preview.UserMessage());
                return false;
            }

            if (!await ConfirmDialog.AskDestructiveAsync(ModalService, "自訂清除",
                    $"將依「{Describe(cutoff)}」刪除「{row.Label}」約 {preview.Data:N0} {row.CountUnit}{(row.Note is { } note ? $"，{note}" : string.Empty)}。\n此動作無法復原，也不會改到系統參數的保留天數。",
                    "清除"))
            {
                return false;
            }

            var result = await Api.CustomPurgeAsync(row.Key, cutoff);
            if (!result.Success || result.Data is not { } outcome)
            {
                ViewNotification.Error(NotificationService, "清除資料時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", result.UserMessage());
                return false;
            }

            Logger.LogInformation("Custom purge completed. RowKey={RowKey}, Removed={Removed}, Left={Left}, Failed={Failed}",
                row.Key, outcome.Removed, outcome.Left, outcome.Failed);
            var message = $"已刪除 {outcome.Removed:N0} {row.CountUnit}「{row.Label}」"
                + (outcome.Left > 0 ? $"；{outcome.Left:N0} {row.CountUnit}受保護、仍在使用或中途被還原，留下。" : "。");
            if (outcome.Failed)
            {
                ViewNotification.Error(NotificationService, message + "途中有一筆失敗，已停止；詳細內容請看「系統例外紀錄」。");
            }
            else
            {
                ViewNotification.Warning(NotificationService, message);
            }

            await ReloadAsync();
            return true;
        }

        /// <summary>與伺服器 <c>CustomPurgeCutoff.Describe</c> 相同的文字。</summary>
        internal static string Describe(CustomPurgeRequestDto cutoff) => cutoff.KeepDays is { } days
            ? $"只留最近 {days} 天"
            : $"刪除 {cutoff.BeforeDate:yyyy-MM-dd} 之前（不含當天）";

        internal static string BuildConfirmContent(DataRetentionRowDto row, IReadOnlyList<DataRetentionRowDto> related, int expected)
        {
            var lines = new List<string>();
            if (row.JobName == SoftDeletePurgeJobName)
            {
                lines.Add($"已刪除資料是一起清理的，會同時永久刪除這五種中超過保留期限的資料（{row.RuleText}）：");
                lines.AddRange(related.Select(x => $"・{x.Label}：{x.Expected ?? 0:N0} {x.CountUnit}"));
                lines.Add($"共最多 {expected:N0} 筆；support 帳號、預設角色、仍在使用的角色與還有下屬的團隊會留到下次。");
            }
            else
            {
                lines.Add($"將依目前的保留規則（{row.RuleText}）刪除「{row.Label}」中約 {expected:N0} {row.CountUnit}{(row.Note is { } note ? $"，{note}" : string.Empty)}。");
            }

            if (row.Job is { NextRunLocal: null })
            {
                lines.Add("這個作業目前不會依排程執行，這次只執行一次。");
            }

            lines.Add("清理在背景進行，此動作無法復原。");
            return string.Join('\n', lines);
        }

        private static string ParameterUrl(string group) => $"/system-parameters?group={Uri.EscapeDataString(group)}";

        private static string FolderText(DataRetentionFolderDto? folder) => folder switch
        {
            null => "—",
            { Status: "NotConfigured" } => "未設定",
            { Status: "Missing" } => "0 B",
            { Status: "Partial" } => $"至少 {SizeFormatHelper.FormatBytes(folder.Bytes)}",
            _ => SizeFormatHelper.FormatBytes(folder.Bytes),
        };

        private static string StatusText(string status) => status switch
        {
            JobRunStatuses.Running => "執行中",
            JobRunStatuses.Succeeded => "成功",
            JobRunStatuses.Failed => "失敗",
            JobRunStatuses.Interrupted => "中斷",
            JobRunStatuses.Skipped => "略過",
            _ => status,
        };

        /// <summary>有清理作業在佇列或執行中就每 5 秒自動更新；都結束了就停掉，不白白查資料庫。</summary>
        private void UpdateAutoRefresh()
        {
            var busy = overview?.Rows.Any(x => x.Job is { } job && (job.IsQueued || job.LastRun?.Status == JobRunStatuses.Running)) == true;
            if (busy && autoRefresh is null && !disposed)
            {
                autoRefresh = new CancellationTokenSource();
                _ = AutoRefreshAsync(autoRefresh.Token);
            }
            else if (!busy && autoRefresh is not null)
            {
                autoRefresh.Cancel();
                autoRefresh = null;
            }
        }

        private async Task AutoRefreshAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(AutoRefreshInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    await InvokeAsync(async () =>
                    {
                        if (disposed || cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }

                        await ReloadAsync(background: true);
                        StateHasChanged();
                    });
                }
            }
            catch (OperationCanceledException)
            {
                Logger.LogDebug("Data retention auto refresh stopped.");
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Data retention auto refresh failed.");
            }
        }

        public void Dispose()
        {
            disposed = true;
            autoRefresh?.Cancel();
            autoRefresh = null;
        }
    }
}
