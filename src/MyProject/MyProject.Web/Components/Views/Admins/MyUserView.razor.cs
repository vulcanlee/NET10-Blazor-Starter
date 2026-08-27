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

namespace MyProject.Web.Components.Views.Admins
{
    public partial class MyUserView
    {
        private readonly ILogger<MyUserView> logger;
        private readonly MyUserService myUserService;
        private readonly RoleViewService roleViewService;
        private readonly ModalService modalService;
        private readonly MessageService messageService;
        private readonly NotificationService notificationService;
        private readonly TeamService teamService;
        List<string> availableTeams = new();
        ITable? table;
        int _pageIndex = 1;
        int _pageSize = MagicObjectHelper.PageSize;
        int _total = 0;
        string searchText = string.Empty;
        string sortField = string.Empty;
        string sortDirection = "None";

        List<MyUserAdapterModel> myUserAdapterModels = new();
        List<RoleViewAdapterModel> roleViewAdapterModels = new();

        string modalTitle = "使用者維護";
        bool modalVisible = false;
        MyUserAdapterModel CurrentRecord = new();
        public EditContext? LocalEditContext { get; set; }
        bool isNewRecordMode;
        string RoleMessage = string.Empty;

        [Inject]
        public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;
        [Inject]
        public AuthenticationStateProvider authStateProvider { get; set; } = default!;
        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        public MyUserView(
            ILogger<MyUserView> logger,
            MyUserService myUserService,
            RoleViewService roleViewService,
            ModalService modalService,
            MessageService messageService,
            NotificationService notificationService,
            TeamService teamService)
        {
            this.logger = logger;
            this.myUserService = myUserService;
            this.roleViewService = roleViewService;
            this.modalService = modalService;
            this.messageService = messageService;
            this.notificationService = notificationService;
            this.teamService = teamService;
        }

        protected override async Task OnInitializedAsync()
        {
            logger.LogDebug("Initializing user management view.");
            var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
            if (checkResult != AuthenticationCheckResult.Succeeded)
            {
                logger.LogWarning("User management view initialization stopped because authentication check failed.");
                return;
            }

            if (!AuthenticationStateHelper.CheckIsAdmin())
            {
                RoleMessage = "你沒有權限存取此頁面";
                logger.LogWarning("User management view denied because current user is not an administrator.");
                return;
            }

            availableTeams = await teamService.GetAllEnabledNamesAsync();

            await ReloadAsync();
        }

        public async Task ReloadAsync()
        {
            logger.LogDebug(
                "Reloading users. Search={Search}, SortField={SortField}, SortDirection={SortDirection}, PageIndex={PageIndex}, PageSize={PageSize}",
                searchText,
                sortField,
                sortDirection,
                _pageIndex,
                _pageSize);

            DataRequestResult<MyUserAdapterModel> dataRequestResult = await myUserService.GetAsync(new DataRequest
            {
                Search = searchText,
                SortField = sortField,
                SortDescending = sortDirection == "Descending" ? true : sortDirection == "Ascending" ? false : (bool?)null,
                CurrentPage = _pageIndex,
                PageSize = _pageSize,
                Take = 0,
            });

            myUserAdapterModels = dataRequestResult.Result.ToList();
            _total = dataRequestResult.Count;
            logger.LogDebug("User list reloaded successfully. Count={Count}", _total);
            StateHasChanged();
        }

        async Task OnTableChange(QueryModel<MyUserAdapterModel> args)
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

            logger.LogDebug("User table changed. PageIndex={PageIndex}, SortField={SortField}, SortDirection={SortDirection}", _pageIndex, sortField, sortDirection);
            await ReloadAsync();
        }


        async Task OnSearchAsync()
        {
            _pageIndex = 1;
            logger.LogInformation("User search triggered. Search={Search}", searchText);
            await ReloadAsync();
        }

        async Task OnRefreshAsync()
        {
            logger.LogInformation("User refresh triggered.");
            await ReloadAsync();

            ViewNotification.Warning(notificationService, "已更新最新資料");
        }

