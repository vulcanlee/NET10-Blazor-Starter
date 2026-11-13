using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Models;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Client.Views.Teams
{
    /// <summary>
    /// 團隊清單（部門樹；0.10.2 起在 WASM 執行，WASM 遷移 M3）。畫面、對話窗流程與提示訊息照 Server 版，資料改走 <see cref="TeamApi"/>：
    /// 上層規則、改名同步引用、有下屬不可刪與稽核都在伺服器的 <c>TeamService</c>，這裡只負責畫面（部門樹在畫面上組）。
    /// </summary>
    public partial class TeamViewView
    {
        [Inject]
        public IJSRuntime JSRuntime { get; set; } = default!;

        [Inject]
        public TeamApi Api { get; set; } = default!;

        [Inject]
        public PagePermissions Permissions { get; set; } = default!;

        [Inject]
        public ModalService ModalService { get; set; } = default!;

        [Inject]
        public MessageService MessageService { get; set; } = default!;

        [Inject]
        public NotificationService NotificationService { get; set; } = default!;

        [Inject]
        public ILogger<TeamViewView> Logger { get; set; } = default!;

        /// <summary>
        /// 匯出 Excel：與清單同一個查詢（沒有搜尋、不是「顯示已刪除」時是部門樹，匯出全部團隊）；
        /// 伺服器產生檔案並寫稽核，超過上限筆數時顯示原因。
        /// </summary>
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
                Logger.LogInformation("Team export downloaded. Bytes={Bytes}", outcome.Content!.Length);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Team export failed.");
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

        List<TeamAdapterModel> teamAdapterModels = new();
        IEnumerable<TeamAdapterModel> selectedRows = [];
        bool batchDeleting;

        /// <summary>每個團隊的上層 Id，給「刪除選取」排出先下屬、後上層的順序（0.9.132 起）。</summary>
        Dictionary<int, int?> parentIds = new();

        /// <summary>部門樹（0.9.105 起）：上層 Id → 下屬；名稱查詢給「上層部門」欄與表單用。</summary>
        ILookup<int, TeamAdapterModel> childrenLookup = Enumerable.Empty<TeamAdapterModel>().ToLookup(x => 0);
        Dictionary<int, string> teamNames = new();
        List<TeamNodeDto> parentCandidates = new();

        bool IsTreeMode => !showDeleted && string.IsNullOrWhiteSpace(searchText);

        IEnumerable<TeamAdapterModel> ChildrenOf(TeamAdapterModel item) => IsTreeMode ? childrenLookup[item.Id] : [];

        string ParentNameOf(TeamAdapterModel item)
            => item.ParentId is { } parentId ? teamNames.GetValueOrDefault(parentId, "（已刪除的團隊）") : "—";

        /// <summary>下拉以字串綁定（與其他表單的多選一致），留白＝最上層。</summary>
        string? ParentIdText
        {
            get => CurrentRecord.ParentId?.ToString();
            set => CurrentRecord.ParentId = int.TryParse(value, out var id) ? id : null;
        }

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

        /// <summary>「資料保留與清理」頁的「前往」帶 <c>?deleted=1</c> 進來時為 true：一進頁面就顯示已刪除的資料（0.9.133 起）。</summary>
        [Parameter]
        public bool InitialShowDeleted { get; set; }

        protected override async Task OnInitializedAsync()
        {
            // 登入檢查已由版面（ClientMainLayout）完成；沒有權限的開頁稽核由伺服器在開頁紀錄時寫（shell/page-view）。
            isAccessChecked = true;

            if (Permissions.CanAccessPage(MagicObjectHelper.角色_團隊清單) == false)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                Logger.LogWarning("Team view denied because current user has not this role permission.");
                return;
            }

            // 只有看得到「顯示已刪除」按鈕的人（刪除權限）才套用。
            canDelete = Permissions.CanAccessAction(MagicObjectHelper.角色_團隊清單, PermissionActions.Delete);
            showDeleted = InitialShowDeleted && canDelete;
            await ReloadAsync();
        }

        /// <summary>目前的搜尋、排序、分頁與「顯示已刪除」（平面清單與匯出共用）。</summary>
        TeamSearchRequestDto BuildRequest(bool recordSearch = false) => new()
        {
            Keyword = searchText,
            SortBy = sortDirection is "Ascending" or "Descending" ? sortField : null,
            SortDescending = sortDirection == "Descending",
            PageIndex = _pageIndex,
            PageSize = _pageSize,
            Deleted = showDeleted,
            RecordSearch = recordSearch,
        };

        /// <summary>重新讀取；回傳是否成功（失敗時已顯示通知）。</summary>
        public async Task<bool> ReloadAsync(bool recordSearch = false)
        {
            // 換頁、排序、搜尋、切換與刪除後都會走到這裡：勾選只在目前這一頁有效（0.9.132 起）。
            selectedRows = [];
            Logger.LogDebug(
                "Reloading teams. HasSearch={HasSearch}, SortField={SortField}, SortDirection={SortDirection}, PageIndex={PageIndex}, PageSize={PageSize}",
                string.IsNullOrWhiteSpace(searchText) == false, sortField, sortDirection, _pageIndex, _pageSize);

            var allResult = await Api.GetAllAsync();
            if (!allResult.Success || allResult.Data is null)
            {
                ViewNotification.Error(NotificationService, allResult.UserMessage());
                return false;
            }

            var all = allResult.Data.Select(TeamDtoMapper.ToModel).ToList();
            teamNames = all.ToDictionary(x => x.Id, x => x.Name);
            parentIds = all.ToDictionary(x => x.Id, x => x.ParentId);
            if (IsTreeMode)
            {
                // 一次載入全部在畫面上組樹；上層不存在（資料異常）的當作最上層，不會消失。
                childrenLookup = all.Where(x => x.ParentId is { } p && teamNames.ContainsKey(p)).ToLookup(x => x.ParentId!.Value);
                var roots = SortRoots(all.Where(x => x.ParentId is not { } p || !teamNames.ContainsKey(p))).ToList();
                _total = roots.Count;
                teamAdapterModels = roots.Skip((_pageIndex - 1) * _pageSize).Take(_pageSize).ToList();
                Logger.LogDebug("Team tree reloaded successfully. Count={Count}, Roots={Roots}", all.Count, _total);
                if (recordSearch)
                {
                    // 樹狀模式（關鍵字空白）按搜尋：仍要讓伺服器寫搜尋稽核。
                    var auditOnly = BuildRequest(recordSearch: true);
                    auditOnly.PageSize = 1;
                    await Api.SearchAsync(auditOnly);
                }

                StateHasChanged();
                return true;
            }

            // 「顯示已刪除」開啟時改讀已刪除的資料（0.9.94 起）。
            var result = await Api.SearchAsync(BuildRequest(recordSearch));
            if (!result.Success || result.Data is null)
            {
                ViewNotification.Error(NotificationService, result.UserMessage());
                return false;
            }

            teamAdapterModels = result.Data.Items.Select(TeamDtoMapper.ToModel).ToList();
            _total = result.Data.TotalCount;
            Logger.LogDebug("Team list reloaded successfully. Count={Count}", _total);
            StateHasChanged();
            return true;
        }

        /// <summary>樹狀模式的最上層排序：沿用欄位排序，預設依名稱。</summary>
        IEnumerable<TeamAdapterModel> SortRoots(IEnumerable<TeamAdapterModel> roots)
        {
            var descending = sortDirection == "Descending";
            return sortField switch
            {
                nameof(TeamAdapterModel.Code) => descending ? roots.OrderByDescending(x => x.Code) : roots.OrderBy(x => x.Code),
                nameof(TeamAdapterModel.IsEnabled) => descending ? roots.OrderByDescending(x => x.IsEnabled) : roots.OrderBy(x => x.IsEnabled),
                nameof(TeamAdapterModel.UpdatedAt) => descending ? roots.OrderByDescending(x => x.UpdatedAt) : roots.OrderBy(x => x.UpdatedAt),
                nameof(TeamAdapterModel.Name) when descending => roots.OrderByDescending(x => x.Name),
                _ => roots.OrderBy(x => x.Name),
            };
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

            Logger.LogDebug("Team table changed. PageIndex={PageIndex}, SortField={SortField}, SortDirection={SortDirection}", _pageIndex, sortField, sortDirection);
            await ReloadAsync();
        }


        async Task OnSearchAsync()
        {
            _pageIndex = 1;
            Logger.LogInformation("Team search triggered. HasSearch={HasSearch}", string.IsNullOrWhiteSpace(searchText) == false);
            // 搜尋稽核由伺服器寫（只有按「搜尋」才帶 RecordSearch）。
            await ReloadAsync(recordSearch: true);
        }

        async Task OnRefreshAsync()
        {
            Logger.LogInformation("Team refresh triggered.");
            if (await ReloadAsync())
            {
                ViewNotification.Warning(NotificationService, "已更新最新資料");
            }
        }

        /// <summary>開修改窗：向伺服器讀這一筆的最新資料與版本號（伺服器寫「查看」稽核），以及可選的上層團隊。</summary>
        async Task OnEditAsync(TeamAdapterModel teamAdapterModel)
        {
            var result = await Api.GetAsync(teamAdapterModel.Id);
            if (!result.Success || result.Data is null)
            {
                ViewNotification.Error(NotificationService, result.UserMessage());
                await ReloadAsync();
                return;
            }

            isNewRecordMode = false;
            modalTitle = "修改團隊";
            CurrentRecord = TeamDtoMapper.ToModel(result.Data);
            parentCandidates = await LoadParentCandidatesAsync(teamAdapterModel.Id);
            // ⚠️ 必須是開窗前的最後一步：任何預設值都要先塞完，否則會被當成使用者的變更。
            dirtyTracker.Capture(CurrentRecord);

            modalVisible = true;
            Logger.LogInformation("Opened edit modal for team. TeamId={TeamId}", teamAdapterModel.Id);
        }

        /// <summary>名稱欄位的非同步檢查（<see cref="AsyncFieldCheck"/>）：回「名稱已存在」的訊息，沒衝突回 null（0.10.15 起）。</summary>
        private Task<string?> CheckNameAvailableAsync()
        {
            // 只問名稱：代號重複也是 409，帶著代號問會把「代號已存在」顯示在名稱下面（代號留給存檔時的完整檢查）。
            var dto = TeamDtoMapper.ToDto(CurrentRecord);
            dto.Code = null;
            return Api.NameConflictAsync(dto);
        }

        async Task<List<TeamNodeDto>> LoadParentCandidatesAsync(int teamId)
        {
            var result = await Api.GetParentCandidatesAsync(teamId);
            if (!result.Success || result.Data is null)
            {
                ViewNotification.Error(NotificationService, result.UserMessage());
                return [];
            }

            return result.Data;
        }

        /// <summary>
        /// 點狀態標籤切換啟用／停用（0.9.120 起）：停用先確認，啟用直接切換；只改狀態，不經修改窗。
        /// </summary>
        async Task OnToggleEnabledAsync(TeamAdapterModel record)
        {
            try
            {
                var enable = !record.IsEnabled;
                if (!enable && !await ConfirmDialog.AskAsync(ModalService, "停用團隊",
                        $"要停用「{record.Name}」嗎？停用後它不會出現在下拉選單，已綁定的人員與資料不受影響。", "停用"))
                {
                    return;
                }

                var result = await Api.SetEnabledAsync(record.Id, enable);
                if (!result.Success)
                {
                    Logger.LogInformation("Team status change rejected. TeamId={TeamId}, Message={Message}", record.Id, result.Message);
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
                Logger.LogError(ex, "Unhandled exception while changing team status.");
                ViewNotification.UnexpectedError(NotificationService, "切換團隊狀態時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        /// <summary>包住實際邏輯以捕捉未預期的例外：WASM 頁面的例外會讓整頁換成錯誤畫面，這裡改成右下角提示。</summary>
        async Task OnDeleteAsync(TeamAdapterModel teamAdapterModel)
        {
            try
            {
                await OnDeleteCoreAsync(teamAdapterModel);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unhandled exception while deleting team.");
                ViewNotification.UnexpectedError(NotificationService, "刪除團隊時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        async Task OnDeleteCoreAsync(TeamAdapterModel teamAdapterModel)
        {
            Logger.LogInformation("Delete team requested. TeamId={TeamId}", teamAdapterModel.Id);

            var ok = await ConfirmDialog.AskSoftDeleteRecordAsync(ModalService);

            if (!ok)
            {
                Logger.LogDebug("Team delete cancelled by user. TeamId={TeamId}", teamAdapterModel.Id);
                return;
            }

            var result = await Api.DeleteAsync(teamAdapterModel.Id);
            if (!result.Success)
            {
                Logger.LogInformation("Team delete rejected. TeamId={TeamId}, Message={Message}", teamAdapterModel.Id, result.Message);
                ViewNotification.Error(NotificationService, result.Message);
                return;
            }

            Logger.LogInformation("Team delete completed. TeamId={TeamId}", teamAdapterModel.Id);

            ViewNotification.Warning(NotificationService, "刪除成功");

            await ReloadAsync();
        }

        /// <summary>
        /// 「刪除選取」（0.9.132 起）：逐筆呼叫刪除 API，規則與稽核同單筆刪除。
        /// 由深到淺刪：同時勾了上層與它全部的下屬時，上層不會因「還有下屬部門」被擋下；
        /// 只勾上層、下屬仍在時照原規則失敗。
        /// </summary>
        async Task OnBatchDeleteAsync()
        {
            try
            {
                var items = BatchDelete.DeepestFirst(selectedRows, x => DepthOf(x.Id));
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
                Logger.LogError(ex, "Unhandled exception while batch deleting teams.");
                ViewNotification.UnexpectedError(NotificationService, "刪除團隊時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
            finally
            {
                batchDeleting = false;
            }
        }

        /// <summary>往上數到最上層的層數；上限為團隊總數，資料若有循環也不會卡住。</summary>
        int DepthOf(int id)
        {
            var depth = 0;
            while (parentIds.GetValueOrDefault(id) is { } parent && depth < parentIds.Count)
            {
                id = parent;
                depth++;
            }

            return depth;
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

        async Task OnRestoreAsync(TeamAdapterModel record)
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
                    Logger.LogInformation("Team restore rejected. TeamId={TeamId}, Message={Message}", record.Id, result.Message);
                    ViewNotification.Error(NotificationService, result.Message);
                    return;
                }

                Logger.LogInformation("Team restore completed. TeamId={TeamId}", record.Id);
                ViewNotification.Warning(NotificationService, "還原成功");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unhandled exception while restoring team.");
                ViewNotification.UnexpectedError(NotificationService, "還原團隊時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        async Task OnPurgeAsync(TeamAdapterModel record)
        {
            try
            {
                var ok = await ConfirmDialog.AskDestructiveAsync(
                    ModalService,
                    "永久刪除",
                    $"永久刪除「{record.Name}」後無法復原，使用者與這個團隊的關聯也會一併刪除。確定要永久刪除嗎？",
                    "永久刪除");
                if (!ok)
                {
                    return;
                }

                var result = await Api.PurgeAsync(record.Id);
                if (!result.Success)
                {
                    Logger.LogInformation("Team purge rejected. TeamId={TeamId}, Message={Message}", record.Id, result.Message);
                    ViewNotification.Error(NotificationService, result.Message);
                    return;
                }

                Logger.LogInformation("Team purge completed. TeamId={TeamId}", record.Id);
                ViewNotification.Warning(NotificationService, "已永久刪除");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Unhandled exception while purging team.");
                ViewNotification.UnexpectedError(NotificationService, "永久刪除團隊時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        async Task OnAddAsync()
        {
            CurrentRecord = new();
            isNewRecordMode = true;
            modalTitle = "新增團隊";
            parentCandidates = await LoadParentCandidatesAsync(0);
            // ⚠️ 必須是開窗前的最後一步：任何預設值都要先塞完，否則會被當成使用者的變更。
            dirtyTracker.Capture(CurrentRecord);

            modalVisible = true;
            Logger.LogInformation("Opened create modal for team.");
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
                "儲存團隊時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
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
                    Logger.LogInformation("Team form validation failed. Error={Error}", error);
                    ViewNotification.ValidationError(NotificationService, error);
                }

                return false;
            }

            // 一個字都沒改就按儲存：不值得白寫一筆，也不該白跳一次確認窗。
            if (isNewRecordMode == false && dirtyTracker.IsDirty(CurrentRecord) == false)
            {
                Logger.LogDebug("Team save skipped because nothing changed. TeamId={TeamId}", CurrentRecord.Id);
                ViewNotification.Info(NotificationService, "沒有任何變更，未進行儲存。");

                return true;
            }

            // 儲存前的二次確認。排在名稱檢查之前：使用者若選「再檢查」，就不必白跑一次伺服器來回。
            if (await FormEditConfirm.AskSaveAsync(ModalService) == false)
            {
                Logger.LogDebug("Team save cancelled at save confirmation. TeamId={TeamId}", CurrentRecord.Id);
                return false;
            }

            var dto = TeamDtoMapper.ToDto(CurrentRecord);
            var checkResult = await Api.CheckAsync(dto);
            if (!checkResult.Success)
            {
                Logger.LogInformation("Team pre-check failed. TeamId={TeamId}, Message={Message}", CurrentRecord.Id, checkResult.Message);
                ViewNotification.Error(NotificationService, checkResult.Message);

                return false;
            }

            VerifyRecordResult actionResult = isNewRecordMode
                ? await Api.CreateAsync(dto)
                : await Api.UpdateAsync(CurrentRecord.Id, dto);
            Logger.LogInformation("Team save submitted. IsNew={IsNew}, TeamId={TeamId}", isNewRecordMode, CurrentRecord.Id);

            // 檢查通過不代表寫得進去：唯一索引、上層規則與並行衝突在伺服器端仍會擋下，
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
                Logger.LogDebug("Team modal cancelled.");
            }
        }

        public void OnEditContestChanged(EditContext context)
        {
            LocalEditContext = context;
        }
    }
}
