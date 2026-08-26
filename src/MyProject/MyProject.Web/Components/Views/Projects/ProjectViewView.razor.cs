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

namespace MyProject.Web.Components.Views.Projects;

public partial class ProjectViewView
{
    private readonly ILogger<ProjectViewView> logger;
    private readonly ProjectService projectService;
    private readonly CategoryService categoryService;
    private readonly TeamService teamService;
    private readonly ModalService modalService;
    private readonly MessageService messageService;
    private readonly NotificationService notificationService;
    private ITable? table;

    private List<string> availableCategories = [];
    private List<string> availableTeams = [];
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
    private readonly List<PendingUploadFileItem> pendingUploadFiles = [];
    private readonly HashSet<int> removedFileIds = [];

    private string modalTitle = "專案維護";
    private bool modalVisible;
    private ProjectAdapterModel CurrentRecord = new();
    public EditContext? LocalEditContext { get; set; }
    private bool isNewRecordMode;
    private string RoleMessage = string.Empty;

    private IReadOnlyList<string> StatusOptions => ProjectAdapterModel.StatusOptions;
    private IReadOnlyList<string> PriorityOptions => ProjectAdapterModel.PriorityOptions;

    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    public AuthenticationStateProvider authStateProvider { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    public ProjectViewView(
        ILogger<ProjectViewView> logger,
        ProjectService projectService,
        CategoryService categoryService,
        TeamService teamService,
        ModalService modalService,
        MessageService messageService,
        NotificationService notificationService)
    {
        this.logger = logger;
        this.projectService = projectService;
        this.categoryService = categoryService;
        this.teamService = teamService;
        this.modalService = modalService;
        this.messageService = messageService;
        this.notificationService = notificationService;
    }

    protected override async Task OnInitializedAsync()
    {
        logger.LogDebug("Initializing project management view.");
        var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            logger.LogWarning("Project management view initialization stopped because authentication check failed.");
            return;
        }

        if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_專案項目) == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            logger.LogWarning("Project management view denied because current user has not this role permission.");
            return;
        }

        availableCategories = await categoryService.GetAllEnabledNamesAsync();
        availableTeams = await teamService.GetAllEnabledNamesAsync();

        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        logger.LogDebug(
            "Reloading projects. Search={Search}, SortField={SortField}, SortDirection={SortDirection}, PageIndex={PageIndex}, PageSize={PageSize}",
            searchText,
            sortField,
            sortDirection,
            _pageIndex,
            _pageSize);

        DataRequestResult<ProjectAdapterModel> dataRequestResult = await projectService.GetAsync(new DataRequest
        {
            Search = searchText,
            SortField = sortField,
            SortDescending = sortDirection == "Descending" ? true : sortDirection == "Ascending" ? false : (bool?)null,
            CurrentPage = _pageIndex,
            PageSize = _pageSize,
            Take = 0,
            CategoryFilters = selectedCategoryFilters.ToList(),
            TeamFilters = selectedTeamFilters.ToList(),
        });

        projectAdapterModels = dataRequestResult.Result.ToList();
        _total = dataRequestResult.Count;
        logger.LogDebug("Project list reloaded successfully. Count={Count}", _total);
        StateHasChanged();
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

        logger.LogDebug("Project table changed. PageIndex={PageIndex}, SortField={SortField}, SortDirection={SortDirection}", _pageIndex, sortField, sortDirection);
        await ReloadAsync();
    }


    private async Task OnSearchAsync()
    {
        _pageIndex = 1;
        logger.LogInformation("Project search triggered. Search={Search}", searchText);
        await ReloadAsync();
    }

    private async Task OnRefreshAsync()
    {
        logger.LogInformation("Project refresh triggered.");
        await ReloadAsync();

        ViewNotification.Warning(notificationService, "已更新最新資料");
    }

    private async Task OnEditAsync(ProjectAdapterModel projectAdapterModel)
    {
        isNewRecordMode = false;
        modalTitle = "修改專案";
        // 與其他檢視一致：編輯前一律 Clone() 隔離，避免雙向綁定污染來源資料。
        // 本檢視另有 RemoveExistingFile 會直接 mutate Files 集合，更需要隔離。
        CurrentRecord = (await projectService.GetAsync(projectAdapterModel.Id)).Clone();
        pendingUploadFiles.Clear();
        removedFileIds.Clear();
        BuildModalCategoryOptions();
        modalVisible = true;
        logger.LogInformation("Opened edit modal for project. ProjectId={ProjectId}, Title={Title}", projectAdapterModel.Id, projectAdapterModel.Title);
    }

    /// <summary>
    /// 包住實際邏輯以捕捉未預期的例外：先前這些寫入操作完全沒有 try/catch，
    /// 例外會直接拆掉 Blazor circuit，使用者只看到畫面斷線、日誌上也留不下任何痕跡。
    /// </summary>
    private async Task OnDeleteAsync(ProjectAdapterModel projectAdapterModel)
    {
        try
        {
            await OnDeleteCoreAsync(projectAdapterModel);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception while deleting project.");
            ViewNotification.Error(notificationService, "刪除專案時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
        }
    }

    private async Task OnDeleteCoreAsync(ProjectAdapterModel projectAdapterModel)
    {
        logger.LogInformation("Delete project requested. ProjectId={ProjectId}, Title={Title}", projectAdapterModel.Id, projectAdapterModel.Title);

        var beforeDeleteCheckResult = await projectService.BeforeDeleteCheckAsync(projectAdapterModel);
        if (!beforeDeleteCheckResult.Success)
        {
            logger.LogInformation("Project delete pre-check failed. ProjectId={ProjectId}, Message={Message}", projectAdapterModel.Id, beforeDeleteCheckResult.Message);
            ViewNotification.Error(notificationService, beforeDeleteCheckResult.Message);
            return;
        }

        var ok = await modalService.ConfirmAsync(new ConfirmOptions
        {
            Title = "確認刪除",
            Content = "確定要刪除這筆紀錄嗎？此操作無法復原。",
            OkText = "刪除",
            CancelText = "取消",
            OkButtonProps = new ButtonProps { Danger = true },
            MaskClosable = false
        });

        if (!ok)
        {
            logger.LogDebug("Project delete cancelled by user. ProjectId={ProjectId}", projectAdapterModel.Id);
            return;
        }

        await projectService.DeleteAsync(projectAdapterModel.Id);
        logger.LogInformation("Project delete completed. ProjectId={ProjectId}", projectAdapterModel.Id);

        ViewNotification.Warning(notificationService, "刪除成功");

        await ReloadAsync();
    }

    private Task OnAddAsync(bool continueOnCapturedContext)
    {
        CurrentRecord = new ProjectAdapterModel
        {
            Status = StatusOptions.First(),
            Priority = PriorityOptions[1],
            CompletionPercentage = 0,
            Files = []
        };

        pendingUploadFiles.Clear();
        removedFileIds.Clear();
        BuildModalCategoryOptions();
        isNewRecordMode = true;
        modalTitle = "新增專案";
        modalVisible = true;
        logger.LogInformation("Opened create modal for project.");
        return Task.CompletedTask;
    }

    private async Task OnProjectFilesSelectedAsync(InputFileChangeEventArgs args)
    {
        foreach (var file in args.GetMultipleFiles(1000))
        {
            if (file.Size > ProjectService.MaxUploadFileSize)
            {
                ViewNotification.Error(notificationService, $"{file.Name} 超過 1GB 限制");
                continue;
            }

            pendingUploadFiles.Add(new PendingUploadFileItem
            {
                Id = Guid.NewGuid(),
                File = file
            });
        }

        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// 包住實際邏輯以捕捉未預期的例外：先前這些寫入操作完全沒有 try/catch，
    /// 例外會直接拆掉 Blazor circuit，使用者只看到畫面斷線、日誌上也留不下任何痕跡。
    /// </summary>
    private async Task OnModalOKHandleAsync(MouseEventArgs args)
    {
        try
        {
            await OnModalOKHandleCoreAsync(args);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception while saving project.");
            ViewNotification.Error(notificationService, "儲存專案時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
        }
    }

    private async Task OnModalOKHandleCoreAsync(MouseEventArgs args)
    {
        if (LocalEditContext?.Validate() == false)
        {
            IEnumerable<string> allErrors = LocalEditContext.GetValidationMessages();
            foreach (var error in allErrors)
            {
                logger.LogInformation("Project form validation failed. Error={Error}", error);
                ViewNotification.ValidationError(notificationService, error);
            }

            modalVisible = true;
            return;
        }

        if (CurrentRecord.Teams.Count == 0
            && await TeamBindingConfirm.AskAsync(
                modalService,
                "此專案未指定團隊，將對所有使用者公開可見。確定要這樣儲存嗎？") == false)
        {
            logger.LogDebug("Project save cancelled at team confirmation. ProjectId={ProjectId}", CurrentRecord.Id);

            // 保持 Modal 開啟，讓使用者回到原本的編輯內容重新指定團隊。
            modalVisible = true;
            return;
        }

        var uploadInputs = new List<ProjectUploadFileInput>();
        var uploadStreams = new List<Stream>();

        try
        {
            foreach (var pendingUploadFile in pendingUploadFiles)
            {
                var stream = pendingUploadFile.File.OpenReadStream(ProjectService.MaxUploadFileSize);
                uploadStreams.Add(stream);
                uploadInputs.Add(new ProjectUploadFileInput
                {
                    FileName = pendingUploadFile.File.Name,
                    ContentType = pendingUploadFile.File.ContentType,
                    FileSize = pendingUploadFile.File.Size,
                    Content = stream
                });
            }

            VerifyRecordResult actionResult;

            if (isNewRecordMode)
            {
                var beforeAddCheckResult = await projectService.BeforeAddCheckAsync(CurrentRecord, uploadInputs);
                if (!beforeAddCheckResult.Success)
                {
                    logger.LogInformation("Project create pre-check failed. Title={Title}, Message={Message}", CurrentRecord.Title, beforeAddCheckResult.Message);
                    ViewNotification.Error(notificationService, beforeAddCheckResult.Message);

                    modalVisible = true;
                    return;
                }

                CurrentRecord.CreatedAt = DateTime.Now;
                CurrentRecord.UpdatedAt = DateTime.Now;

                actionResult = await projectService.AddAsync(CurrentRecord, uploadInputs);
                logger.LogInformation("Project create submitted. Title={Title}", CurrentRecord.Title);
            }
            else
            {
                var beforeUpdateCheckResult = await projectService.BeforeUpdateCheckAsync(CurrentRecord, uploadInputs);
                if (!beforeUpdateCheckResult.Success)
                {
                    logger.LogInformation("Project update pre-check failed. ProjectId={ProjectId}, Message={Message}", CurrentRecord.Id, beforeUpdateCheckResult.Message);
                    ViewNotification.Error(notificationService, beforeUpdateCheckResult.Message);

                    modalVisible = true;
                    return;
                }

                CurrentRecord.UpdatedAt = DateTime.Now;
                actionResult = await projectService.UpdateAsync(CurrentRecord, uploadInputs, removedFileIds);
                logger.LogInformation("Project update submitted. ProjectId={ProjectId}, Title={Title}", CurrentRecord.Id, CurrentRecord.Title);
            }

            if (!actionResult.Success)
            {
                ViewNotification.Error(notificationService, actionResult.Message);

                modalVisible = true;
                return;
            }

            pendingUploadFiles.Clear();
            removedFileIds.Clear();

            ViewNotification.Warning(notificationService, isNewRecordMode ? "新增成功" : "修改成功");

            if (isNewRecordMode)
            {
                _ = messageService.SuccessAsync("新增成功");
            }

            await ReloadAsync();
            modalVisible = false;
        }
        finally
        {
            foreach (var uploadStream in uploadStreams)
            {
                uploadStream.Dispose();
            }
        }
    }

    private Task OnModalCancelHandleAsync(MouseEventArgs args)
    {
        modalVisible = false;
        pendingUploadFiles.Clear();
        removedFileIds.Clear();
        logger.LogDebug("Project modal cancelled.");
        return Task.CompletedTask;
    }

    private async Task OnModalKeyDownAsync(KeyboardEventArgs args)
    {
        if (args.Key == "Enter")
        {
            await Task.Delay(200);
            await OnModalOKHandleAsync(new MouseEventArgs());
        }
        else if (args.Key == "Escape" || args.Key == "Esc")
        {
            await OnModalCancelHandleAsync(new MouseEventArgs());
        }
    }

    public void OnEditContestChanged(EditContext context)
    {
        LocalEditContext = context;
    }

    private void RemovePendingFile(Guid fileId)
    {
        var file = pendingUploadFiles.FirstOrDefault(x => x.Id == fileId);
        if (file is not null)
        {
            pendingUploadFiles.Remove(file);
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

    private static string GetProjectFileDownloadUrl(int fileId)
    {
        return $"/api/project-files/{fileId}/download";
    }

    private static string FormatFileSize(long fileSize)
        => SizeFormatHelper.FormatBytes(fileSize);

    private sealed class PendingUploadFileItem
    {
        public Guid Id { get; set; }

        public IBrowserFile File { get; set; } = default!;
    }
}
