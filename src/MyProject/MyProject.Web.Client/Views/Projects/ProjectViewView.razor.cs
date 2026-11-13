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
using MyProject.Web.Client.Commons;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Client.Views.Projects;

/// <summary>
/// 專案項目（0.10.3 起在 WASM 執行，WASM 遷移 M4）。畫面、對話窗流程與提示訊息照 Server 版，資料改走 <see cref="ProjectApi"/>：
/// 團隊範圍、名稱與欄位規則、並行衝突、附件與稽核都在伺服器的 <c>ProjectService</c>，這裡只負責畫面。
/// 附件不經過 WASM 記憶體：選檔時檔案留在瀏覽器（<see cref="BrowserFiles"/>），存檔時與資料一起由瀏覽器直接送出；
/// 下載先申請一次性票證，再交給瀏覽器原生下載。
/// </summary>
public partial class ProjectViewView
{
    [Inject]
    public IJSRuntime JSRuntime { get; set; } = default!;

    [Inject]
    public ProjectApi Api { get; set; } = default!;

    [Inject]
    public BrowserFiles BrowserFiles { get; set; } = default!;

    [Inject]
    public PagePermissions Permissions { get; set; } = default!;

    [Inject]
    public ModalService ModalService { get; set; } = default!;

    [Inject]
    public MessageService MessageService { get; set; } = default!;

    [Inject]
    public NotificationService NotificationService { get; set; } = default!;

