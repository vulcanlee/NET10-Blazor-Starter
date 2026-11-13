using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using MyProject.Dtos.Admins;
using MyProject.Models.AdapterModel;
using MyProject.Models.Admins;
using MyProject.Share.Helpers;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Components.Commons;
using MyProject.Web.Components.Views.Admins;

namespace MyProject.Web.Client.Views.Admins
{
    /// <summary>
    /// 角色管理（0.10.4 起在 WASM 執行，WASM 遷移 M5）。畫面、權限矩陣與對話窗流程照 Server 版，資料改走 <see cref="RoleApi"/>（只限管理員）：
    /// 名稱規則、預設角色保護、仍是主要角色不可刪、並行衝突與稽核都在伺服器的 <c>RoleViewService</c>。
    /// </summary>
    public partial class RoleViewView
    {
        [Inject]
        public RoleApi Api { get; set; } = default!;

        [Inject]
        public PagePermissions Permissions { get; set; } = default!;

        [Inject]
        public ModalService modalService { get; set; } = default!;

        [Inject]
        public MessageService messageService { get; set; } = default!;

        [Inject]
        public NotificationService notificationService { get; set; } = default!;

        [Inject]
        public ILogger<RoleViewView> logger { get; set; } = default!;

        /// <summary>空白的權限矩陣（伺服器的 options；新增時用）。</summary>
        RolePermissionDto emptyPermission = new();
        List<string> availableTeams = new();
        ITable? table;
        /// <summary>「異動歷程」對話窗（0.9.173 起），由每列的 AuditHistoryButton 叫出。</summary>
        AuditHistoryDialog historyDialog = default!;
        int _pageIndex = 1;
        int _pageSize = MagicObjectHelper.PageSize;
        int _total = 0;
        string searchText = string.Empty;
        string sortField = string.Empty;
        string sortDirection = "None";

        List<RoleViewAdapterModel> roleViewAdapterModels = new();
        IEnumerable<RoleViewAdapterModel> selectedRows = [];
        bool batchDeleting;

        string modalTitle = "角色維護";
        bool modalVisible = false;
        RoleViewAdapterModel CurrentRecord = new();
        public EditContext? LocalEditContext { get; set; }
        bool isNewRecordMode;

        /// <summary>
        /// 未儲存變更偵測。開窗時 Capture、按取消／儲存時比對，
        /// 「改了又改回原值」視為無變更，不會白問使用者一次。
        /// </summary>
        private readonly FormDirtyTracker dirtyTracker = new();
        string RoleMessage = string.Empty;
        bool isAccessChecked;


        /// <summary>「資料保留與清理」頁的「前往」帶 <c>?deleted=1</c> 進來時為 true：一進頁面就顯示已刪除的資料（0.9.133 起）。</summary>
        [Parameter]
        public bool InitialShowDeleted { get; set; }

        protected override async Task OnInitializedAsync()
        {
            logger.LogDebug("Initializing role view management.");

            // 登入檢查已由版面（ClientMainLayout）完成；沒有權限的開頁稽核由伺服器在開頁紀錄時寫（shell/page-view）。
            if (!Permissions.IsAdmin)
            {
                isAccessChecked = true;
                RoleMessage = "你沒有權限存取此頁面";
                logger.LogWarning("Role view denied because current user is not an administrator.");
                return;
            }

            var options = await Api.OptionsAsync();
            if (options.Success && options.Data is { } data)
            {
                availableTeams = data.Teams;
                emptyPermission = data.EmptyPermission;
            }
            else
            {
                ViewNotification.Error(notificationService, $"讀取團隊與權限選項失敗：{options.UserMessage()}");
            }

            isAccessChecked = true;

            showDeleted = InitialShowDeleted;
            await ReloadAsync();
        }

        /// <summary>重新讀取；回傳是否成功（失敗時已顯示通知）。</summary>
        public async Task<bool> ReloadAsync(bool recordSearch = false)
        {
            // 換頁、排序、搜尋、切換與刪除後都會走到這裡：勾選只在目前這一頁有效（0.9.132 起）。
            selectedRows = [];
            logger.LogDebug(
                "Reloading role views. HasSearch={HasSearch}, SearchLength={SearchLength}, SortField={SortField}, SortDirection={SortDirection}, PageIndex={PageIndex}, PageSize={PageSize}",
                string.IsNullOrWhiteSpace(searchText) == false,
                searchText.Length,
                sortField,
                sortDirection,
                _pageIndex,
                _pageSize);

            // 「顯示已刪除」開啟時改讀已刪除的資料（0.9.95 起）；搜尋稽核由伺服器寫（只有按「搜尋」才帶 RecordSearch）。
            var result = await Api.SearchAsync(new RoleSearchRequestDto
            {
                Keyword = searchText,
                SortBy = sortDirection is "Ascending" or "Descending" ? sortField : null,
                SortDescending = sortDirection == "Descending",
                PageIndex = _pageIndex,
                PageSize = _pageSize,
                Deleted = showDeleted,
                RecordSearch = recordSearch,
            });
            if (!result.Success || result.Data is null)
            {
                ViewNotification.Error(notificationService, result.UserMessage());
                return false;
            }

            roleViewAdapterModels = result.Data.Items.Select(RoleDtoMapper.ToModel).ToList();
            _total = result.Data.TotalCount;
            logger.LogDebug("Role view list reloaded successfully. Count={Count}", _total);
            StateHasChanged();
            return true;
        }

