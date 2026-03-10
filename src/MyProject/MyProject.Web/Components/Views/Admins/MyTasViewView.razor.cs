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

public partial class MyTasViewView
{
    private readonly ILogger<MyTasViewView> logger;
    private readonly MyTasService myTasService;
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

    private List<MyTasAdapterModel> myTasAdapterModels = [];
    private List<ProjectAdapterModel> projectAdapterModels = [];

    private string modalTitle = "工作列表";
    private bool modalVisible;
    private MyTasAdapterModel CurrentRecord = new();
    public EditContext? LocalEditContext { get; set; }
    private bool isNewRecordMode;
    private string RoleMessage = string.Empty;

    private IReadOnlyList<string> StatusOptions => MyTasAdapterModel.StatusOptions;
    private IReadOnlyList<string> PriorityOptions => MyTasAdapterModel.PriorityOptions;

    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    public AuthenticationStateProvider authStateProvider { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    public MyTasViewView(
        ILogger<MyTasViewView> logger,
        MyTasService myTasService,
        ModalService modalService,
        MessageService messageService,
        NotificationService notificationService)
    {
        this.logger = logger;
        this.myTasService = myTasService;
        this.modalService = modalService;
        this.messageService = messageService;
        this.notificationService = notificationService;
    }

    protected override async Task OnInitializedAsync()
    {
        var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
        if (checkResult == true)
        {
            if (AuthenticationStateHelper.CheckIsAdmin() == false)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            }
            else
            {
                await ReloadAsync();
            }
        }
    }

    public async Task ReloadAsync()
    {
        DataRequestResult<MyTasAdapterModel> dataRequestResult = await myTasService.GetAsync(new DataRequest
        {
            Search = searchText,
            SortField = sortField,
            SortDescending = sortDirection == "Descending" ? true : sortDirection == "Ascending" ? false : (bool?)null,
            CurrentPage = _pageIndex,
            PageSize = _pageSize,
            Take = 0,
        });

        myTasAdapterModels = dataRequestResult.Result.ToList();
        _total = dataRequestResult.Count;

        StateHasChanged();
    }

    private async Task OnTableChange(QueryModel<MyTasAdapterModel> args)
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
        if (string.IsNullOrWhiteSpace(sortModel.FieldName) == false)
        {
            return sortModel.FieldName;
        }

        object? column = sortModel.GetType().GetProperty("Column")?.GetValue(sortModel);
        if (column is null)
        {
            return string.Empty;
        }

        string? columnFieldName = column.GetType().GetProperty("FieldName")?.GetValue(column)?.ToString();
        if (string.IsNullOrWhiteSpace(columnFieldName) == false)
        {
            return columnFieldName;
        }

        object? dataIndex = column.GetType().GetProperty("DataIndex")?.GetValue(column);
        return dataIndex?.ToString() ?? string.Empty;
    }

    private async Task OnSearchAsync()
    {
        _pageIndex = 1;
        await ReloadAsync();
    }

    private async Task OnRefreshAsync()
    {
        await ReloadAsync();

        _ = notificationService.Open(new NotificationConfig
        {
            Message = "系統訊息",
            Description = "已經更新到最新資料庫紀錄",
            NotificationType = NotificationType.Warning,
            Placement = NotificationPlacement.BottomRight
        });
    }

    private async Task OnEditAsync(MyTasAdapterModel myTasAdapterModel)
    {
        await LoadProjectsAsync();

        isNewRecordMode = false;
        modalTitle = "修改工作";
        CurrentRecord = (await myTasService.GetAsync(myTasAdapterModel.Id)).Clone();
        modalVisible = true;
    }

    private async Task OnDeleteAsync(MyTasAdapterModel myTasAdapterModel)
    {
        var beforeDeleteCheckResult = await myTasService.BeforeDeleteCheckAsync(myTasAdapterModel);
        if (beforeDeleteCheckResult.Success == false)
        {
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

        if (ok)
        {
            await myTasService.DeleteAsync(myTasAdapterModel.Id);

            _ = notificationService.Open(new NotificationConfig
            {
                Message = "系統訊息",
                Description = "刪除成功",
                NotificationType = NotificationType.Warning,
                Placement = NotificationPlacement.BottomRight
            });

            await ReloadAsync();
        }
    }

    private async Task OnAddAsync(bool continueOnCapturedContext)
    {
        await LoadProjectsAsync();

        CurrentRecord = new MyTasAdapterModel
        {
            Status = StatusOptions.First(),
            Priority = PriorityOptions[1],
            CompletionPercentage = 0
        };

        if (projectAdapterModels.Any())
        {
            CurrentRecord.ProjectId = projectAdapterModels.First().Id;
        }

        isNewRecordMode = true;
        modalTitle = "新增工作";
        modalVisible = true;
    }

    private async Task OnModalOKHandleAsync(MouseEventArgs args)
    {
        if (LocalEditContext?.Validate() == false)
        {
            IEnumerable<string> allErrors = LocalEditContext.GetValidationMessages();

            foreach (var error in allErrors)
            {
                _ = notificationService.Open(new NotificationConfig
                {
                    Message = "驗證失敗，請修正以下錯誤",
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
            var beforeAddCheckResult = await myTasService.BeforeAddCheckAsync(CurrentRecord);
            if (beforeAddCheckResult.Success == false)
            {
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

            await myTasService.AddAsync(CurrentRecord);

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
            var beforeUpdateCheckResult = await myTasService.BeforeUpdateCheckAsync(CurrentRecord);
            if (beforeUpdateCheckResult.Success == false)
            {
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

            await myTasService.UpdateAsync(CurrentRecord);

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
        projectAdapterModels = await myTasService.GetProjectsAsync();
    }
}
