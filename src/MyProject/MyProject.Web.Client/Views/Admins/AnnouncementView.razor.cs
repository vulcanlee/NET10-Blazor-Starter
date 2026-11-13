using System.Diagnostics;
using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas.Models;
using MyProject.Dtos.Admins;
using MyProject.Models.AdapterModel;
using MyProject.Share.Helpers;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Client.Layout;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Client.Views.Admins
{
    /// <summary>
    /// 公告管理（0.9.100 起；0.10.6 起在 WASM 執行，WASM 遷移 M7）：新增、修改、刪除公告。管理員專屬：權限鍵刻意不進角色矩陣，以 IsAdmin 判斷。
    /// 資料改走 <see cref="AnnouncementApi"/>：驗證、稽核、公告快取失效與通知都在伺服器。
    /// 時間都是伺服器的本地時間；「現在」（狀態與新增時的預設開始時間）用伺服器的時鐘，不用瀏覽器的。
    /// </summary>
    public partial class AnnouncementView
    {
        [Inject]
        public AnnouncementApi Api { get; set; } = default!;

        /// <summary>存檔或刪除後請外框的公告橫幅立即重讀（0.10.6 起；Server 版是即時的）。</summary>
        [Inject]
        public ClientAnnouncementSignal AnnouncementSignal { get; set; } = default!;

        [Inject]
        public PagePermissions Permissions { get; set; } = default!;

        [Inject]
        public ModalService modalService { get; set; } = default!;

        [Inject]
        public NotificationService notificationService { get; set; } = default!;

        [Inject]
        public ILogger<AnnouncementView> logger { get; set; } = default!;

        private List<AnnouncementAdapterModel> items = [];
        private IEnumerable<AnnouncementAdapterModel> selectedRows = [];
        private bool batchDeleting;
        private Table<AnnouncementAdapterModel>? table;
        private List<AnnouncementTargetOptionDto> roleOptions = [];
        private List<AnnouncementTargetOptionDto> teamOptions = [];
        private bool isLoading;
        private string RoleMessage = string.Empty;
        private bool isAccessChecked;

        /// <summary>上次讀取時伺服器的「現在」（本地時間）與讀到的時刻；之後的「現在」＝它加上經過的時間。</summary>
        private DateTime serverNowAtLoad;
        private long loadedTimestamp = Stopwatch.GetTimestamp();

        private bool modalVisible;
        private AnnouncementAdapterModel editing = new();
        private string openedSnapshot = string.Empty;
        private string errorMessage = string.Empty;

        protected override async Task OnInitializedAsync()
        {
            isAccessChecked = true;

            // 沒有權限的開頁稽核由伺服器在開頁紀錄時寫（shell/page-view）；權限未通過前不呼叫任何 API。
            if (!Permissions.IsAdmin)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                logger.LogWarning("Announcement view denied because the current user is not an administrator.");
                return;
            }

            await ReloadAsync();
        }

        /// <summary>重新讀取；回傳是否成功（失敗時已顯示通知）。</summary>
        private async Task<bool> ReloadAsync()
        {
            // 重新整理與刪除後都會走到這裡：勾選只在目前這一頁有效（0.9.132 起）。
            selectedRows = [];
            isLoading = true;
            try
            {
                var result = await Api.GetAllAsync();
                if (result.Success && result.Data is { } data)
                {
                    items = data.Items.Select(AnnouncementDtoMapper.ToModel).ToList();
                    roleOptions = data.Roles;
                    teamOptions = data.Teams;
                    serverNowAtLoad = data.ServerNow;
                    loadedTimestamp = Stopwatch.GetTimestamp();
                    return true;
                }

                logger.LogWarning("Failed to load announcements. Message={Message}", result.UserMessage());
                ViewNotification.Error(notificationService, $"讀取公告失敗：{result.UserMessage()}");
                return false;
            }
            finally
            {
                isLoading = false;
            }
        }

        private async Task OnRefreshAsync()
        {
            if (await ReloadAsync())
            {
                ViewNotification.Info(notificationService, "已更新最新資料");
            }
        }

        /// <summary>伺服器的「現在」（本地時間）。</summary>
        private DateTime ServerNow() => serverNowAtLoad + Stopwatch.GetElapsedTime(loadedTimestamp);

        /// <summary>
        /// 對象的角色、團隊選項（0.10.15 起：開窗、存檔失敗後重讀 —— 別人可能剛新增、刪除了角色或停用了團隊；
        /// 速查表「畫面選項資料一致性」，<c>FormOptionsConventionTests</c> 守門）。選項與清單同一個端點，這裡只換選項，不動清單。
        /// </summary>
        private async Task<bool> LoadFormOptionsAsync()
        {
            var result = await Api.GetAllAsync();
            if (result.Success && result.Data is { } data)
            {
                roleOptions = data.Roles;
                teamOptions = data.Teams;
                return true;
            }

            ViewNotification.Error(notificationService, $"讀取角色與團隊選項失敗：{result.UserMessage()}");
            return false;
        }

        private async Task OnAddAsync()
        {
            await LoadFormOptionsAsync();
            var now = ServerNow();
            Open(new AnnouncementAdapterModel
            {
                StartAt = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0),
                TargetKind = AnnouncementTargetKinds.All,
            });
        }

        private async Task OnEditAsync(AnnouncementAdapterModel item)
        {
            await LoadFormOptionsAsync();
            Open(new AnnouncementAdapterModel
            {
                Id = item.Id,
                Title = item.Title,
                Content = item.Content,
                StartAt = item.StartAt,
                EndAt = item.EndAt,
                TargetKind = item.TargetKind,
                TargetId = item.TargetId,
                ConcurrencyStamp = item.ConcurrencyStamp,
            });
        }

        private void Open(AnnouncementAdapterModel model)
        {
            editing = model;
            openedSnapshot = Snapshot(model);
            errorMessage = string.Empty;
            modalVisible = true;
        }

        private async Task OnModalOkAsync()
        {
            // 第一行先把 Visible 搶回來（見 FormModalFlow）。
            modalVisible = true;
            modalVisible = await FormModalFlow.RunOkAsync(SaveAsync, logger, notificationService, "儲存公告時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
        }

        private async Task OnModalCancelAsync()
        {
            modalVisible = true;
            modalVisible = await FormModalFlow.ConfirmCloseAsync(modalService, Snapshot(editing) != openedSnapshot);
        }

        /// <summary>存檔（建立者由伺服器取目前登入的帳號；伺服器寫 <c>Announcement.Create</c>／<c>Update</c> 稽核）。</summary>
        private async Task<bool> SaveAsync()
        {
            errorMessage = string.Empty;
            var isNew = editing.Id == 0;
            var result = isNew
                ? await Api.AddAsync(editing)
                : await Api.UpdateAsync(editing);
            if (!result.Success)
            {
                errorMessage = result.Message;
                await LoadFormOptionsAsync(); // 失敗可能是選的角色或團隊剛被刪除、停用：讓下拉換成最新的選項
                return false;
            }

            ViewNotification.Warning(notificationService, isNew ? "已新增公告。" : "已更新公告。");
            await ReloadAsync();
            AnnouncementSignal.RequestRefresh();
            return true;
        }

        private async Task OnDeleteAsync(AnnouncementAdapterModel item)
        {
            try
            {
                var ok = await ConfirmDialog.AskDestructiveAsync(modalService, "刪除公告", $"要刪除「{item.Title}」嗎？刪除後立即不再顯示，無法復原。", "刪除");
                if (!ok)
                {
                    return;
                }

                var result = await Api.DeleteAsync(item.Id, item.ConcurrencyStamp);
                if (!result.Success)
                {
                    ViewNotification.Caution(notificationService, result.Message);
                    await ReloadAsync();
                    return;
                }

                ViewNotification.Warning(notificationService, $"已刪除「{item.Title}」。");
                await ReloadAsync();
                AnnouncementSignal.RequestRefresh();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while deleting an announcement.");
                ViewNotification.UnexpectedError(notificationService, "刪除公告時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        /// <summary>
        /// 本頁在畫面上分頁（DataSource 不變），換頁時不會經過 <see cref="ReloadAsync"/>，要自己清掉勾選。
        /// </summary>
        private void OnPageIndexChange(PaginationEventArgs args) => table?.SetSelection(Array.Empty<AnnouncementAdapterModel>());

        /// <summary>
        /// 「刪除選取」（0.9.132 起）：逐筆呼叫單筆刪除（帶各自的版本戳記，每筆一列稽核由伺服器寫）。
        /// </summary>
        private async Task OnBatchDeleteAsync()
        {
            try
            {
                var selected = selectedRows.ToList();
                if (selected.Count == 0 || !await BatchDelete.AskAsync(modalService, selected.Select(x => x.Title).ToList(), restorable: false,
                        note: "刪除後立即不再顯示。"))
                {
                    return;
                }

                batchDeleting = true;
                var outcome = await BatchDelete.RunAsync(selected, x => x.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), x => x.Title,
                    item => Api.DeleteAsync(item.Id, item.ConcurrencyStamp), logger);

                BatchDelete.Notify(notificationService, outcome);
                await ReloadAsync();
                AnnouncementSignal.RequestRefresh();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while batch deleting announcements.");
                ViewNotification.UnexpectedError(notificationService, "刪除公告時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
            finally
            {
                batchDeleting = false;
            }
        }

        private string StatusText(AnnouncementAdapterModel item) => StateOf(item) switch
        {
            0 => "未開始",
            1 => "進行中",
            _ => "已結束",
        };

        private static StatusTone ToneOf(int state) => state switch
        {
            0 => StatusTone.Neutral,
            1 => StatusTone.Positive,
            _ => StatusTone.Muted,
        };

        private int StateOf(AnnouncementAdapterModel item)
        {
            var now = ServerNow();
            return now < item.StartAt ? 0 : item.EndAt is { } end && now >= end ? 2 : 1;
        }

        private static string Snapshot(AnnouncementAdapterModel x)
            => string.Join('|', x.Title, x.Content, x.StartAt.ToString("O"), x.EndAt?.ToString("O"), x.TargetKind, x.TargetId);
    }
}