        async Task OnTableChange(QueryModel<RoleViewAdapterModel> args)
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

            logger.LogDebug("Role view table changed. PageIndex={PageIndex}, SortField={SortField}, SortDirection={SortDirection}", _pageIndex, sortField, sortDirection);
            await ReloadAsync();
        }


        async Task OnSearchAsync()
        {
            _pageIndex = 1;
            logger.LogInformation("Role view search triggered. HasSearch={HasSearch}, SearchLength={SearchLength}", string.IsNullOrWhiteSpace(searchText) == false, searchText.Length);
            await ReloadAsync(recordSearch: true);
        }

        async Task OnRefreshAsync()
        {
            logger.LogInformation("Role view refresh triggered.");
            if (await ReloadAsync())
            {
                ViewNotification.Warning(notificationService, "已更新最新資料");
            }
        }

        /// <summary>開修改窗：向伺服器讀這一筆的最新矩陣與版本號（伺服器寫「查看」稽核）。</summary>
        async Task OnEditAsync(RoleViewAdapterModel roleViewAdapterModel)
        {
            var result = await Api.GetAsync(roleViewAdapterModel.Id);
            if (!result.Success || result.Data is null)
            {
                ViewNotification.Error(notificationService, result.UserMessage());
                await ReloadAsync();
                return;
            }

            isNewRecordMode = false;
            modalTitle = "修改角色";
            CurrentRecord = RoleDtoMapper.ToModel(result.Data);
            InitExpandedGroups();
            // ⚠️ 必須是開窗前的最後一步：任何預設值都要先塞完，否則會被當成使用者的變更。
            dirtyTracker.Capture(CurrentRecord);

            modalVisible = true;
            logger.LogInformation("Opened edit modal for role view. RoleViewId={RoleViewId}", roleViewAdapterModel.Id);
        }

