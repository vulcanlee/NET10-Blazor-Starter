using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Components.Views.Teams
{
    public partial class TeamViewView
    {
        private readonly ILogger<TeamViewView> logger;
        private readonly TeamService teamService;
        private readonly ModalService modalService;
        private readonly MessageService messageService;
        private readonly NotificationService notificationService;
        ITable? table;
        int _pageIndex = 1;
        int _pageSize = MagicObjectHelper.PageSize;
        int _total = 0;
        string searchText = string.Empty;
        string sortField = string.Empty;
        string sortDirection = "None";

        List<TeamAdapterModel> teamAdapterModels = new();

        string modalTitle = "團隊維護";
        bool modalVisible = false;
        TeamAdapterModel CurrentRecord = new();
        public EditContext? LocalEditContext { get; set; }
        bool isNewRecordMode;

        /// <summary>
        /// 未儲存變更偵測。開窗時 Capture、按取消／儲存時比對，
        /// 「改了又改回原值」視為無變更，不會白問使用者一次。
        /// </summary>
        private readonly FormDirtyTracker dirtyTracker = new();
        string RoleMessage = string.Empty;
        bool isAccessChecked;

        [Inject]
        public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;
        [Inject]
        public AuthenticationStateProvider authStateProvider { get; set; } = default!;
        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        public TeamViewView(
            ILogger<TeamViewView> logger,
            TeamService teamService,
            ModalService modalService,
            MessageService messageService,
            NotificationService notificationService)
        {
            this.logger = logger;
            this.teamService = teamService;
            this.modalService = modalService;
            this.messageService = messageService;
            this.notificationService = notificationService;
        }

        protected override async Task OnInitializedAsync()
        {
            logger.LogDebug("Initializing team management view.");
            var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
            if (checkResult != AuthenticationCheckResult.Succeeded)
            {
                logger.LogWarning("Team view initialization stopped because authentication check failed.");
                return;
            }

            isAccessChecked = true;

            if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_團隊清單) == false)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                logger.LogWarning("Team view denied because current user has not this role permission.");
                return;
            }

            await ReloadAsync();
        }

        public async Task ReloadAsync()
        {
            logger.LogDebug(
                "Reloading teams. Search={Search}, SortField={SortField}, SortDirection={SortDirection}, PageIndex={PageIndex}, PageSize={PageSize}",
                searchText,
                sortField,
                sortDirection,
                _pageIndex,
                _pageSize);

            DataRequestResult<TeamAdapterModel> dataRequestResult = await teamService.GetAsync(new DataRequest
            {
                Search = searchText,
                SortField = sortField,
                SortDescending = sortDirection == "Descending" ? true : sortDirection == "Ascending" ? false : (bool?)null,
                CurrentPage = _pageIndex,
                PageSize = _pageSize,
                Take = 0,
            });

            teamAdapterModels = dataRequestResult.Result.ToList();
            _total = dataRequestResult.Count;
            logger.LogDebug("Team list reloaded successfully. Count={Count}", _total);
            StateHasChanged();
        }

        async Task OnTableChange(QueryModel<TeamAdapterModel> args)
        {
            _pageIndex = args.PageIndex;

            if (args.SortModel?.Any() == true)
            {
                var tableSortModel = TableSortHelper.GetCurrentSortModel(args.SortModel);
                string sortValue = tableSortModel.SortDirection.ToString() ?? string.Empty;
                string resolvedSortField = TableSortHelper.ResolveSortFieldName(tableSortModel);
                sortDirection = sortValue;
                sortField = resolvedSortField;
            }
            else
            {
                sortField = string.Empty;
                sortDirection = "None";
            }

            logger.LogDebug("Team table changed. PageIndex={PageIndex}, SortField={SortField}, SortDirection={SortDirection}", _pageIndex, sortField, sortDirection);
            await ReloadAsync();
        }


        async Task OnSearchAsync()
        {
            _pageIndex = 1;
            logger.LogInformation("Team search triggered. Search={Search}", searchText);
            await ReloadAsync();
        }

        async Task OnRefreshAsync()
        {
            logger.LogInformation("Team refresh triggered.");
            await ReloadAsync();

            ViewNotification.Warning(notificationService, "已更新最新資料");
        }

        async Task OnEditAsync(TeamAdapterModel teamAdapterModel)
        {
            isNewRecordMode = false;
            modalTitle = "修改團隊";
            CurrentRecord = teamAdapterModel.Clone();
            // ⚠️ 必須是開窗前的最後一步：任何預設值都要先塞完，否則會被當成使用者的變更。
            dirtyTracker.Capture(CurrentRecord);

            modalVisible = true;
            logger.LogInformation("Opened edit modal for team. TeamId={TeamId}, Name={Name}", teamAdapterModel.Id, teamAdapterModel.Name);
        }

        /// <summary>
        /// 包住實際邏輯以捕捉未預期的例外：先前這些寫入操作完全沒有 try/catch，
        /// 例外會直接拆掉 Blazor circuit，使用者只看到畫面斷線、日誌上也留不下任何痕跡。
        /// </summary>
        async Task OnDeleteAsync(TeamAdapterModel teamAdapterModel)
        {
            try
            {
                await OnDeleteCoreAsync(teamAdapterModel);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while deleting team.");
                ViewNotification.Error(notificationService, "刪除團隊時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
            }
        }

        async Task OnDeleteCoreAsync(TeamAdapterModel teamAdapterModel)
        {
            logger.LogInformation("Delete team requested. TeamId={TeamId}, Name={Name}", teamAdapterModel.Id, teamAdapterModel.Name);

            var ok = await ConfirmDialog.AskDeleteRecordAsync(modalService);

            if (!ok)
            {
                logger.LogDebug("Team delete cancelled by user. TeamId={TeamId}", teamAdapterModel.Id);
                return;
            }

            await teamService.DeleteAsync(teamAdapterModel.Id);
            logger.LogInformation("Team delete completed. TeamId={TeamId}", teamAdapterModel.Id);

            ViewNotification.Warning(notificationService, "刪除成功");

            await ReloadAsync();
        }

        async Task OnAddAsync()
        {
            CurrentRecord = new();
            isNewRecordMode = true;
            modalTitle = "新增團隊";
            // ⚠️ 必須是開窗前的最後一步：任何預設值都要先塞完，否則會被當成使用者的變更。
            dirtyTracker.Capture(CurrentRecord);

            modalVisible = true;
            logger.LogInformation("Opened create modal for team.");
        }

        /// <summary>
        /// 包住實際邏輯以捕捉未預期的例外：先前這些寫入操作完全沒有 try/catch，
        /// 例外會直接拆掉 Blazor circuit，使用者只看到畫面斷線、日誌上也留不下任何痕跡。
        /// </summary>
        /// <summary>
        /// 「儲存」按鈕。
        ///
        /// ⚠️ 第一行的 modalVisible = true 不可移動，也不可在它之前 await：
        /// AntDesign 在呼叫本方法**之前**就已送出 VisibleChanged(false)，這一行是在同一個
        /// render batch 內把它搶回來（那個 false 從來不會被畫出來，所以不會閃爍）。
        /// 之後的開關一律由 FormModalFlow 的回傳值決定 —— 失敗路徑只要 return false。
        /// ⚠️ args 可能是 null，不要解參考它。
        /// </summary>
        private async Task OnModalOKHandleAsync(MouseEventArgs args)
        {
            modalVisible = true;
            modalVisible = await FormModalFlow.RunOkAsync(
                SaveAsync,
                logger,
                notificationService,
                "儲存團隊時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
        }

        /// <summary>
        /// 實際存檔。回傳 true 表示已完成、可以關窗；
        /// 任何驗證失敗或使用者中止都 return false，不必再碰 modalVisible。
        /// </summary>
        private async Task<bool> SaveAsync()
        {
            if (LocalEditContext?.Validate() == false)
            {
                IEnumerable<string> allErrors = LocalEditContext.GetValidationMessages();
                foreach (var error in allErrors)
                {
                    logger.LogInformation("Team form validation failed. Error={Error}", error);
                    ViewNotification.ValidationError(notificationService, error);
                }

                return false;
            }

            // 一個字都沒改就按儲存：不值得白寫一筆，也不該白跳一次確認窗。
            if (isNewRecordMode == false && dirtyTracker.IsDirty(CurrentRecord) == false)
            {
                logger.LogDebug("Team save skipped because nothing changed. TeamId={TeamId}", CurrentRecord.Id);
                ViewNotification.Info(notificationService, "沒有任何變更，未進行儲存。");

                return true;
            }

            // 儲存前的二次確認。排在資料庫前置檢查之前：使用者若選「再檢查」，
            // 就不必白跑一次資料庫來回。
            if (await FormEditConfirm.AskSaveAsync(modalService) == false)
            {
                logger.LogDebug("Team save cancelled at save confirmation. TeamId={TeamId}", CurrentRecord.Id);
                return false;
            }

            if (isNewRecordMode)
            {
                var beforeAddCheckResult = await teamService.BeforeAddCheckAsync(CurrentRecord);
                if (!beforeAddCheckResult.Success)
                {
                    logger.LogInformation("Team create pre-check failed. Name={Name}, Message={Message}", CurrentRecord.Name, beforeAddCheckResult.Message);
                    ViewNotification.Error(notificationService, beforeAddCheckResult.Message);

                    return false;
                }

                CurrentRecord.CreatedAt = DateTime.Now;
                CurrentRecord.UpdatedAt = DateTime.Now;

                var actionResult = await teamService.AddAsync(CurrentRecord);
                logger.LogInformation("Team create submitted. Name={Name}", CurrentRecord.Name);

                // 前置檢查通過不代表寫得進去：唯一索引在並發時仍會擋下，
                // 忽略這個回傳值會讓失敗的儲存顯示成「新增成功」。
                if (!actionResult.Success)
                {
                    ViewNotification.Error(notificationService, actionResult.Message);

                    return false;
                }

                ViewNotification.Warning(notificationService, "新增成功");

                _ = messageService.SuccessAsync("新增成功");
            }
            else
            {
                var beforeUpdateCheckResult = await teamService.BeforeUpdateCheckAsync(CurrentRecord);
                if (!beforeUpdateCheckResult.Success)
                {
                    logger.LogInformation("Team update pre-check failed. TeamId={TeamId}, Message={Message}", CurrentRecord.Id, beforeUpdateCheckResult.Message);
                    ViewNotification.Error(notificationService, beforeUpdateCheckResult.Message);

                    return false;
                }

                CurrentRecord.UpdatedAt = DateTime.Now;
                var actionResult = await teamService.UpdateAsync(CurrentRecord);
                logger.LogInformation("Team update submitted. TeamId={TeamId}, Name={Name}", CurrentRecord.Id, CurrentRecord.Name);

                if (!actionResult.Success)
                {
                    ViewNotification.Error(notificationService, actionResult.Message);

                    return false;
                }

                ViewNotification.Warning(notificationService, "修改成功");
            }

            await ReloadAsync();
            dirtyTracker.Clear();

            return true;
        }

        /// <summary>
        /// 取消／✕／ESC 的共用出口（遮罩已由 MaskClosable="false" 擋掉，不會走到這裡）。
        /// 有未儲存變更時先問過使用者；無變更直接關閉，不打擾。
        ///
        /// ⚠️ 第一行的 modalVisible = true 不可移動：AntDesign 呼叫本方法前已送出
        /// VisibleChanged(false)，要讓「繼續編輯」留住整窗輸入就得在這裡搶回來。
        /// ⚠️ args 可能是 null（ESC／✕ 走 Config.OnCancel.Invoke(null)），不要解參考它。
        /// </summary>
        private async Task OnModalCancelHandleAsync(MouseEventArgs args)
        {
            modalVisible = true;
            modalVisible = await FormModalFlow.ConfirmCloseAsync(modalService, dirtyTracker.IsDirty(CurrentRecord));

            if (modalVisible == false)
            {
                dirtyTracker.Clear();
                logger.LogDebug("Team modal cancelled.");
            }
        }

        public void OnEditContestChanged(EditContext context)
        {
            LocalEditContext = context;
        }
    }
}
