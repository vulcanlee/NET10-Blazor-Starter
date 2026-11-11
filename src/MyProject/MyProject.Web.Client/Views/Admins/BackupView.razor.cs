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
    /// 系統備份（0.9.99 起；0.10.6 起在 WASM 執行，WASM 遷移 M7）：立即備份、備份清單、下載與刪除。管理員專屬：權限鍵刻意不進角色矩陣，以 IsAdmin 判斷。
    /// 畫面照 Server 版，資料改走 <see cref="BackupApi"/>（只限管理員），稽核由伺服器寫。
    /// 「立即備份」放進排程框架的佇列（與排程共用作業鎖），備份進行中每 5 秒重新讀一次清單（Server 版用計時器，這裡改成輪詢 API，離開頁面就停）。
    /// 下載改用一次性票證＋瀏覽器原生下載（<c>&lt;a download&gt;</c>，大檔不經 WASM 記憶體；票證只能用一次，所以不支援續傳）。
    /// </summary>
    public partial class BackupView : IDisposable
    {
        private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(5);

        [Inject]
        public BackupApi Api { get; set; } = default!;

        [Inject]
        public PagePermissions Permissions { get; set; } = default!;

        [Inject]
        public ModalService ModalService { get; set; } = default!;

        [Inject]
        public NotificationService NotificationService { get; set; } = default!;

        [Inject]
        public ILogger<BackupView> Logger { get; set; } = default!;

        private List<BackupFileDto> backups = [];
        private IEnumerable<BackupFileDto> selectedRows = [];
        private bool batchDeleting;
        private MaintenanceJobDto? job;
        private int keepCount;
        private bool isLoading;
        private string RoleMessage = string.Empty;
        private bool isAccessChecked;
        private CancellationTokenSource? autoRefresh;
        private bool disposed;

        private bool IsBusy => job is not null && (job.IsQueued || job.LastRun?.Status == JobRunStatuses.Running);

        protected override async Task OnInitializedAsync()
        {
            isAccessChecked = true;

            // 沒有權限的開頁稽核由伺服器在開頁紀錄時寫（shell/page-view），這裡不讀任何資料。
            if (!Permissions.IsAdmin)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                Logger.LogWarning("Backup view denied because the current user is not an administrator.");
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
            // 重新整理與刪除後都會走到這裡：清單換了，舊的勾選不再有效（0.9.132 起）。
            selectedRows = [];
            isLoading = true;
            try
            {
                var result = await Api.ListAsync();
                if (result.Success && result.Data is { } data)
                {
                    backups = data.Items;
                    job = data.Job;
                    keepCount = data.KeepCount;
                }
                else if (background)
                {
                    Logger.LogWarning("Polling backups failed. Message={Message}", result.UserMessage());
                }
                else
                {
                    ViewNotification.Error(NotificationService, "讀取備份清單時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", result.UserMessage());
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

        private async Task OnBackupNowAsync()
        {
            try
            {
                var ok = await ConfirmDialog.AskAsync(ModalService, "立即備份",
                    "要立即建立一份系統備份嗎？備份在背景進行，完成後這一頁會自動更新。", "立即備份");
                if (!ok)
                {
                    return;
                }

                var result = await Api.RunAsync();
                if (!result.Success)
                {
                    // 已在執行中（409）與 Server 版同一句；其他失敗照伺服器的訊息。
                    ViewNotification.Caution(NotificationService, result.UserMessage());
                    return;
                }

                Logger.LogInformation("Backup triggered manually from the backup page.");
                ViewNotification.Warning(NotificationService, "已開始備份。");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unhandled exception while triggering a backup.");
                ViewNotification.UnexpectedError(NotificationService, "啟動備份時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        /// <summary>下載：申請一次性票證後由瀏覽器原生下載；備份已不在（被刪除或被清掉）時顯示原因。</summary>
        private async Task OnDownloadAsync(BackupFileDto item)
        {
            try
            {
                var result = await Api.DownloadAsync(item.FileName);
                if (!result.Success)
                {
                    ViewNotification.Error(NotificationService, result.Message);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unhandled exception while downloading a backup.");
                ViewNotification.UnexpectedError(NotificationService, "下載備份時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        private async Task OnDeleteAsync(BackupFileDto item)
        {
            try
            {
                var ok = await ConfirmDialog.AskDestructiveAsync(ModalService, "刪除備份",
                    $"要刪除「{item.FileName}」嗎？刪除後無法復原。", "刪除");
                if (!ok)
                {
                    return;
                }

                var result = await DeleteOneAsync(item);
                if (!result.Success)
                {
                    ViewNotification.Caution(NotificationService, result.Message);
                    await ReloadAsync();
                    return;
                }

                ViewNotification.Warning(NotificationService, $"已刪除「{item.FileName}」。");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unhandled exception while deleting a backup.");
                ViewNotification.UnexpectedError(NotificationService, "刪除備份時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        /// <summary>刪除一份備份（伺服器記稽核）；單筆刪除與「刪除選取」共用。</summary>
        private async Task<VerifyRecordResult> DeleteOneAsync(BackupFileDto item)
        {
            var result = await Api.DeleteAsync(item.FileName);
            if (result.Success)
            {
                Logger.LogInformation("Backup deleted from the backup page. FileName={FileName}", item.FileName);
            }

            return result;
        }

        /// <summary>「刪除選取」（0.9.132 起）：逐筆走 <see cref="DeleteOneAsync"/>。</summary>
        private async Task OnBatchDeleteAsync()
        {
            try
            {
                var selected = selectedRows.ToList();
                if (selected.Count == 0 || !await BatchDelete.AskAsync(ModalService, selected.Select(x => x.FileName).ToList(), restorable: false))
                {
                    return;
                }

                batchDeleting = true;
                var outcome = await BatchDelete.RunAsync(selected, x => x.FileName, x => x.FileName, DeleteOneAsync, Logger);
                BatchDelete.Notify(NotificationService, outcome);
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unhandled exception while batch deleting backups.");
                ViewNotification.UnexpectedError(NotificationService, "刪除備份時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
            finally
            {
                batchDeleting = false;
            }
        }

        /// <summary>備份在佇列或執行中就每 5 秒重新讀一次清單；結束了就停掉（同 Server 版的計時器）。</summary>
        private void UpdateAutoRefresh()
        {
            if (IsBusy && autoRefresh is null && !disposed)
            {
                autoRefresh = new CancellationTokenSource();
                _ = AutoRefreshAsync(autoRefresh.Token);
            }
            else if (!IsBusy && autoRefresh is not null)
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
                Logger.LogDebug("Backup auto refresh stopped.");
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Backup auto refresh failed.");
            }
        }

        private static string ContentText(BackupManifestDto manifest)
        {
            var folders = string.Join("、", manifest.Folders.Select(x => $"{FolderLabel(x.SettingKey)} {x.FileCount}"));
            var missing = manifest.MissingFileCount > 0 ? $"；{manifest.MissingFileCount} 個檔案打包時已被刪除" : string.Empty;
            return $"資料庫＋{folders}{missing}";
        }

        private static string FolderLabel(string settingKey) => settingKey switch
        {
            nameof(ExternalFileSystem.ProjectFilePath) => "附件",
            nameof(ExternalFileSystem.ExceptionPath) => "例外堆疊",
            nameof(ExternalFileSystem.TokenUsagePath) => "Token 原始檔",
            nameof(ExternalFileSystem.AuditContentPath) => "稽核內容",
            nameof(ExternalFileSystem.DataProtectionKeyPath) => "金鑰",
            nameof(ExternalFileSystem.AiCallLogPath) => "AI 對話內容",
            nameof(ExternalFileSystem.AiAgentConversationPath) => "Agent 對話",
            _ => settingKey,
        };

        private static string TriggerText(string? trigger) => trigger switch
        {
            JobRunTriggers.Schedule => "排程",
            JobRunTriggers.CatchUp => "補跑",
            JobRunTriggers.Manual => "手動",
            _ => "—",
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

        private static StatusTone ToneOf(string status) => status switch
        {
            JobRunStatuses.Succeeded => StatusTone.Positive,
            JobRunStatuses.Running => StatusTone.Accent,
            JobRunStatuses.Failed or JobRunStatuses.Interrupted => StatusTone.Warning,
            _ => StatusTone.Muted,
        };

        public void Dispose()
        {
            disposed = true;
            autoRefresh?.Cancel();
            autoRefresh = null;
        }
    }
}
