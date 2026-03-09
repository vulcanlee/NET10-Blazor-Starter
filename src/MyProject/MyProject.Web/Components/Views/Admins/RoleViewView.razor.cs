using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;

namespace MyProject.Web.Components.Views.Admins
{
    public partial class RoleViewView
    {
        private readonly ILogger<RoleViewView> logger;
        private readonly RoleViewService roleViewService;
        private readonly ModalService modalService;
        private readonly MessageService messageService;
        private readonly NotificationService notificationService;
        private readonly RolePermissionService rolePermissionService;
        ITable table;
        int _pageIndex = 1;
        int _pageSize = MagicObjectHelper.PageSize;
        int _total = 0;
        string searchText = string.Empty;
        string sortField = string.Empty;
        string sortDirection = "None";

        List<RoleViewAdapterModel> roleViewAdapterModels = new();

        string modalTitle = "角色列表";
        bool modalVisible = false;
        RoleViewAdapterModel CurrentRecord = new();
        public EditContext LocalEditContext { get; set; }
        bool isNewRecordMode;

        public RoleViewView(ILogger<RoleViewView> logger,
            RoleViewService roleViewService,
            ModalService modalService, MessageService messageService, NotificationService notificationService,
            RolePermissionService rolePermissionService)
        {
            this.logger = logger;
            this.roleViewService = roleViewService;
            this.modalService = modalService;
            this.messageService = messageService;
            this.notificationService = notificationService;
            this.rolePermissionService = rolePermissionService;
        }

        public async Task ReloadAsync()
        {
            DataRequestResult<RoleViewAdapterModel> dataRequestResult = await roleViewService.GetAsync(new DataRequest
            {
                Search = searchText,
                SortField = sortField,
                SortDescending = sortDirection == "Descending"? true : sortDirection == "Ascending" ? false : (bool?)null ,
                CurrentPage = _pageIndex,
                PageSize = _pageSize,
                Take = 0,
            });

            roleViewAdapterModels = dataRequestResult.Result.ToList();
            _total = dataRequestResult.Count;

            StateHasChanged();
        }

        async Task OnTableChange(AntDesign.TableModels.QueryModel<RoleViewAdapterModel> args)
        {
            _pageIndex = args.PageIndex;

            if (args.SortModel?.Any() == true)
            {
                var tableSortModel = GetCurrentSortModel(args.SortModel);
                string sortValue = tableSortModel.SortDirection.ToString() ?? string.Empty;
                string resolvedSortField = ResolveSortFieldName(tableSortModel);
                sortDirection = sortValue;
                sortField = resolvedSortField;

                //bool isDesc = sortValue.Equals("descend", StringComparison.OrdinalIgnoreCase)
                //      || sortValue.Equals("descending", StringComparison.OrdinalIgnoreCase)
                //      || sortValue.Equals("desc", StringComparison.OrdinalIgnoreCase);
                //bool isAsc = sortValue.Equals("ascend", StringComparison.OrdinalIgnoreCase)
                //    || sortValue.Equals("ascending", StringComparison.OrdinalIgnoreCase)
                //    || sortValue.Equals("asc", StringComparison.OrdinalIgnoreCase);

                //if (isDesc)
                //{
                //    sortDirection = "Descending";
                //    sortField = resolvedSortField;
                //}
                //else if (isAsc)
                //{
                //    sortDirection = "Ascending";
                //    sortField = resolvedSortField;
                //}
                //else
                //{
                //    sortDirection = "None";
                //    sortField = resolvedSortField;
                //}
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

        async Task OnSearchAsync()
        {
            _pageIndex = 1;
            await ReloadAsync();
        }

        async Task OnRefreshAsync()
        {
            await ReloadAsync();

            _ = notificationService.Open(new NotificationConfig()
            {
                Message = "系統訊息",
                Description = "已經更新到最新資料庫紀錄",
                NotificationType = NotificationType.Warning,
                Placement = NotificationPlacement.BottomRight
            });

        }

        async Task OnEditAsync(RoleViewAdapterModel roleViewAdapterModel)
        {
            isNewRecordMode = false;
            CurrentRecord = roleViewAdapterModel.Clone();
            modalVisible = true;
        }

        async Task OnDeleteAsync(RoleViewAdapterModel roleViewAdapterModel)
        {
            var ok = await modalService.ConfirmAsync(new ConfirmOptions()
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
                await roleViewService.DeleteAsync(roleViewAdapterModel.Id);

                _ = notificationService.Open(new NotificationConfig()
                {
                    Message = "系統訊息",
                    Description = "刪除成功",
                    NotificationType = NotificationType.Warning,
                    Placement = NotificationPlacement.BottomRight
                });

                await ReloadAsync();
            }
            else
            {
                // 使用者按「取消」或關閉
            }
        }

        async Task OnAddAsync(bool continueOnCapturedContext)
        {
            CurrentRecord = new();

            #region 針對新增的紀錄所要做的初始值設定商業邏輯
            CurrentRecord.RolePermission = rolePermissionService
                .InitializePermissionSetting();
            #endregion

            isNewRecordMode = true;
            modalVisible = true;
        }

        private async Task OnModalOKHandleAsync(Microsoft.AspNetCore.Components.Web.MouseEventArgs args)
        {
            #region 進行 Form Validation 檢查驗證作業
            if (LocalEditContext.Validate() == false)
            {
                // 取得所有驗證失敗的錯誤訊息
                IEnumerable<string> allErrors = LocalEditContext.GetValidationMessages();

                // 取得指定欄位的錯誤訊息
                // IEnumerable<string> fieldErrors = LocalEditContext
                //     .GetValidationMessages(LocalEditContext.Field(nameof(CurrentRecord.Name)));

                //string errorDescription = string.Join(Environment.NewLine, allErrors);

                foreach (var error in allErrors)
                {
                    _ = notificationService.Open(new NotificationConfig()
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
            #endregion

            #region 新增與修改儲存紀錄
            if (isNewRecordMode == true)
            {
                #region 新增紀錄

                CurrentRecord.CreateAt = DateTime.Now;
                CurrentRecord.UpdateAt = DateTime.Now;

                await roleViewService.AddAsync(CurrentRecord);

                _ = notificationService.Open(new NotificationConfig()
                {
                    Message = "系統訊息",
                    Description = "新增成功",
                    NotificationType = NotificationType.Warning,
                    Placement = NotificationPlacement.BottomRight
                });

                _ = messageService.SuccessAsync("新增成功");

                //await modalService.InfoAsync(new ConfirmOptions()
                //{
                //    Title = "系統訊息",
                //    Content = "新增成功",
                //});

                await ReloadAsync();

                modalVisible = false;

                #endregion
            }
            else
            {
                #region 修改紀錄
                CurrentRecord.UpdateAt = DateTime.Now;

                await roleViewService.UpdateAsync(CurrentRecord);

                _ = notificationService.Open(new NotificationConfig()
                {
                    Message = "系統訊息",
                    Description = "新增成功",
                    NotificationType = NotificationType.Warning,
                    Placement = NotificationPlacement.BottomRight
                });

                await ReloadAsync();

                modalVisible = false;
                #endregion
            }
            #endregion

            modalVisible = false;
        }

        private async Task OnModalCancelHandleAsync(Microsoft.AspNetCore.Components.Web.MouseEventArgs args)
        {
            modalVisible = false;
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
    }
}
