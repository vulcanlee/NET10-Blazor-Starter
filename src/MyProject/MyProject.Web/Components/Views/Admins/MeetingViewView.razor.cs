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

namespace MyProject.Web.Components.Views.Admins;

public partial class MeetingViewView
{
    private readonly ILogger<MeetingViewView> logger;
    private readonly MeetingService meetingService;
    private readonly ModalService modalService;
    private readonly MessageService messageService;
    private readonly NotificationService notificationService;
    private ITable? table;
    private int _pageIndex = 1;
    private int _pageSize = MagicObjectHelper.PageSize;
    private int _total;
    private string searchText = string.Empty;
    private string sortField = string.Empty;
    private string sortDirection = "None";

    private List<MeetingAdapterModel> meetingAdapterModels = [];
    private List<ProjectAdapterModel> projectAdapterModels = [];

    private string modalTitle = "會議維護";
    private bool modalVisible;
    private MeetingAdapterModel CurrentRecord = new();
    public EditContext? LocalEditContext { get; set; }
    private bool isNewRecordMode;
    private string RoleMessage = string.Empty;

    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    public AuthenticationStateProvider authStateProvider { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    public MeetingViewView(
        ILogger<MeetingViewView> logger,
        MeetingService meetingService,
        ModalService modalService,
        MessageService messageService,
        NotificationService notificationService)
    {
        this.logger = logger;
        this.meetingService = meetingService;
        this.modalService = modalService;
        this.messageService = messageService;
        this.notificationService = notificationService;
    }

    protected override async Task OnInitializedAsync()
    {
        logger.LogInformation("Initializing meeting management view.");
        var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
        if (!checkResult)
        {
            logger.LogWarning("Meeting management view initialization stopped because authentication check failed.");
            return;
        }

        if (!AuthenticationStateHelper.CheckIsAdmin())
        {
            RoleMessage = "你沒有權限存取此頁面";
            logger.LogWarning("Meeting management view denied because current user is not an administrator.");
            return;
        }

        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        logger.LogDebug(
            "Reloading meetings. Search={Search}, SortField={SortField}, SortDirection={SortDirection}, PageIndex={PageIndex}, PageSize={PageSize}",
            searchText,
            sortField,
            sortDirection,
            _pageIndex,
            _pageSize);

        DataRequestResult<MeetingAdapterModel> dataRequestResult = await meetingService.GetAsync(new DataRequest
        {
            Search = searchText,
            SortField = sortField,
            SortDescending = sortDirection == "Descending" ? true : sortDirection == "Ascending" ? false : (bool?)null,
            CurrentPage = _pageIndex,
            PageSize = _pageSize,
            Take = 0,
        });

        meetingAdapterModels = dataRequestResult.Result.ToList();
        _total = dataRequestResult.Count;
        logger.LogInformation("Meeting list reloaded successfully. Count={Count}", _total);
        StateHasChanged();
    }

    private async Task OnTableChange(QueryModel<MeetingAdapterModel> args)
    {
        _pageIndex = args.PageIndex;

        if (args.SortModel?.Any() == true)
        {
            var tableSortModel = GetCurrentSortModel(args.SortModel);
            sortDirection = tableSortModel.SortDirection.ToString() ?? string.Empty;
            sortField = ResolveSortFieldName(tableSortModel);
        }
        else
        {
            sortField = string.Empty;
            sortDirection = "None";
        }

        logger.LogDebug("Meeting table changed. PageIndex={PageIndex}, SortField={SortField}, SortDirection={SortDirection}", _pageIndex, sortField, sortDirection);
        await ReloadAsync();
    }

    private static ITableSortModel GetCurrentSortModel(IEnumerable<ITableSortModel> sortModels)
    {
        return sortModels.FirstOrDefault(model => HasSortDirection(model.SortDirection))
            ?? sortModels.Last();
    }

    private static bool HasSortDirection(SortDirection sortDirection)
    {
        return sortDirection == SortDirection.Ascending || sortDirection == SortDirection.Descending;
    }

    private static string ResolveSortFieldName(ITableSortModel sortModel)
    {
        if (!string.IsNullOrWhiteSpace(sortModel.FieldName))
        {
            return sortModel.FieldName;
        }

        object? column = sortModel.GetType().GetProperty("Column")?.GetValue(sortModel);
        if (column is null)
        {
            return string.Empty;
        }

        string? columnFieldName = column.GetType().GetProperty("FieldName")?.GetValue(column)?.ToString();
        if (!string.IsNullOrWhiteSpace(columnFieldName))
        {
            return columnFieldName;
        }

        object? dataIndex = column.GetType().GetProperty("DataIndex")?.GetValue(column);
        return dataIndex?.ToString() ?? string.Empty;
    }

    private async Task OnSearchAsync()
    {
        _pageIndex = 1;
        logger.LogInformation("Meeting search triggered. Search={Search}", searchText);
        await ReloadAsync();
    }

    private async Task OnRefreshAsync()
    {
        logger.LogInformation("Meeting refresh triggered.");
        await ReloadAsync();

        _ = notificationService.Open(new NotificationConfig
        {
            Message = "系統訊息",
            Description = "已更新最新資料",
            NotificationType = NotificationType.Warning,
            Placement = NotificationPlacement.BottomRight
        });
    }

    private async Task OnEditAsync(MeetingAdapterModel meetingAdapterModel)
    {
        await LoadProjectsAsync();
        isNewRecordMode = false;
        modalTitle = "修改會議";
        CurrentRecord = (await meetingService.GetAsync(meetingAdapterModel.Id)).Clone();
        modalVisible = true;
        logger.LogInformation("Opened edit modal for meeting. MeetingId={MeetingId}, Title={Title}", meetingAdapterModel.Id, meetingAdapterModel.Title);
    }

    private async Task OnDeleteAsync(MeetingAdapterModel meetingAdapterModel)
    {
        logger.LogInformation("Delete meeting requested. MeetingId={MeetingId}, Title={Title}", meetingAdapterModel.Id, meetingAdapterModel.Title);

        var beforeDeleteCheckResult = await meetingService.BeforeDeleteCheckAsync(meetingAdapterModel);
        if (!beforeDeleteCheckResult.Success)
        {
            logger.LogWarning("Meeting delete pre-check failed. MeetingId={MeetingId}, Message={Message}", meetingAdapterModel.Id, beforeDeleteCheckResult.Message);
            _ = notificationService.Open(new NotificationConfig
            {
                Message = "系統訊息",
                Description = beforeDeleteCheckResult.Message,
                NotificationType = NotificationType.Error,
                Placement = NotificationPlacement.BottomRight
            });
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
            logger.LogDebug("Meeting delete cancelled by user. MeetingId={MeetingId}", meetingAdapterModel.Id);
            return;
        }

        await meetingService.DeleteAsync(meetingAdapterModel.Id);
        logger.LogInformation("Meeting delete completed. MeetingId={MeetingId}", meetingAdapterModel.Id);

        _ = notificationService.Open(new NotificationConfig
        {
            Message = "系統訊息",
            Description = "刪除成功",
            NotificationType = NotificationType.Warning,
            Placement = NotificationPlacement.BottomRight
        });

        await ReloadAsync();
    }

    private async Task OnAddAsync(bool continueOnCapturedContext)
    {
        await LoadProjectsAsync();
        CurrentRecord = new MeetingAdapterModel();

        if (projectAdapterModels.Any())
        {
            CurrentRecord.ProjectId = projectAdapterModels.First().Id;
        }

        isNewRecordMode = true;
        modalTitle = "新增會議";
        modalVisible = true;
        logger.LogInformation("Opened create modal for meeting.");
    }

    private async Task OnModalOKHandleAsync(MouseEventArgs args)
    {
        if (LocalEditContext?.Validate() == false)
        {
            IEnumerable<string> allErrors = LocalEditContext.GetValidationMessages();
            foreach (var error in allErrors)
            {
                logger.LogWarning("Meeting form validation failed. Error={Error}", error);
                _ = notificationService.Open(new NotificationConfig
                {
                    Message = "驗證失敗",
                    Description = error,
                    NotificationType = NotificationType.Error,
                    Placement = NotificationPlacement.BottomRight,
                    Duration = 5
                });
            }

            modalVisible = true;
            return;
        }

        if (isNewRecordMode)
        {
            var beforeAddCheckResult = await meetingService.BeforeAddCheckAsync(CurrentRecord);
            if (!beforeAddCheckResult.Success)
            {
                logger.LogWarning("Meeting create pre-check failed. Title={Title}, Message={Message}", CurrentRecord.Title, beforeAddCheckResult.Message);
                _ = notificationService.Open(new NotificationConfig
                {
                    Message = "系統訊息",
                    Description = beforeAddCheckResult.Message,
                    NotificationType = NotificationType.Error,
                    Placement = NotificationPlacement.BottomRight
                });

                modalVisible = true;
                return;
            }

            CurrentRecord.CreatedAt = DateTime.Now;
            CurrentRecord.UpdatedAt = DateTime.Now;

            await meetingService.AddAsync(CurrentRecord);
            logger.LogInformation("Meeting create submitted. Title={Title}", CurrentRecord.Title);

            _ = notificationService.Open(new NotificationConfig
            {
                Message = "系統訊息",
                Description = "新增成功",
                NotificationType = NotificationType.Warning,
                Placement = NotificationPlacement.BottomRight
            });

            _ = messageService.SuccessAsync("新增成功");
        }
        else
        {
            var beforeUpdateCheckResult = await meetingService.BeforeUpdateCheckAsync(CurrentRecord);
            if (!beforeUpdateCheckResult.Success)
            {
                logger.LogWarning("Meeting update pre-check failed. MeetingId={MeetingId}, Message={Message}", CurrentRecord.Id, beforeUpdateCheckResult.Message);
                _ = notificationService.Open(new NotificationConfig
                {
                    Message = "系統訊息",
                    Description = beforeUpdateCheckResult.Message,
                    NotificationType = NotificationType.Error,
                    Placement = NotificationPlacement.BottomRight
                });

                modalVisible = true;
                return;
            }

            CurrentRecord.UpdatedAt = DateTime.Now;
            await meetingService.UpdateAsync(CurrentRecord);
            logger.LogInformation("Meeting update submitted. MeetingId={MeetingId}, Title={Title}", CurrentRecord.Id, CurrentRecord.Title);

            _ = notificationService.Open(new NotificationConfig
            {
                Message = "系統訊息",
                Description = "修改成功",
                NotificationType = NotificationType.Warning,
                Placement = NotificationPlacement.BottomRight
            });
        }

        await ReloadAsync();
        modalVisible = false;
    }

    private Task OnModalCancelHandleAsync(MouseEventArgs args)
    {
        modalVisible = false;
        logger.LogDebug("Meeting modal cancelled.");
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

    private async Task LoadProjectsAsync()
    {
        projectAdapterModels = await meetingService.GetProjectsAsync();
        logger.LogDebug("Loaded projects for meeting view. Count={Count}", projectAdapterModels.Count);
    }
}
