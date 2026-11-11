using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MyProject.Dtos.Commons;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Client.Views.Categories
{
    /// <summary>
    /// 分類清單（0.10.2 起在 WASM 執行，WASM 遷移 M3）。畫面、對話窗流程與提示訊息照 Server 版，資料改走 <see cref="CategoryApi"/>：
    /// 團隊範圍、名稱規則、並行衝突與稽核都在伺服器的 <c>CategoryService</c>，這裡只負責畫面。
    /// </summary>
    public partial class CategoryViewView
    {
        [Inject]
        public IJSRuntime JSRuntime { get; set; } = default!;

        [Inject]
        public CategoryApi Api { get; set; } = default!;

        [Inject]
        public PagePermissions Permissions { get; set; } = default!;

        [Inject]
        public ModalService ModalService { get; set; } = default!;

        [Inject]
        public MessageService MessageService { get; set; } = default!;

        [Inject]
        public NotificationService NotificationService { get; set; } = default!;

        [Inject]
        public ILogger<CategoryViewView> Logger { get; set; } = default!;

        /// <summary>匯出 Excel：與清單同一個查詢（伺服器產生檔案並寫稽核），超過上限筆數時顯示原因。</summary>
        private async Task OnExportExcelAsync()
        {
            var outcome = await Api.ExportAsync(BuildRequest());
            if (!outcome.Success)
            {
                if (outcome.StatusCode == 400)
                {
                    ViewNotification.Caution(NotificationService, outcome.Message);
                }
                else
                {
                    ViewNotification.Error(NotificationService, $"匯出失敗：{outcome.Message}");
                }

                return;
            }

            try
            {
                await JSRuntime.DownloadFileAsync(outcome.FileName!, outcome.Content!, outcome.ContentType!);
                Logger.LogInformation("Category export downloaded. Bytes={Bytes}", outcome.Content!.Length);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Category export failed.");
                ViewNotification.UnexpectedError(NotificationService, $"匯出失敗：{ex.GetType().Name}。", ex);
            }
        }

        ITable? table;
        /// <summary>「異動歷程」對話窗（0.9.173 起），由每列的 AuditHistoryButton 叫出。</summary>
        AuditHistoryDialog historyDialog = default!;
        int _pageIndex = 1;
        int _pageSize = MagicObjectHelper.PageSize;
        int _total = 0;
        string searchText = string.Empty;
        string sortField = string.Empty;
        string sortDirection = "None";

        List<CategoryAdapterModel> categoryAdapterModels = new();
        IEnumerable<CategoryAdapterModel> selectedRows = [];
        bool batchDeleting;
        List<string> availableTeams = new();

        /// <summary>
        /// 表單「適用團隊」的選項（0.9.105 起）：管理員是全部；其他人只有自己範圍內的團隊（含下屬部門），
        /// 加上這筆原本就有的。伺服器端 <c>RecordTeamScope.CheckAssignment</c> 會再檢查一次。
        /// </summary>
        IEnumerable<string> AssignableTeams => isAdmin
            ? availableTeams
            : availableTeams.Where(t => myTeams.Contains(t, StringComparer.OrdinalIgnoreCase) || CurrentRecord.Teams.Contains(t, StringComparer.OrdinalIgnoreCase));

        /// <summary>目前使用者的團隊範圍（伺服器的 <c>options</c>），用於判斷「存檔後自己就看不到這筆」的提醒。</summary>
        bool isAdmin;
        List<string> myTeams = [];

        string modalTitle = "分類維護";
        bool modalVisible = false;
        CategoryAdapterModel CurrentRecord = new();
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
            // 登入檢查已由版面（ClientMainLayout）完成；沒有權限的開頁稽核由伺服器在開頁紀錄時寫（shell/page-view）。
            if (Permissions.CanAccessPage(MagicObjectHelper.角色_分類清單) == false)
            {
                isAccessChecked = true;
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                Logger.LogWarning("Category view denied because current user has not this role permission.");
                return;
            }

            var options = await Api.OptionsAsync();
            if (options.Success && options.Data is { } data)
            {
                availableTeams = data.Teams;
                isAdmin = data.IsAdmin;
                myTeams = data.MyTeams;
            }
            else
            {
                // 讀不到選項時「適用團隊」會是空的、團隊提醒也不準：告訴使用者，而不是默默少了選項。
                ViewNotification.Error(NotificationService, $"讀取「適用團隊」選項失敗：{options.UserMessage()}");
            }

            // 只有看得到「顯示已刪除」按鈕的人（刪除權限）才套用。
            canDelete = Permissions.CanAccessAction(MagicObjectHelper.角色_分類清單, PermissionActions.Delete);
            showDeleted = InitialShowDeleted && canDelete;

            // ⚠️ 勾選欄是否出現（canDelete）要在表格第一次畫出來之前定案：WASM 版等 options 回來時會先畫一次，
            // 表格畫好後才加上 <Selection />，AntDesign 之後釋放它時會丟 NullReferenceException（整頁出現錯誤列）。
            isAccessChecked = true;
            await ReloadAsync();
        }

        /// <summary>目前的搜尋、排序、分頁與「顯示已刪除」（清單與匯出共用）。</summary>
        CategorySearchRequestDto BuildRequest(bool recordSearch = false) => new()
        {
            Keyword = searchText,
            SortBy = sortDirection is "Ascending" or "Descending" ? sortField : null,
            SortDescending = sortDirection == "Descending",
            PageIndex = _pageIndex,
            PageSize = _pageSize,
            Deleted = showDeleted,
            RecordSearch = recordSearch,
        };

        public async Task ReloadAsync(bool recordSearch = false)
        {
            // 換頁、排序、搜尋、切換與刪除後都會走到這裡：勾選只在目前這一頁有效（0.9.132 起）。
            selectedRows = [];
            Logger.LogDebug(
                "Reloading categories. HasSearch={HasSearch}, SortField={SortField}, SortDirection={SortDirection}, PageIndex={PageIndex}, PageSize={PageSize}",
                string.IsNullOrWhiteSpace(searchText) == false, sortField, sortDirection, _pageIndex, _pageSize);

            var result = await Api.SearchAsync(BuildRequest(recordSearch));
            if (!result.Success || result.Data is null)
            {
                ViewNotification.Error(NotificationService, result.UserMessage());
                return;
            }

            categoryAdapterModels = result.Data.Items.Select(CategoryDtoMapper.ToModel).ToList();
            _total = result.Data.TotalCount;
            Logger.LogDebug("Category list reloaded successfully. Count={Count}", _total);
            StateHasChanged();
        }

        async Task OnTableChange(QueryModel<CategoryAdapterModel> args)
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

            Logger.LogDebug("Category table changed. PageIndex={PageIndex}, SortField={SortField}, SortDirection={SortDirection}", _pageIndex, sortField, sortDirection);
            await ReloadAsync();
        }


        async Task OnSearchAsync()
        {
            _pageIndex = 1;
            Logger.LogInformation("Category search triggered. HasSearch={HasSearch}", string.IsNullOrWhiteSpace(searchText) == false);
            // 搜尋稽核由伺服器寫（只有按「搜尋」才帶 RecordSearch）。
            await ReloadAsync(recordSearch: true);
        }

        async Task OnRefreshAsync()
        {
            Logger.LogInformation("Category refresh triggered.");
            await ReloadAsync();

            ViewNotification.Warning(NotificationService, "已更新最新資料");
        }

        /// <summary>
        /// 一律指派新清單：CurrentRecord 是 MemberwiseClone 出來的，
        /// 直接異動原清單會連帶改到表格上那一列的資料。
        /// </summary>
        void OnRecordTeamsChanged(IEnumerable<string> values)
        {
            CurrentRecord.Teams = values?.ToList() ?? new List<string>();
        }

        /// <summary>開修改窗：向伺服器讀這一筆的最新資料與版本號（伺服器寫「查看」稽核）。</summary>
        async Task OnEditAsync(CategoryAdapterModel categoryAdapterModel)
        {
            var result = await Api.GetAsync(categoryAdapterModel.Id);
            if (!result.Success || result.Data is null)
            {
                ViewNotification.Error(NotificationService, result.UserMessage());
                await ReloadAsync();
                return;
            }

            isNewRecordMode = false;
            modalTitle = "修改分類";
            CurrentRecord = CategoryDtoMapper.ToModel(result.Data);
            // ⚠️ 必須是開窗前的最後一步：任何預設值都要先塞完，否則會被當成使用者的變更。
            dirtyTracker.Capture(CurrentRecord);

            modalVisible = true;
            Logger.LogInformation("Opened edit modal for category. CategoryId={CategoryId}", categoryAdapterModel.Id);
        }

        /// <summary>
        /// 點狀態標籤切換啟用／停用（0.9.120 起）：停用先確認，啟用直接切換；只改狀態，不經修改窗。
        /// </summary>
        async Task OnToggleEnabledAsync(CategoryAdapterModel record)
        {
            try
            {
                var enable = !record.IsEnabled;
                if (!enable && !await ConfirmDialog.AskAsync(ModalService, "停用分類",
                        $"要停用「{record.Name}」嗎？停用後它不會出現在下拉選單，已使用這個分類的資料不受影響。", "停用"))
                {
                    return;
                }

                var result = await Api.SetEnabledAsync(record.Id, enable);
                if (!result.Success)
                {
                    Logger.LogInformation("Category status change rejected. CategoryId={CategoryId}, Message={Message}", record.Id, result.Message);
                    ViewNotification.Error(NotificationService, result.Message);
                }
                else
                {
                    ViewNotification.Warning(NotificationService, $"已{(enable ? "啟用" : "停用")}「{record.Name}」");
                }

                await ReloadAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unhandled exception while changing category status.");
                ViewNotification.UnexpectedError(NotificationService, "切換分類狀態時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        /// <summary>包住實際邏輯以捕捉未預期的例外：WASM 頁面的例外會讓整頁換成錯誤畫面，這裡改成右下角提示。</summary>
        async Task OnDeleteAsync(CategoryAdapterModel categoryAdapterModel)
        {
            try
            {
                await OnDeleteCoreAsync(categoryAdapterModel);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unhandled exception while deleting category.");
                ViewNotification.UnexpectedError(NotificationService, "刪除分類時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        async Task OnDeleteCoreAsync(CategoryAdapterModel categoryAdapterModel)
        {
            Logger.LogInformation("Delete category requested. CategoryId={CategoryId}", categoryAdapterModel.Id);

            var ok = await ConfirmDialog.AskSoftDeleteRecordAsync(ModalService);

            if (!ok)
            {
                Logger.LogDebug("Category delete cancelled by user. CategoryId={CategoryId}", categoryAdapterModel.Id);
                return;
            }

            var result = await Api.DeleteAsync(categoryAdapterModel.Id);
            if (!result.Success)
            {
                Logger.LogInformation("Category delete rejected. CategoryId={CategoryId}, Message={Message}", categoryAdapterModel.Id, result.Message);
                ViewNotification.Error(NotificationService, result.Message);
                return;
            }

            Logger.LogInformation("Category delete completed. CategoryId={CategoryId}", categoryAdapterModel.Id);

            ViewNotification.Warning(NotificationService, "刪除成功");

            await ReloadAsync();
        }

        /// <summary>「刪除選取」（0.9.132 起）：逐筆呼叫刪除 API，規則與稽核同單筆刪除。</summary>
        async Task OnBatchDeleteAsync()
        {
            try
            {
                var items = selectedRows.ToList();
                if (items.Count == 0 || !await BatchDelete.AskAsync(ModalService, items.Select(x => x.Name).ToList(), restorable: true))
                {
                    return;
                }

                batchDeleting = true;
                var outcome = await BatchDelete.RunAsync(items, x => x.Id.ToString(), x => x.Name,
                    x => Api.DeleteAsync(x.Id), Logger);
                BatchDelete.Notify(NotificationService, outcome);
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unhandled exception while batch deleting categories.");
                ViewNotification.UnexpectedError(NotificationService, "刪除分類時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
            finally
            {
                batchDeleting = false;
            }
        }

        bool showDeleted;

        /// <summary>有刪除權限（勾選欄與「刪除選取」用；0.9.150 起不用 RequirePermission 包勾選欄，見 razor 的註解）。</summary>
        bool canDelete;

        async Task OnToggleDeletedAsync()
        {
            showDeleted = !showDeleted;
            _pageIndex = 1;
            await ReloadAsync();
        }

        async Task OnRestoreAsync(CategoryAdapterModel record)
        {
            try
            {
                var ok = await ConfirmDialog.AskAsync(ModalService, "確認還原", $"要還原「{record.Name}」嗎？", "還原");
                if (!ok)
                {
                    return;
                }

                var result = await Api.RestoreAsync(record.Id);
                if (!result.Success)
                {
                    Logger.LogInformation("Category restore rejected. CategoryId={CategoryId}, Message={Message}", record.Id, result.Message);
                    ViewNotification.Error(NotificationService, result.Message);
                    return;
                }

                Logger.LogInformation("Category restore completed. CategoryId={CategoryId}", record.Id);
                ViewNotification.Warning(NotificationService, "還原成功");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unhandled exception while restoring category.");
                ViewNotification.UnexpectedError(NotificationService, "還原分類時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        async Task OnPurgeAsync(CategoryAdapterModel record)
        {
            try
            {
                var ok = await ConfirmDialog.AskDestructiveAsync(
                    ModalService,
                    "永久刪除",
                    $"永久刪除「{record.Name}」後無法復原。確定要永久刪除嗎？",
                    "永久刪除");
                if (!ok)
                {
                    return;
                }

                var result = await Api.PurgeAsync(record.Id);
                if (!result.Success)
                {
                    Logger.LogInformation("Category purge rejected. CategoryId={CategoryId}, Message={Message}", record.Id, result.Message);
                    ViewNotification.Error(NotificationService, result.Message);
                    return;
                }

                Logger.LogInformation("Category purge completed. CategoryId={CategoryId}", record.Id);
                ViewNotification.Warning(NotificationService, "已永久刪除");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unhandled exception while purging category.");
                ViewNotification.UnexpectedError(NotificationService, "永久刪除分類時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        Task OnAddAsync()
        {
            CurrentRecord = new();
            isNewRecordMode = true;
            modalTitle = "新增分類";
            // ⚠️ 必須是開窗前的最後一步：任何預設值都要先塞完，否則會被當成使用者的變更。
            dirtyTracker.Capture(CurrentRecord);

            modalVisible = true;
            Logger.LogInformation("Opened create modal for category.");
            return Task.CompletedTask;
        }

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
                Logger,
                NotificationService,
                "儲存分類時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
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
                    Logger.LogInformation("Category form validation failed. Error={Error}", error);
                    ViewNotification.ValidationError(NotificationService, error);
                }

                return false;
            }

            // 一個字都沒改就按儲存：不值得白寫一筆，也不該白跳一次確認窗。
            if (isNewRecordMode == false && dirtyTracker.IsDirty(CurrentRecord) == false)
            {
                Logger.LogDebug("Category save skipped because nothing changed. CategoryId={CategoryId}", CurrentRecord.Id);
                ViewNotification.Info(NotificationService, "沒有任何變更，未進行儲存。");

                return true;
            }

            // 儲存前的二次確認。排在名稱檢查之前：使用者若選「再檢查」，就不必白跑一次伺服器來回。
            if (await FormEditConfirm.AskSaveAsync(ModalService) == false)
            {
                Logger.LogDebug("Category save cancelled at save confirmation. CategoryId={CategoryId}", CurrentRecord.Id);
                return false;
            }

            var dto = CategoryDtoMapper.ToDto(CurrentRecord);

            // 名稱重複檢查排在團隊確認對話窗之前：這個檢查便宜、且失敗時必定不能儲存，
            // 沒有理由讓使用者先回答一個對話窗才被告知名稱重複。
            var checkResult = await Api.CheckAsync(dto);
            if (!checkResult.Success)
            {
                Logger.LogInformation("Category pre-check failed. CategoryId={CategoryId}, Message={Message}", CurrentRecord.Id, checkResult.Message);
                ViewNotification.Error(NotificationService, checkResult.Message);

                return false;
            }

            if (await ConfirmTeamBindingAsync() == false)
            {
                Logger.LogDebug("Category save cancelled at team confirmation. CategoryId={CategoryId}", CurrentRecord.Id);

                // 保持 Modal 開啟，讓使用者回到原本的編輯內容重新指定團隊。
                return false;
            }

            VerifyRecordResult actionResult = isNewRecordMode
                ? await Api.CreateAsync(dto)
                : await Api.UpdateAsync(CurrentRecord.Id, dto);
            Logger.LogInformation("Category save submitted. IsNew={IsNew}, CategoryId={CategoryId}", isNewRecordMode, CurrentRecord.Id);

            // 檢查通過不代表寫得進去：唯一索引與並行衝突在伺服器端仍會擋下，
            // 忽略這個回傳值會讓失敗的儲存顯示成「新增成功」。
            if (!actionResult.Success)
            {
                ViewNotification.Error(NotificationService, actionResult.Message);

                return false;
            }

            ViewNotification.Warning(NotificationService, isNewRecordMode ? "新增成功" : "修改成功");

            if (isNewRecordMode)
            {
                _ = MessageService.SuccessAsync("新增成功");
            }

            await ReloadAsync();
            dirtyTracker.Clear();

            return true;
        }

        /// <summary>
        /// 儲存前對「團隊設定」提出警告。兩種情況只會擇一提醒：
        /// 1. 完全沒指定團隊 —— 這筆會變成所有人都看得到的公用分類。
        /// 2. 指定的團隊與自己所屬團隊沒有交集 —— 存檔後自己就會在清單上看不到它。
        /// 回傳 true 表示可以繼續儲存。
        /// </summary>
        private async Task<bool> ConfirmTeamBindingAsync()
        {
            if (CurrentRecord.Teams.Count == 0)
            {
                return await TeamBindingConfirm.AskAsync(
                    ModalService,
                    "此分類未指定適用團隊，將出現在所有使用者的分類下拉清單中（視同公用分類）。確定要這樣儲存嗎？");
            }

            if (isAdmin == false
                && myTeams.Count > 0
                && CurrentRecord.Teams.Any(x => myTeams.Any(
                    team => string.Equals(x, team, StringComparison.OrdinalIgnoreCase))) == false)
            {
                return await TeamBindingConfirm.AskAsync(
                    ModalService,
                    "此分類指定的團隊不包含你所屬的任何團隊，儲存後你將無法在分類清單中看到這筆資料。確定要這樣儲存嗎？");
            }

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
            modalVisible = await FormModalFlow.ConfirmCloseAsync(ModalService, dirtyTracker.IsDirty(CurrentRecord));

            if (modalVisible == false)
            {
                dirtyTracker.Clear();
                Logger.LogDebug("Category modal cancelled.");
            }
        }

        public void OnEditContestChanged(EditContext context)
        {
            LocalEditContext = context;
        }
    }
}
