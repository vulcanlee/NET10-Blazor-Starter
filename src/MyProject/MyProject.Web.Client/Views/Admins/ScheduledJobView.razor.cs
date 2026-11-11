using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MyProject.Dtos.Admins;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Client.Layout;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Client.Views.Admins
{
    /// <summary>
    /// 排程作業（0.9.96 起；0.10.6 起在 WASM 執行，WASM 遷移 M7）：每個作業的執行時間、下次執行、最近一次結果、啟用開關、立即執行與執行紀錄。
    /// 管理員專屬：權限鍵刻意不進角色矩陣，以 Permissions.IsAdmin 判斷。資料走 <see cref="ScheduledJobApi"/>，稽核由伺服器寫。
    /// 排程器是伺服器的單例狀態：Server 版靠伺服器端計時器更新畫面，這裡改成有作業在佇列或執行中時定期輪詢總覽，都結束就停。
    /// </summary>
    public partial class ScheduledJobView : IAsyncDisposable
    {
        /// <summary>有作業在佇列或執行中時，每隔這麼久更新一次（Server 版是 5 秒）。</summary>
        internal static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(2);

        private ScheduledJobOverviewDto? overview;
        private bool isLoading;
        private string RoleMessage = string.Empty;
        private bool isAccessChecked;

        private bool runsVisible;
        private bool isRunsLoading;
        private string runsTitle = "執行紀錄";
        private List<ScheduledJobRunDto> runs = [];

        private PeriodicRefresh? autoRefresh;
        private bool isStartingAutoRefresh;
        private bool disposed;

        [Inject]
        public ScheduledJobApi Api { get; set; } = default!;

        [Inject]
        public PagePermissions Permissions { get; set; } = default!;

        [Inject]
        public ModalService ModalService { get; set; } = default!;

        [Inject]
        public NotificationService NotificationService { get; set; } = default!;

        [Inject]
        public IJSRuntime JSRuntime { get; set; } = default!;

        [Inject]
        public ILogger<ScheduledJobView> Logger { get; set; } = default!;

        protected override async Task OnInitializedAsync()
        {
            isAccessChecked = true;

            // 沒有權限的開頁稽核由伺服器在開頁紀錄時寫（shell/page-view），這裡不讀任何資料。
            if (!Permissions.IsAdmin)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                Logger.LogWarning("Scheduled job view denied because the current user is not an administrator.");
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
                    Logger.LogWarning("Polling scheduled jobs failed. Message={Message}", result.UserMessage());
                }
                else
                {
                    ViewNotification.Error(NotificationService, $"讀取排程作業失敗：{result.UserMessage()}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to load scheduled jobs.");
                if (!background)
                {
                    ViewNotification.UnexpectedError(NotificationService, "讀取排程作業時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
                }
            }
            finally
            {
                isLoading = false;
            }

            await UpdateAutoRefreshAsync();
        }

        private async Task OnRefreshAsync()
        {
            await ReloadAsync();
            ViewNotification.Info(NotificationService, "已更新最新資料");
        }

        private async Task OnTriggerAsync(ScheduledJobItemDto item)
        {
            try
            {
                var ok = await ConfirmDialog.AskAsync(
                    ModalService,
                    "立即執行",
                    $"要立即執行「{item.DisplayName}」嗎？執行會在背景進行，完成後這一頁會自動更新結果。",
                    "立即執行");
                if (!ok)
                {
                    return;
                }

                var result = await Api.TriggerAsync(item.Name);
                if (result.StatusCode == 409)
                {
                    ViewNotification.Caution(NotificationService, "這個作業已經在執行中，請等它完成。");
                    return;
                }

                if (!result.Success)
                {
                    ViewNotification.Error(NotificationService, result.UserMessage());
                    return;
                }

                Logger.LogInformation("Scheduled job triggered manually. JobName={JobName}", item.Name);
                ViewNotification.Warning(NotificationService, $"已開始執行「{item.DisplayName}」。");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unhandled exception while triggering a scheduled job.");
                ViewNotification.UnexpectedError(NotificationService, "啟動排程作業時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        private async Task OnToggleEnabledAsync(ScheduledJobItemDto item, bool enabled)
        {
            try
            {
                var result = await Api.SetEnabledAsync(item.Name, enabled);
                if (!result.Success)
                {
                    ViewNotification.Error(NotificationService, result.UserMessage());
                    await ReloadAsync();
                    return;
                }

                Logger.LogInformation("Scheduled job enabled state changed from the page. JobName={JobName}, Enabled={Enabled}", item.Name, enabled);
                ViewNotification.Warning(NotificationService, enabled
                    ? $"已啟用「{item.DisplayName}」，從下一個時段開始依排程執行。"
                    : $"已停用「{item.DisplayName}」，不會再依排程執行（仍可按「立即執行」）。");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unhandled exception while changing a scheduled job enabled state.");
                ViewNotification.UnexpectedError(NotificationService, "切換排程作業時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
                await ReloadAsync();
            }
        }

        private async Task OnShowRunsAsync(ScheduledJobItemDto item)
        {
            runsTitle = $"執行紀錄：{item.DisplayName}";
            runsVisible = true;
            isRunsLoading = true;
            try
            {
                var result = await Api.RunsAsync(item.Name);
                if (result.Success && result.Data is { } data)
                {
                    runs = data;
                }
                else
                {
                    ViewNotification.Error(NotificationService, $"讀取執行紀錄失敗：{result.UserMessage()}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to load scheduled job runs.");
                ViewNotification.UnexpectedError(NotificationService, "讀取執行紀錄時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
            finally
            {
                isRunsLoading = false;
            }
        }

        private static bool IsRunning(ScheduledJobItemDto item) => item.LastRun?.Status == JobRunStatuses.Running;

        /// <summary>有作業在佇列或執行中時開始定期更新；都結束了就停掉，不白白查資料庫。</summary>
        internal static bool IsBusy(ScheduledJobOverviewDto? overview) => overview?.Items.Any(x => x.IsQueued || IsRunning(x)) == true;

        private async Task UpdateAutoRefreshAsync()
        {
            var busy = IsBusy(overview);
            if (busy && autoRefresh is null && !isStartingAutoRefresh && !disposed)
            {
                isStartingAutoRefresh = true;
                try
                {
                    var started = await PeriodicRefresh.StartAsync(JSRuntime, AutoRefreshInterval, () => InvokeAsync(async () =>
                    {
                        if (disposed)
                        {
                            return;
                        }

                        await ReloadAsync(background: true);
                        StateHasChanged();
                    }), Logger);

                    // 啟動途中已離開這一頁：立刻停掉，不留下沒人管的計時器。
                    if (disposed)
                    {
                        await started.DisposeAsync();
                        return;
                    }

                    autoRefresh = started;
                }
                finally
                {
                    isStartingAutoRefresh = false;
                }
            }
            else if (!busy && autoRefresh is not null)
            {
                var stopping = autoRefresh;
                autoRefresh = null;
                await stopping.DisposeAsync();
            }
        }

        private static string LocalText(DateTime local) => local.ToString("yyyy-MM-dd HH:mm:ss");

        private static string DurationText(long milliseconds)
            => milliseconds < 1000 ? $"{milliseconds} ms" : $"{milliseconds / 1000d:0.#} 秒";

        private static string StatusText(string status) => status switch
        {
            JobRunStatuses.Running => "執行中",
            JobRunStatuses.Succeeded => "成功",
            JobRunStatuses.Failed => "失敗",
            JobRunStatuses.Interrupted => "中斷",
            JobRunStatuses.Skipped => "略過",
            _ => status,
        };

        private static StatusTone ToneOf(string status) => status switch
        {
            JobRunStatuses.Succeeded => StatusTone.Positive,
            JobRunStatuses.Running => StatusTone.Accent,
            JobRunStatuses.Failed or JobRunStatuses.Interrupted => StatusTone.Warning,
            _ => StatusTone.Muted,
        };

        private static string TriggerText(string trigger) => trigger switch
        {
            JobRunTriggers.Schedule => "排程",
            JobRunTriggers.CatchUp => "補跑",
            JobRunTriggers.Manual => "手動",
            _ => trigger,
        };

        public async ValueTask DisposeAsync()
        {
            disposed = true;
            if (autoRefresh is not null)
            {
                var stopping = autoRefresh;
                autoRefresh = null;
                await stopping.DisposeAsync();
            }

            GC.SuppressFinalize(this);
        }
    }
}