        async Task OnEditAsync(MyUserAdapterModel myUserAdapterModel)
        {
            await LoadRoleViewsAsync();

            isNewRecordMode = false;
            modalTitle = "修改使用者";
            CurrentRecord = (await myUserService.GetAsync(myUserAdapterModel.Id)).Clone();
            var (additionalRoleIds, teamNames) = await myUserService.GetUserAssignmentsAsync(myUserAdapterModel.Id);
            CurrentRecord.AdditionalRoleIds = additionalRoleIds;
            CurrentRecord.TeamNames = teamNames;
            modalVisible = true;
            logger.LogInformation("Opened edit modal for user. UserId={UserId}, Account={Account}", myUserAdapterModel.Id, myUserAdapterModel.Account);
        }

        /// <summary>
        /// 包住實際邏輯以捕捉未預期的例外：先前這些寫入操作完全沒有 try/catch，
        /// 例外會直接拆掉 Blazor circuit，使用者只看到畫面斷線、日誌上也留不下任何痕跡。
        /// </summary>
        async Task OnDeleteAsync(MyUserAdapterModel myUserAdapterModel)
        {
            try
            {
                await OnDeleteCoreAsync(myUserAdapterModel);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while deleting user.");
                ViewNotification.Error(notificationService, "刪除使用者時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
            }
        }

        async Task OnDeleteCoreAsync(MyUserAdapterModel myUserAdapterModel)
        {
            logger.LogInformation("Delete user requested. UserId={UserId}, Account={Account}", myUserAdapterModel.Id, myUserAdapterModel.Account);

            var ok = await modalService.ConfirmAsync(new ConfirmOptions()
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
                logger.LogDebug("User delete cancelled by user. UserId={UserId}", myUserAdapterModel.Id);
                return;
            }

            await myUserService.DeleteAsync(myUserAdapterModel.Id);
            logger.LogInformation("User delete completed. UserId={UserId}", myUserAdapterModel.Id);

            ViewNotification.Warning(notificationService, "刪除成功");

            await ReloadAsync();
        }

        async Task OnAddAsync(bool continueOnCapturedContext)
        {
            await LoadRoleViewsAsync();

            CurrentRecord = new();
            RoleViewAdapterModel? defaultRole = null;
            try
            {
                defaultRole = await roleViewService.Get預設新建帳號角色Async();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load default role for new user creation.");
            }

            if (defaultRole is not null && defaultRole.Id != 0)
            {
                CurrentRecord.RoleViewId = defaultRole.Id;
            }
            else if (roleViewAdapterModels.Any())
            {
                CurrentRecord.RoleViewId = roleViewAdapterModels.First().Id;
            }

            isNewRecordMode = true;
            modalTitle = "新增使用者";
            modalVisible = true;
            logger.LogInformation("Opened create modal for user.");
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
                logger.LogError(ex, "Unhandled exception while saving user.");
                ViewNotification.Error(notificationService, "儲存使用者時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
            }
        }

        private async Task OnModalOKHandleCoreAsync(MouseEventArgs args)
        {
            if (LocalEditContext?.Validate() == false)
            {
                IEnumerable<string> allErrors = LocalEditContext.GetValidationMessages();

                foreach (var error in allErrors)
                {
                    logger.LogInformation("User form validation failed. Error={Error}", error);
                    ViewNotification.ValidationError(notificationService, error);
                }

                modalVisible = true;
                return;
            }

            if (isNewRecordMode && string.IsNullOrWhiteSpace(CurrentRecord.Password))
            {
                logger.LogInformation("User create validation failed because password is empty. Account={Account}", CurrentRecord.Account);
                ViewNotification.ValidationError(notificationService, "新增使用者時必須輸入密碼。");

                modalVisible = true;
                return;
            }

            if (await ConfirmTeamBindingAsync() == false)
            {
                logger.LogDebug("User save cancelled at team confirmation. UserId={UserId}", CurrentRecord.Id);

                // 保持 Modal 開啟，讓使用者回到原本的編輯內容重新指定團隊。
                modalVisible = true;
                return;
            }

            if (isNewRecordMode)
            {
                var beforeAddCheckResult = await myUserService.BeforeAddCheckAsync(CurrentRecord);
                if (!beforeAddCheckResult.Success)
                {
                    logger.LogInformation("User create pre-check failed. Account={Account}, Message={Message}", CurrentRecord.Account, beforeAddCheckResult.Message);
                    ViewNotification.Error(notificationService, beforeAddCheckResult.Message);

                    modalVisible = true;
                    return;
                }

                CurrentRecord.CreateAt = DateTime.Now;
                CurrentRecord.UpdateAt = DateTime.Now;

                await myUserService.AddAsync(CurrentRecord);
                logger.LogInformation("User create submitted. Account={Account}", CurrentRecord.Account);

                ViewNotification.Warning(notificationService, "新增成功");

                _ = messageService.SuccessAsync("新增成功");
            }
            else
            {
                var beforeUpdateCheckResult = await myUserService.BeforeUpdateCheckAsync(CurrentRecord);
                if (!beforeUpdateCheckResult.Success)
                {
                    logger.LogInformation("User update pre-check failed. UserId={UserId}, Message={Message}", CurrentRecord.Id, beforeUpdateCheckResult.Message);
                    ViewNotification.Error(notificationService, beforeUpdateCheckResult.Message);

                    modalVisible = true;
                    return;
                }

                CurrentRecord.UpdateAt = DateTime.Now;

                await myUserService.UpdateAsync(CurrentRecord);
                logger.LogInformation("User update submitted. UserId={UserId}, Account={Account}", CurrentRecord.Id, CurrentRecord.Account);

                ViewNotification.Warning(notificationService, "修改成功");
            }

            await ReloadAsync();
            modalVisible = false;
        }

        /// <summary>
        /// 儲存前對「團隊欄位留空」提出警告。使用者的有效團隊＝直接綁定的團隊 ∪ 其所有角色的
        /// 預設團隊（見 EffectiveTeamResolver），所以留空不等於沒有團隊 —— 訊息依角色實際有沒有
        /// 預設團隊分成兩種措辭。管理員不受團隊行級過濾，提醒對他沒有意義，直接放行。
        /// 回傳 true 表示可以繼續儲存。
        /// </summary>
        private async Task<bool> ConfirmTeamBindingAsync()
        {
            if (CurrentRecord.TeamNames.Count > 0 || CurrentRecord.IsAdmin)
            {
                return true;
            }

            List<int> roleIds = new(CurrentRecord.AdditionalRoleIds);
            if (CurrentRecord.RoleViewId.HasValue)
            {
                roleIds.Add(CurrentRecord.RoleViewId.Value);
            }

            List<string> inheritedTeams = roleViewAdapterModels
                .Where(x => roleIds.Contains(x.Id))
                .SelectMany(x => x.DefaultTeams)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            string content = inheritedTeams.Count > 0
                ? $"未直接指定團隊，此使用者將沿用其角色的預設團隊（{string.Join("、", inheritedTeams)}）。確定要這樣儲存嗎？"
                : "未直接指定團隊，且其角色也沒有預設團隊，此使用者將只能看到無團隊標記的公開紀錄。確定要這樣儲存嗎？";

            return await TeamBindingConfirm.AskAsync(modalService, content);
        }

        private Task OnModalCancelHandleAsync(MouseEventArgs args)
        {
            modalVisible = false;
            logger.LogDebug("User modal cancelled.");
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

        private void OnAdditionalRolesChanged(IEnumerable<int> values)
        {
            CurrentRecord.AdditionalRoleIds = values?.ToList() ?? new List<int>();
        }

        private void OnUserTeamsChanged(IEnumerable<string> values)
        {
            CurrentRecord.TeamNames = values?.ToList() ?? new List<string>();
        }

        private async Task LoadRoleViewsAsync()
        {
            roleViewAdapterModels = await myUserService.GetRoleViewsAsync();
            logger.LogDebug("Loaded role views for user view. Count={Count}", roleViewAdapterModels.Count);
        }
    }
}