    [Inject]
    public ILogger<ProjectViewView> Logger { get; set; } = default!;

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
            Logger.LogInformation("Project export downloaded. Bytes={Bytes}", outcome.Content!.Length);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Project export failed.");
            ViewNotification.UnexpectedError(NotificationService, $"匯出失敗：{ex.GetType().Name}。", ex);
        }
    }

    private ITable? table;
    /// <summary>「異動歷程」對話窗（0.9.173 起），由每列的 AuditHistoryButton 叫出。</summary>
    private AuditHistoryDialog historyDialog = default!;
    private InputFile? fileInput;

    /// <summary>目前使用者是管理員、團隊範圍（伺服器的 <c>options</c>）；表單「團隊」的選項用。</summary>
    private bool isAdmin;
    private List<string> myTeams = [];

    private List<string> availableCategories = [];
    private List<string> availableTeams = [];

    /// <summary>
    /// 表單「團隊」的選項（0.9.105 起）：管理員是全部；其他人只有自己範圍內的團隊（含下屬部門），
    /// 加上這筆原本就有的（才看得到、也能保留）。伺服器端 <c>RecordTeamScope.CheckAssignment</c> 會再檢查一次。
    /// </summary>
    private IEnumerable<string> AssignableTeams => isAdmin
        ? availableTeams
        : availableTeams.Where(t => myTeams.Contains(t, StringComparer.OrdinalIgnoreCase) || CurrentRecord.Teams.Contains(t, StringComparer.OrdinalIgnoreCase));
    private List<string> selectedCategoryFilters = [];
    private List<string> selectedTeamFilters = [];

    /// <summary>編輯 Modal 的分類選項；與工具列過濾器不同，會額外帶上這筆紀錄的既有值。</summary>
    private List<string> modalCategoryOptions = [];

    /// <summary>這筆紀錄已貼、但目前使用者看不到的分類（顯示時加註，避免被誤認為可自由選取）。</summary>
    private readonly HashSet<string> restrictedCategoryValues = new(StringComparer.OrdinalIgnoreCase);
    private int _pageIndex = 1;
    private int _pageSize = MagicObjectHelper.PageSize;
    private int _total;
    private string searchText = string.Empty;
    private string sortField = string.Empty;
    private string sortDirection = "None";

    private List<ProjectAdapterModel> projectAdapterModels = [];
    private IEnumerable<ProjectAdapterModel> selectedRows = [];
    private bool batchDeleting;

    /// <summary>待上傳的檔案（內容在瀏覽器，這裡只有 id、檔名、大小）。</summary>
    private readonly List<PickedFile> pendingUploadFiles = [];
    private readonly HashSet<int> removedFileIds = [];

    /// <summary>單一附件的大小上限（伺服器的 <c>options</c>；讀不到時用 1 GB，伺服器仍會再檢查）。</summary>
    private long maxUploadFileSize = 1024L * 1024L * 1024L;

    /// <summary>允許的副檔名（伺服器的 <c>options</c>，含點、小寫）；讀不到時是空的，改由伺服器在存檔時檢查。</summary>
    private HashSet<string> allowedExtensions = new(StringComparer.OrdinalIgnoreCase);

    private string modalTitle = "專案維護";
    private bool modalVisible;
    private ProjectAdapterModel CurrentRecord = new();
    public EditContext? LocalEditContext { get; set; }
    private bool isNewRecordMode;
    private string RoleMessage = string.Empty;
    private bool isAccessChecked;

    /// <summary>
    /// 未儲存變更偵測。開窗時 Capture、按取消／儲存時比對，
    /// 「改了又改回原值」視為無變更，不會白問使用者一次。
    /// </summary>
    private readonly FormDirtyTracker dirtyTracker = new();

    private IReadOnlyList<string> StatusOptions => ProjectAdapterModel.StatusOptions;
    private IReadOnlyList<string> PriorityOptions => ProjectAdapterModel.PriorityOptions;

    /// <summary>新增時預設帶入的團隊：範圍裡最上層的那些（0.9.105 起範圍含下屬部門，全帶會讓專案對所有下屬公開）。</summary>
    private List<string> defaultTeams = [];

    /// <summary>「資料保留與清理」頁的「前往」帶 <c>?deleted=1</c> 進來時為 true：一進頁面就顯示已刪除的資料（0.9.133 起）。</summary>
    [Parameter]
    public bool InitialShowDeleted { get; set; }

    protected override async Task OnInitializedAsync()
    {
        // 登入檢查已由版面（ClientMainLayout）完成；沒有權限的開頁稽核由伺服器在開頁紀錄時寫（shell/page-view）。
        if (Permissions.CanAccessPage(MagicObjectHelper.角色_專案項目) == false)
        {
            isAccessChecked = true;
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            Logger.LogWarning("Project management view denied because current user has not this role permission.");
            return;
        }

        var options = await Api.OptionsAsync();
        if (options.Success && options.Data is { } data)
        {
            availableCategories = data.Categories;
            availableTeams = data.Teams;
            isAdmin = data.IsAdmin;
            myTeams = data.MyTeams;
            defaultTeams = data.DefaultTeams;
            maxUploadFileSize = data.MaxUploadFileSize;
            allowedExtensions = new HashSet<string>(data.AllowedExtensions, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            // 讀不到選項時過濾與表單的下拉會是空的：告訴使用者，而不是默默少了選項。
            ViewNotification.Error(NotificationService, $"讀取分類與團隊選項失敗：{options.UserMessage()}");
        }

        // 只有看得到「顯示已刪除」按鈕的人（刪除權限）才套用。
        canDelete = Permissions.CanAccessAction(MagicObjectHelper.角色_專案項目, PermissionActions.Delete);
        showDeleted = InitialShowDeleted && canDelete;

        // ⚠️ 勾選欄是否出現（canDelete）要在表格第一次畫出來之前定案（見分類頁的說明：之後才加上 <Selection /> 會在釋放時出錯）。
        isAccessChecked = true;
        await ReloadAsync();
    }

    /// <summary>目前的搜尋、過濾、排序、分頁與「顯示已刪除」（清單與匯出共用）。</summary>
    private ProjectSearchRequestDto BuildRequest(bool recordSearch = false) => new()
    {
        Keyword = searchText,
        SortBy = sortDirection is "Ascending" or "Descending" ? sortField : null,
        SortDescending = sortDirection == "Descending",
        PageIndex = _pageIndex,
        PageSize = _pageSize,
        // 「顯示已刪除」時工具列沒有分類、團隊過濾，與 Server 版相同不套用（之前選的值仍保留，回清單時再套用）。
        Categories = showDeleted ? [] : selectedCategoryFilters.ToList(),
        Teams = showDeleted ? [] : selectedTeamFilters.ToList(),
        Deleted = showDeleted,
        RecordSearch = recordSearch,
    };

    /// <summary>重新讀取；回傳是否成功（失敗時已顯示通知）。</summary>
    public async Task<bool> ReloadAsync(bool recordSearch = false)
    {
        // 換頁、排序、搜尋、篩選、切換與刪除後都會走到這裡：勾選只在目前這一頁有效（0.9.132 起）。
        selectedRows = [];
        Logger.LogDebug(
            "Reloading projects. HasSearch={HasSearch}, SortField={SortField}, SortDirection={SortDirection}, PageIndex={PageIndex}, PageSize={PageSize}",
            string.IsNullOrWhiteSpace(searchText) == false, sortField, sortDirection, _pageIndex, _pageSize);

        var result = await Api.SearchAsync(BuildRequest(recordSearch));
        if (!result.Success || result.Data is null)
        {
            ViewNotification.Error(NotificationService, result.UserMessage());
            return false;
        }

        projectAdapterModels = result.Data.Items.Select(ProjectDtoMapper.ToModel).ToList();
        _total = result.Data.TotalCount;
        Logger.LogDebug("Project list reloaded successfully. Count={Count}", _total);
        StateHasChanged();
        return true;
    }

    private async Task OnCategoryFilterChanged(IEnumerable<string> values)
    {
        selectedCategoryFilters = values?.ToList() ?? [];
        _pageIndex = 1;
        await ReloadAsync();
    }

    private async Task OnTeamFilterChanged(IEnumerable<string> values)
    {
        selectedTeamFilters = values?.ToList() ?? [];
        _pageIndex = 1;
        await ReloadAsync();
    }

    private void OnRecordCategoriesChanged(IEnumerable<string> values)
    {
        CurrentRecord.Categories = values?.ToList() ?? [];
    }

    /// <summary>
    /// 編輯 Modal 的分類選項 = 目前可見分類 ∪ 這筆紀錄已貼但已限定其他團隊的分類。
    /// 後者若不列出，AntDesign 的多選 Select 會把它視為未知值，使用者一存檔就被靜默清掉。
    /// </summary>
    private void BuildModalCategoryOptions()
    {
        restrictedCategoryValues.Clear();

        var options = new List<string>(availableCategories);
        var visible = new HashSet<string>(availableCategories, StringComparer.OrdinalIgnoreCase);

        foreach (var category in CurrentRecord.Categories.Where(x => visible.Contains(x) == false))
        {
            options.Add(category);
            restrictedCategoryValues.Add(category);
        }

        modalCategoryOptions = options;
    }

    /// <summary>選項顯示文字；Value 一律維持原始名稱，只有顯示加註。</summary>
    private string CategoryOptionLabel(string name)
        => restrictedCategoryValues.Contains(name) ? $"{name}（已限定其他團隊）" : name;

    private void OnRecordTeamsChanged(IEnumerable<string> values)
    {
        CurrentRecord.Teams = values?.ToList() ?? [];
    }

    private async Task OnTableChange(QueryModel<ProjectAdapterModel> args)
    {
        _pageIndex = args.PageIndex;

        if (args.SortModel?.Any() == true)
        {
            var tableSortModel = TableSortHelper.GetCurrentSortModel(args.SortModel);
            sortDirection = tableSortModel.SortDirection.ToString() ?? string.Empty;
            sortField = TableSortHelper.ResolveSortFieldName(tableSortModel);
        }
        else
        {
            sortField = string.Empty;
            sortDirection = "None";
        }

        Logger.LogDebug("Project table changed. PageIndex={PageIndex}, SortField={SortField}, SortDirection={SortDirection}", _pageIndex, sortField, sortDirection);
        await ReloadAsync();
    }

    private async Task OnSearchAsync()
    {
        _pageIndex = 1;
        Logger.LogInformation("Project search triggered. HasSearch={HasSearch}", string.IsNullOrWhiteSpace(searchText) == false);
        // 搜尋稽核由伺服器寫（只有按「搜尋」才帶 RecordSearch）。
        await ReloadAsync(recordSearch: true);
    }

    private async Task OnRefreshAsync()
    {
        Logger.LogInformation("Project refresh triggered.");
        if (await ReloadAsync())
        {
            ViewNotification.Warning(NotificationService, "已更新最新資料");
        }
    }

    /// <summary>開修改窗：向伺服器讀這一筆的最新資料、附件與版本號（伺服器寫「查看」稽核）。</summary>
    private async Task OnEditAsync(ProjectAdapterModel projectAdapterModel)
    {
        var result = await Api.GetAsync(projectAdapterModel.Id);
        if (!result.Success || result.Data is null)
        {
            ViewNotification.Error(NotificationService, result.UserMessage());
            await ReloadAsync();
            return;
        }

        isNewRecordMode = false;
        modalTitle = "修改專案";
        CurrentRecord = ProjectDtoMapper.ToModel(result.Data);
        await ClearPendingFilesAsync();
        removedFileIds.Clear();
        BuildModalCategoryOptions();

        // ⚠️ 必須是開窗前的最後一步：任何預設值與關聯資料都要先塞完。
        //    第二個參數是檔案指紋 —— 待上傳與待刪除的檔案不在 ProjectAdapterModel 裡，
        //    不納入比對的話，「只加了檔案、沒動欄位」會被判定為無變更而直接關窗。
        dirtyTracker.Capture(CurrentRecord, UploadStateFingerprint);

        modalVisible = true;
        Logger.LogInformation("Opened edit modal for project. ProjectId={ProjectId}", projectAdapterModel.Id);
    }

    /// <summary>包住實際邏輯以捕捉未預期的例外：WASM 頁面的例外會讓整頁換成錯誤畫面，這裡改成右下角提示。</summary>
    private async Task OnDeleteAsync(ProjectAdapterModel projectAdapterModel)
    {
        try
        {
            await OnDeleteCoreAsync(projectAdapterModel);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unhandled exception while deleting project.");
            ViewNotification.UnexpectedError(NotificationService, "刪除專案時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
        }
    }

    private async Task OnDeleteCoreAsync(ProjectAdapterModel projectAdapterModel)
    {
        Logger.LogInformation("Delete project requested. ProjectId={ProjectId}", projectAdapterModel.Id);

        var ok = await ConfirmDialog.AskSoftDeleteRecordAsync(ModalService);

        if (!ok)
        {
            Logger.LogDebug("Project delete cancelled by user. ProjectId={ProjectId}", projectAdapterModel.Id);
            return;
        }

        var result = await Api.DeleteAsync(projectAdapterModel.Id);
        if (!result.Success)
        {
            Logger.LogInformation("Project delete rejected. ProjectId={ProjectId}, Message={Message}", projectAdapterModel.Id, result.Message);
            ViewNotification.Error(NotificationService, result.Message);
            return;
        }

        Logger.LogInformation("Project delete completed. ProjectId={ProjectId}", projectAdapterModel.Id);

        ViewNotification.Warning(NotificationService, "刪除成功");

        await ReloadAsync();
    }

    /// <summary>「刪除選取」（0.9.132 起）：逐筆呼叫刪除 API，規則與稽核同單筆刪除。</summary>
    private async Task OnBatchDeleteAsync()
    {
        try
        {
            var items = selectedRows.ToList();
            if (items.Count == 0 || !await BatchDelete.AskAsync(ModalService, items.Select(x => x.Title).ToList(), restorable: true))
            {
                return;
            }

            batchDeleting = true;
            var outcome = await BatchDelete.RunAsync(items, x => x.Id.ToString(), x => x.Title, x => Api.DeleteAsync(x.Id), Logger);
            BatchDelete.Notify(NotificationService, outcome);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unhandled exception while batch deleting projects.");
            ViewNotification.UnexpectedError(NotificationService, "刪除專案時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
        }
        finally
        {
            batchDeleting = false;
        }
    }

    private bool showDeleted;

    /// <summary>有刪除權限（勾選欄與「刪除選取」用；0.9.150 起不用 RequirePermission 包勾選欄，見 razor 的註解）。</summary>
    private bool canDelete;

    private async Task OnToggleDeletedAsync()
    {
        showDeleted = !showDeleted;
        _pageIndex = 1;
        await ReloadAsync();
    }

    private async Task OnRestoreAsync(ProjectAdapterModel record)
    {
        try
        {
            var ok = await ConfirmDialog.AskAsync(ModalService, "確認還原", $"要還原「{record.Title}」嗎？", "還原");
            if (!ok)
            {
                return;
            }

            var result = await Api.RestoreAsync(record.Id);
            if (!result.Success)
            {
                Logger.LogInformation("Project restore rejected. ProjectId={ProjectId}, Message={Message}", record.Id, result.Message);
                ViewNotification.Error(NotificationService, result.Message);
                return;
            }

            Logger.LogInformation("Project restore completed. ProjectId={ProjectId}", record.Id);
            ViewNotification.Warning(NotificationService, "還原成功");
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unhandled exception while restoring project.");
            ViewNotification.UnexpectedError(NotificationService, "還原專案時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
        }
    }

    private async Task OnPurgeAsync(ProjectAdapterModel record)
    {
        try
        {
            var ok = await ConfirmDialog.AskDestructiveAsync(
                ModalService,
                "永久刪除",
                $"永久刪除「{record.Title}」後無法復原，附件檔案也會一併刪除。確定要永久刪除嗎？",
                "永久刪除");
            if (!ok)
            {
                return;
            }

            var result = await Api.PurgeAsync(record.Id);
            if (!result.Success)
            {
                Logger.LogInformation("Project purge rejected. ProjectId={ProjectId}, Message={Message}", record.Id, result.Message);
                ViewNotification.Error(NotificationService, result.Message);
                return;
            }

            Logger.LogInformation("Project purge completed. ProjectId={ProjectId}", record.Id);
            ViewNotification.Warning(NotificationService, "已永久刪除");
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unhandled exception while purging project.");
            ViewNotification.UnexpectedError(NotificationService, "永久刪除專案時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
        }
    }

    private async Task OnAddAsync(bool continueOnCapturedContext)
    {
        CurrentRecord = new ProjectAdapterModel
        {
            Status = StatusOptions.First(),
            Priority = PriorityOptions[1],
            CompletionPercentage = 0,
            Files = [],
            // 預設帶入使用者範圍裡最上層的團隊；已停用（不在選項內）的略過，否則多選 Select 會視為未知值。
            Teams = defaultTeams
                .Where(t => availableTeams.Contains(t, StringComparer.OrdinalIgnoreCase))
                .ToList()
        };

        await ClearPendingFilesAsync();
        removedFileIds.Clear();
        BuildModalCategoryOptions();
        isNewRecordMode = true;
        modalTitle = "新增專案";

        // ⚠️ 必須是開窗前的最後一步；第二個參數見 OnEditAsync 的說明。
        dirtyTracker.Capture(CurrentRecord, UploadStateFingerprint);

        modalVisible = true;
        Logger.LogInformation("Opened create modal for project.");
    }

    /// <summary>
    /// 選檔：檔案內容留在瀏覽器，這裡只記 id、檔名、大小。超過上限的不收（訊息同 Server 版）；
    /// 0.10.3 起類型不在允許清單的也在選檔時就擋（訊息與伺服器相同）：WASM 版要整份上傳完伺服器才看得到檔名，
    /// 不先擋的話，選錯一個 900 MB 的檔案要等它傳完才被拒（審查發現）。伺服器存檔時仍會再檢查一次。
    /// </summary>
    private async Task OnProjectFilesSelectedAsync(InputFileChangeEventArgs args)
    {
        if (fileInput?.Element is not { } element)
        {
            return;
        }

        try
        {
            var rejected = new List<string>();
            foreach (var file in await BrowserFiles.CaptureAsync(element))
            {
                if (file.Size > maxUploadFileSize)
                {
                    ViewNotification.Error(NotificationService, $"{file.Name} 超過 1GB 限制");
                    rejected.Add(file.Id);
                    continue;
                }

                if (allowedExtensions.Count > 0 && !allowedExtensions.Contains(Path.GetExtension(file.Name)))
                {
                    ViewNotification.Error(NotificationService, $"檔案 {file.Name} 的類型不在允許清單中。");
                    rejected.Add(file.Id);
                    continue;
                }

                pendingUploadFiles.Add(file);
            }

            if (rejected.Count > 0)
            {
                await BrowserFiles.ReleaseAsync(rejected);
            }
        }
        catch (JSException ex)
        {
            Logger.LogError(ex, "Reading the selected files failed.");
            ViewNotification.UnexpectedError(NotificationService, "讀取選擇的檔案失敗，請重新選擇。", ex);
        }
    }

    /// <summary>下載附件：申請一次性票證後由瀏覽器原生下載；看不到（已刪除、範圍外、檔案不見）時顯示原因。</summary>
    private async Task OnDownloadFileAsync(int fileId)
    {
        try
        {
            var result = await Api.DownloadFileAsync(fileId);
            if (!result.Success)
            {
                ViewNotification.Error(NotificationService, result.Message);
            }
        }
        catch (JSException ex)
        {
            Logger.LogError(ex, "Project file download failed. ProjectFileId={ProjectFileId}", fileId);
            ViewNotification.UnexpectedError(NotificationService, "下載附件失敗，請稍後再試。", ex);
        }
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
            "儲存專案時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
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
                Logger.LogInformation("Project form validation failed. Error={Error}", error);
                ViewNotification.ValidationError(NotificationService, error);
            }

            return false;
        }

        // 一個字都沒改就按儲存：不值得白寫一筆，也不該白跳一次確認窗。
        if (isNewRecordMode == false && dirtyTracker.IsDirty(CurrentRecord) == false)
        {
            Logger.LogDebug("Project save skipped because nothing changed. ProjectId={ProjectId}", CurrentRecord.Id);
            ViewNotification.Info(NotificationService, "沒有任何變更，未進行儲存。");

            return true;
        }

        // 儲存前的二次確認。排在上傳之前：使用者若選「再檢查」，就不必白傳一次檔案。
        if (await FormEditConfirm.AskSaveAsync(ModalService) == false)
        {
            Logger.LogDebug("Project save cancelled at save confirmation. ProjectId={ProjectId}", CurrentRecord.Id);
            return false;
        }

        if (CurrentRecord.Teams.Count == 0
            && await TeamBindingConfirm.AskAsync(
                ModalService,
                "此專案未指定團隊，將對所有使用者公開可見。確定要這樣儲存嗎？") == false)
        {
            Logger.LogDebug("Project save cancelled at team confirmation. ProjectId={ProjectId}", CurrentRecord.Id);

            // 保持 Modal 開啟，讓使用者回到原本的編輯內容重新指定團隊。
            return false;
        }

        // 欄位規則、重名、附件大小與類型都由伺服器在存檔前檢查（任何一項不過就什麼都不存），訊息與 Server 版相同。
        var dto = ProjectDtoMapper.ToDto(CurrentRecord);
        var actionResult = await Api.SaveAsync(dto, isNewRecordMode, pendingUploadFiles.Select(x => x.Id), removedFileIds);
        Logger.LogInformation("Project save submitted. IsNew={IsNew}, ProjectId={ProjectId}, Files={Files}", isNewRecordMode, CurrentRecord.Id, pendingUploadFiles.Count);

        if (!actionResult.Success)
        {
            ViewNotification.Error(NotificationService, actionResult.Message);

            return false;
        }

        await ClearPendingFilesAsync();
        removedFileIds.Clear();

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
    /// ⚠️ 待上傳／待刪除的檔案只能在**確定要關窗之後**才清掉，
    ///    否則使用者選「繼續編輯」會發現選好的檔案不見了。
    /// </summary>
    private async Task OnModalCancelHandleAsync(MouseEventArgs args)
    {
        modalVisible = true;
        modalVisible = await FormModalFlow.ConfirmCloseAsync(ModalService, dirtyTracker.IsDirty(CurrentRecord));

        if (modalVisible)
        {
            return;
        }

        await ClearPendingFilesAsync();
        removedFileIds.Clear();
        dirtyTracker.Clear();
        Logger.LogDebug("Project modal cancelled.");
    }

    /// <summary>
    /// 待上傳與待刪除檔案的指紋。這些狀態不在 ProjectAdapterModel 裡，
    /// 但使用者會認為「我剛加了一個檔案」就是變更，因此一併納入比對。
    /// </summary>
    private string UploadStateFingerprint()
        => string.Join('|', pendingUploadFiles.Select(x => $"{x.Name}:{x.Size}"))
           + "#"
           + string.Join(',', removedFileIds.OrderBy(x => x));

    public void OnEditContestChanged(EditContext context)
    {
        LocalEditContext = context;
    }

    private async Task RemovePendingFile(string fileId)
    {
        var file = pendingUploadFiles.FirstOrDefault(x => x.Id == fileId);
        if (file is not null)
        {
            pendingUploadFiles.Remove(file);
            await ReleaseQuietlyAsync([file.Id]);
        }
    }

    private void RemoveExistingFile(int fileId)
    {
        var file = CurrentRecord.Files.FirstOrDefault(x => x.Id == fileId);
        if (file is null)
        {
            return;
        }

        removedFileIds.Add(fileId);
        CurrentRecord.Files.Remove(file);
    }

    /// <summary>清掉待上傳清單，瀏覽器那邊留著的檔案一併放掉。</summary>
    private async Task ClearPendingFilesAsync()
    {
        if (pendingUploadFiles.Count == 0)
        {
            return;
        }

        var ids = pendingUploadFiles.Select(x => x.Id).ToList();
        pendingUploadFiles.Clear();
        await ReleaseQuietlyAsync(ids);
    }

    /// <summary>放掉瀏覽器留著的檔案；失敗只影響記憶體（頁面關閉時也會一併釋放），不打擾使用者。</summary>
    private async Task ReleaseQuietlyAsync(IReadOnlyList<string> ids)
    {
        try
        {
            await BrowserFiles.ReleaseAsync(ids);
        }
        catch (JSException ex)
        {
            Logger.LogWarning(ex, "Releasing picked files failed. Count={Count}", ids.Count);
        }
    }

    private static string FormatFileSize(long fileSize)
        => SizeFormatHelper.FormatBytes(fileSize);

    /// <summary>
    /// 清單的狀態徽章語氣。對應表刻意留在呼叫端 ——
    /// <see cref="StatusPill"/> 不該認得「暫緩」這種只有本模組才懂的字。
    /// </summary>
    /// <remarks>
    /// 合法值由 <see cref="ProjectAdapterModel.StatusOptions"/> 界定、服務層把關，
    /// 因此預設分支只會接到舊資料，不是正常路徑。
    /// </remarks>
    private static StatusTone ResolveStatusTone(string status) => status switch
    {
        "已完成" => StatusTone.Positive,
        "進行中" => StatusTone.Accent,
        "暫緩" => StatusTone.Warning,
        "未開始" => StatusTone.Muted,
        _ => StatusTone.Neutral,
    };
}