        /// <summary>
        /// 包住實際邏輯以捕捉未預期的例外：先前這些寫入操作完全沒有 try/catch，
        /// 例外會直接拆掉 Blazor circuit，使用者只看到畫面斷線、日誌上也留不下任何痕跡。
        /// </summary>
        async Task OnDeleteAsync(RoleViewAdapterModel roleViewAdapterModel)
        {
            try
            {
                await OnDeleteCoreAsync(roleViewAdapterModel);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while deleting role.");
                ViewNotification.UnexpectedError(notificationService, "刪除角色時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        async Task OnDeleteCoreAsync(RoleViewAdapterModel roleViewAdapterModel)
        {
            logger.LogInformation("Delete role view requested. RoleViewId={RoleViewId}, Name={RoleName}", roleViewAdapterModel.Id, roleViewAdapterModel.Name);

            var ok = await ConfirmDialog.AskSoftDeleteRecordAsync(modalService);

            if (!ok)
            {
                logger.LogDebug("Role view delete cancelled by user. RoleViewId={RoleViewId}", roleViewAdapterModel.Id);
                return;
            }

            var result = await Api.DeleteAsync(roleViewAdapterModel.Id);
            if (!result.Success)
            {
                // 0.9.93 之前這裡不看結果，失敗也顯示「刪除成功」（例如角色仍是某些使用者的主要角色時根本刪不掉）。
                logger.LogInformation("Role view delete rejected. RoleViewId={RoleViewId}, Message={Message}", roleViewAdapterModel.Id, result.Message);
                ViewNotification.Error(notificationService, result.Message);
                return;
            }

            logger.LogInformation("Role view delete completed. RoleViewId={RoleViewId}", roleViewAdapterModel.Id);

            ViewNotification.Warning(notificationService, "刪除成功");

            await ReloadAsync();
        }

        /// <summary>「刪除選取」（0.9.132 起）：逐筆呼叫刪除 API，規則與稽核同單筆刪除。</summary>
        async Task OnBatchDeleteAsync()
        {
            try
            {
                var items = selectedRows.ToList();
                if (items.Count == 0 || !await BatchDelete.AskAsync(modalService, items.Select(x => x.Name).ToList(), restorable: true))
                {
                    return;
                }

                batchDeleting = true;
                var outcome = await BatchDelete.RunAsync(items, x => x.Id.ToString(), x => x.Name,
                    x => Api.DeleteAsync(x.Id), logger);
                BatchDelete.Notify(notificationService, outcome);
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while batch deleting roles.");
                ViewNotification.UnexpectedError(notificationService, "刪除角色時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
            finally
            {
                batchDeleting = false;
            }
        }

        bool showDeleted;

        async Task OnToggleDeletedAsync()
        {
            showDeleted = !showDeleted;
            _pageIndex = 1;
            await ReloadAsync();
        }

        async Task OnRestoreAsync(RoleViewAdapterModel record)
        {
            try
            {
                var ok = await ConfirmDialog.AskAsync(modalService, "確認還原", $"要還原「{record.Name}」嗎？", "還原");
                if (!ok)
                {
                    return;
                }

                var result = await Api.RestoreAsync(record.Id);
                if (!result.Success)
                {
                    logger.LogInformation("Role view restore rejected. RoleViewId={RoleViewId}, Message={Message}", record.Id, result.Message);
                    ViewNotification.Error(notificationService, result.Message);
                    return;
                }

                logger.LogInformation("Role view restore completed. RoleViewId={RoleViewId}", record.Id);
                ViewNotification.Warning(notificationService, "還原成功");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while restoring role view.");
                ViewNotification.UnexpectedError(notificationService, "還原角色時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        async Task OnPurgeAsync(RoleViewAdapterModel record)
        {
            try
            {
                var ok = await ConfirmDialog.AskDestructiveAsync(
                    modalService,
                    "永久刪除",
                    $"永久刪除「{record.Name}」後無法復原，它的權限設定與「額外角色」關聯也會一併刪除。確定要永久刪除嗎？",
                    "永久刪除");
                if (!ok)
                {
                    return;
                }

                var result = await Api.PurgeAsync(record.Id);
                if (!result.Success)
                {
                    logger.LogInformation("Role view purge rejected. RoleViewId={RoleViewId}, Message={Message}", record.Id, result.Message);
                    ViewNotification.Error(notificationService, result.Message);
                    return;
                }

                logger.LogInformation("Role view purge completed. RoleViewId={RoleViewId}", record.Id);
                ViewNotification.Warning(notificationService, "已永久刪除");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while purging role view.");
                ViewNotification.UnexpectedError(notificationService, "永久刪除角色時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        Task OnAddAsync(bool continueOnCapturedContext)
        {
            CurrentRecord = new();
            CurrentRecord.RolePermission = RoleDtoMapper.ToModel(emptyPermission);

            isNewRecordMode = true;
            modalTitle = "新增角色";
            InitExpandedGroups();
            // ⚠️ 必須是開窗前的最後一步：任何預設值都要先塞完，否則會被當成使用者的變更。
            dirtyTracker.Capture(CurrentRecord);

            modalVisible = true;
            logger.LogInformation("Opened create modal for role view.");
            return Task.CompletedTask;
        }

        /// <summary>
        /// 清單的「複製」（0.9.139 起）：以這一列為範本開新增視窗，名稱、預設團隊、兩步驟驗證、權限都帶入且可修改，
        /// 存檔走一般新增流程（確認儲存、名稱重複檢查、Role.Create 稽核）。
        /// </summary>
        void OnCopy(RoleViewAdapterModel source)
        {
            CurrentRecord = RolePermissionMatrix.CreateCopy(source);

            isNewRecordMode = true;
            modalTitle = $"複製角色（來源：{source.Name}）";
            InitExpandedGroups();
            // ⚠️ 必須是開窗前的最後一步：任何預設值都要先塞完，否則會被當成使用者的變更。
            dirtyTracker.Capture(CurrentRecord);

            modalVisible = true;
            logger.LogInformation("Opened copy modal for role view. SourceRoleViewId={RoleViewId}, SourceName={RoleName}", source.Id, source.Name);
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
                "儲存角色時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
        }

        /// <summary>
        /// 實際存檔。回傳 true 表示已完成、可以關窗；
        /// 任何驗證失敗或使用者中止都 return false，不必再碰 modalVisible。
        /// </summary>
        private async Task<bool> SaveAsync()
        {
            if (LocalEditContext is not null && await LocalEditContext.ValidateAsync() == false) // .NET 11：Validate() 已過時，ValidateAsync 也會跑非同步驗證
            {
                IEnumerable<string> allErrors = LocalEditContext.GetValidationMessages();
                foreach (var error in allErrors)
                {
                    logger.LogInformation("Role view form validation failed. Error={Error}", error);
                    ViewNotification.ValidationError(notificationService, error);
                }

                return false;
            }

            // 一個字都沒改就按儲存：不值得白寫一筆，也不該白跳一次確認窗。
            if (isNewRecordMode == false && dirtyTracker.IsDirty(CurrentRecord) == false)
            {
                logger.LogDebug("Role save skipped because nothing changed. RoleViewId={RoleViewId}", CurrentRecord.Id);
                ViewNotification.Info(notificationService, "沒有任何變更，未進行儲存。");

                return true;
            }

            // 儲存前的二次確認。排在資料庫前置檢查之前：使用者若選「再檢查」，
            // 就不必白跑一次資料庫來回。
            if (await FormEditConfirm.AskSaveAsync(modalService) == false)
            {
                logger.LogDebug("Role save cancelled at save confirmation. RoleViewId={RoleViewId}", CurrentRecord.Id);
                return false;
            }

            // 名稱重複、預設角色不可改名等規則由伺服器在存檔前檢查（任何一項不過就什麼都不存），訊息與 Server 版相同。
            if (isNewRecordMode)
            {
                var actionResult = await Api.CreateAsync(RoleDtoMapper.ToDto(CurrentRecord));
                if (!actionResult.Success)
                {
                    // 0.9.92 之前這裡不看結果，失敗也顯示「新增成功」。
                    logger.LogInformation("Role view create rejected. Name={RoleName}, Message={Message}", CurrentRecord.Name, actionResult.Message);
                    ViewNotification.Error(notificationService, actionResult.Message);
                    return false;
                }

                logger.LogInformation("Role view create submitted. Name={RoleName}", CurrentRecord.Name);

                ViewNotification.Warning(notificationService, "新增成功");

                _ = messageService.SuccessAsync("新增成功");
            }
            else
            {
                var actionResult = await Api.UpdateAsync(RoleDtoMapper.ToDto(CurrentRecord));
                if (!actionResult.Success)
                {
                    // 0.9.92 之前這裡不看結果，失敗也顯示「修改成功」；0.9.93 起的並行衝突訊息也走這裡，表單維持開啟。
                    logger.LogInformation("Role view update rejected. RoleViewId={RoleViewId}, Message={Message}", CurrentRecord.Id, actionResult.Message);
                    ViewNotification.Error(notificationService, actionResult.Message);
                    return false;
                }

                logger.LogInformation("Role view update submitted. RoleViewId={RoleViewId}, Name={RoleName}", CurrentRecord.Id, CurrentRecord.Name);

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

            if (modalVisible)
            {
                return;
            }

            dirtyTracker.Clear();
            logger.LogDebug("Role view modal cancelled.");
        }

        public void OnEditContestChanged(EditContext context)
        {
            LocalEditContext = context;
        }

        private void OnRoleTeamsChanged(IEnumerable<string> values)
        {
            CurrentRecord.DefaultTeams = values?.ToList() ?? [];
        }

        /// <summary>
        /// 展開中的權限群組。開窗時有勾選的群組展開、完全沒勾的收合；只是畫面狀態，不屬於表單資料。
        /// </summary>
        private readonly HashSet<RolePermissionGroup> expandedGroups = [];

        private void InitExpandedGroups()
        {
            expandedGroups.Clear();
            expandedGroups.UnionWith(CurrentRecord.RolePermission.Groups.Where(RolePermissionMatrix.HasAnyGrant));
        }

        private void ToggleGroup(RolePermissionGroup group)
        {
            if (!expandedGroups.Remove(group))
            {
                expandedGroups.Add(group);
            }
        }

        private void OnPermissionGroupChanged(RolePermissionGroup group, bool value)
        {
            group.Enable = value;

            if (value)
            {
                // 勾了群組通常接著要勾底下的項目，順手展開。
                expandedGroups.Add(group);
                return;
            }

            foreach (var permission in group.Permissions)
            {
                permission.Enable = false;
            }
        }

        private void OnPermissionItemChanged(RolePermissionGroup group, RolePermissionNode role, bool value)
        {
            role.Enable = value;

            if (value)
            {
                group.Enable = true;
            }
        }

        /// <summary>權限矩陣的動作欄（代碼 → 顯示名）。</summary>
        private static readonly (string Key, string Label)[] PermissionActionItems =
        [
            (PermissionActions.View, "檢視"),
            (PermissionActions.Create, "新增"),
            (PermissionActions.Edit, "編輯"),
            (PermissionActions.Delete, "刪除"),
        ];

        private void OnPermissionActionChanged(RolePermissionGroup group, RolePermissionNode node, string action, bool value)
        {
            node.Actions ??= new Dictionary<string, bool>();
            node.Actions[action] = value;

            if (value)
            {
                group.Enable = true;
            }
        }
    }
}
