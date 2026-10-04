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
using MyProject.Web.Auth;
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
        bool isAccessChecked;

        /// <summary>
        /// 未儲存變更偵測。開窗時 Capture、按取消／儲存時比對，
        /// 「改了又改回原值」視為無變更，不會白問使用者一次。
        /// </summary>
        private readonly FormDirtyTracker dirtyTracker = new();

        [Inject]
        public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;
        [Inject]
        public AuthenticationStateProvider authStateProvider { get; set; } = default!;
        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;
        [Inject]
        public IPasswordPolicy PasswordPolicy { get; set; } = default!;
        [Inject]
        public CurrentUserService CurrentUserService { get; set; } = default!;
        [Inject]
        public SessionRefreshNavigator SessionRefreshNavigator { get; set; } = default!;

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

            isAccessChecked = true;

            if (!AuthenticationStateHelper.CheckIsAdmin())
            {
                RoleMessage = "你沒有權限存取此頁面";
                await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/myusers");
                logger.LogWarning("User management view denied because current user is not an administrator.");
                return;
            }

            availableTeams = await teamService.GetAllEnabledNamesAsync();

            await ReloadAsync();
        }

        public async Task ReloadAsync()
        {
            logger.LogDebug(
                "Reloading users. HasSearch={HasSearch}, SearchLength={SearchLength}, SortField={SortField}, SortDirection={SortDirection}, PageIndex={PageIndex}, PageSize={PageSize}",
                string.IsNullOrWhiteSpace(searchText) == false,
                searchText.Length,
                sortField,
                sortDirection,
                _pageIndex,
                _pageSize);

            var dataRequest = new DataRequest
            {
                Search = searchText,
                SortField = sortField,
                SortDescending = sortDirection == "Descending" ? true : sortDirection == "Ascending" ? false : (bool?)null,
                CurrentPage = _pageIndex,
                PageSize = _pageSize,
                Take = 0,
            };

            // 「顯示已刪除」開啟時改讀已刪除的資料（0.9.95 起）。
            DataRequestResult<MyUserAdapterModel> dataRequestResult = showDeleted
                ? await myUserService.GetDeletedAsync(dataRequest)
                : await myUserService.GetAsync(dataRequest);

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
            logger.LogInformation("User search triggered. HasSearch={HasSearch}, SearchLength={SearchLength}", string.IsNullOrWhiteSpace(searchText) == false, searchText.Length);
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

            // ⚠️ 必須是開窗前的最後一步：任何預設值與關聯資料都要先塞完，
            //    否則那些值會被當成「使用者的變更」。
            dirtyTracker.Capture(CurrentRecord);

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

            var ok = await ConfirmDialog.AskSoftDeleteRecordAsync(modalService);

            if (!ok)
            {
                logger.LogDebug("User delete cancelled by user. UserId={UserId}", myUserAdapterModel.Id);
                return;
            }

            var result = await myUserService.DeleteAsync(myUserAdapterModel.Id);
            if (!result.Success)
            {
                // 0.9.93 之前這裡不看結果，失敗也顯示「刪除成功」。
                logger.LogInformation("User delete rejected. UserId={UserId}, Message={Message}", myUserAdapterModel.Id, result.Message);
                ViewNotification.Error(notificationService, result.Message);
                return;
            }

            logger.LogInformation("User delete completed. UserId={UserId}", myUserAdapterModel.Id);

            ViewNotification.Warning(notificationService, "刪除成功");

            await ReloadAsync();
        }

        /// <summary>管理員替使用者輸入新密碼時，自動勾選「下次登入須變更密碼」（仍可取消勾選）。</summary>
        void OnPasswordTyped()
        {
            if (!string.IsNullOrEmpty(CurrentRecord.Password))
            {
                CurrentRecord.MustChangePassword = true;
            }
        }

        static bool IsLocked(MyUserAdapterModel record) => record.LockoutEndUtc is { } end && end > DateTime.UtcNow;

        static string LockedText(MyUserAdapterModel record)
        {
            var local = DateTime.SpecifyKind(record.LockoutEndUtc!.Value, DateTimeKind.Utc).ToLocalTime();
            return local.Date == DateTime.Today ? $"鎖定至 {local:HH:mm}" : $"鎖定至 {local:MM-dd HH:mm}";
        }

        /// <summary>自己那一列不顯示「強制登出」（要登出自己請用右上角的登出）。</summary>
        bool CanForceLogout(MyUserAdapterModel record) => record.Id != CurrentUserService.CurrentUser.Id;

        async Task OnForceLogoutAsync(MyUserAdapterModel record)
        {
            try
            {
                var ok = await ConfirmDialog.AskAsync(modalService, "強制登出", $"要讓「{record.Account}」所有已登入的瀏覽器與 API 都登出嗎？他需要重新登入。", "強制登出");
                if (!ok)
                {
                    return;
                }

                var result = await myUserService.ForceLogoutAsync(record.Id);
                if (!result.Success)
                {
                    ViewNotification.Error(notificationService, result.Message);
                    return;
                }

                logger.LogInformation("User force logout requested from list. UserId={UserId}", record.Id);
                ViewNotification.Warning(notificationService, $"已強制登出「{record.Account}」");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while forcing a user to log out.");
                ViewNotification.Error(notificationService, "強制登出時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
            }
        }

        async Task OnUnlockAsync(MyUserAdapterModel record)
        {
            try
            {
                var ok = await ConfirmDialog.AskAsync(modalService, "解除鎖定", $"要解除「{record.Account}」的登入鎖定嗎？他可以立刻再登入。", "解鎖");
                if (!ok)
                {
                    return;
                }

                var result = await myUserService.UnlockAsync(record.Id);
                if (!result.Success)
                {
                    ViewNotification.Error(notificationService, result.Message);
                }
                else
                {
                    logger.LogInformation("User unlocked from list. UserId={UserId}", record.Id);
                    ViewNotification.Warning(notificationService, $"已解除「{record.Account}」的鎖定");
                }

                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while unlocking user.");
                ViewNotification.Error(notificationService, "解除鎖定時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
            }
        }

        bool showDeleted;

        async Task OnToggleDeletedAsync()
        {
            showDeleted = !showDeleted;
            _pageIndex = 1;
            await ReloadAsync();
        }

        async Task OnRestoreAsync(MyUserAdapterModel record)
        {
            try
            {
                var ok = await ConfirmDialog.AskAsync(modalService, "確認還原", $"要還原「{record.Account}」嗎？", "還原");
                if (!ok)
                {
                    return;
                }

                var result = await myUserService.RestoreAsync(record.Id);
                if (!result.Success)
                {
                    logger.LogInformation("User restore rejected. UserId={UserId}, Message={Message}", record.Id, result.Message);
                    ViewNotification.Error(notificationService, result.Message);
                    return;
                }

                logger.LogInformation("User restore completed. UserId={UserId}", record.Id);
                ViewNotification.Warning(notificationService, "還原成功");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while restoring user.");
                ViewNotification.Error(notificationService, "還原使用者時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
            }
        }

        async Task OnPurgeAsync(MyUserAdapterModel record)
        {
            try
            {
                var ok = await ConfirmDialog.AskDestructiveAsync(
                    modalService,
                    "永久刪除",
                    $"永久刪除「{record.Account}」後無法復原，他的角色與團隊設定也會一併刪除。確定要永久刪除嗎？",
                    "永久刪除");
                if (!ok)
                {
                    return;
                }

                var result = await myUserService.PurgeAsync(record.Id);
                if (!result.Success)
                {
                    logger.LogInformation("User purge rejected. UserId={UserId}, Message={Message}", record.Id, result.Message);
                    ViewNotification.Error(notificationService, result.Message);
                    return;
                }

                logger.LogInformation("User purge completed. UserId={UserId}", record.Id);
                ViewNotification.Warning(notificationService, "已永久刪除");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while purging user.");
                ViewNotification.Error(notificationService, "永久刪除使用者時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
            }
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
            // 新帳號的密碼是管理員設的，預設要求本人第一次登入時換掉（0.9.101 起）。
            CurrentRecord.MustChangePassword = true;

            // ⚠️ 必須是開窗前的最後一步：預設角色已經塞完才拍快照。
            dirtyTracker.Capture(CurrentRecord);

            modalVisible = true;
            logger.LogInformation("Opened create modal for user.");
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
                logger,
                notificationService,
                "儲存使用者時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
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
                    logger.LogInformation("User form validation failed. Error={Error}", error);
                    ViewNotification.ValidationError(notificationService, error);
                }

                return false;
            }

            if (isNewRecordMode && string.IsNullOrWhiteSpace(CurrentRecord.Password))
            {
                logger.LogInformation("User create validation failed because password is empty. Account={Account}", CurrentRecord.Account);
                ViewNotification.ValidationError(notificationService, "新增使用者時必須輸入密碼。");

                return false;
            }

            // 一個字都沒改就按儲存：不值得白寫一筆，也不該白跳一次確認窗。
            if (isNewRecordMode == false && dirtyTracker.IsDirty(CurrentRecord) == false)
            {
                logger.LogDebug("User save skipped because nothing changed. UserId={UserId}", CurrentRecord.Id);
                ViewNotification.Info(notificationService, "沒有任何變更，未進行儲存。");

                return true;
            }

            // 儲存前的二次確認。排在資料庫前置檢查之前：使用者若選「再檢查」，
            // 就不必白跑一次資料庫來回。
            if (await FormEditConfirm.AskSaveAsync(modalService) == false)
            {
                logger.LogDebug("User save cancelled at save confirmation. UserId={UserId}", CurrentRecord.Id);
                return false;
            }

            if (await ConfirmTeamBindingAsync() == false)
            {
                logger.LogDebug("User save cancelled at team confirmation. UserId={UserId}", CurrentRecord.Id);
                return false;
            }

            if (isNewRecordMode)
            {
                var beforeAddCheckResult = await myUserService.BeforeAddCheckAsync(CurrentRecord);
                if (!beforeAddCheckResult.Success)
                {
                    logger.LogInformation("User create pre-check failed. Account={Account}, Message={Message}", CurrentRecord.Account, beforeAddCheckResult.Message);
                    ViewNotification.Error(notificationService, beforeAddCheckResult.Message);

                    return false;
                }

                CurrentRecord.CreateAt = DateTime.Now;
                CurrentRecord.UpdateAt = DateTime.Now;

                var actionResult = await myUserService.AddAsync(CurrentRecord);
                if (!actionResult.Success)
                {
                    // 0.9.92 之前這裡不看結果，失敗也顯示「新增成功」。
                    logger.LogInformation("User create rejected. Account={Account}, Message={Message}", CurrentRecord.Account, actionResult.Message);
                    ViewNotification.Error(notificationService, actionResult.Message);
                    return false;
                }

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

                    return false;
                }

                CurrentRecord.UpdateAt = DateTime.Now;

                var actionResult = await myUserService.UpdateAsync(CurrentRecord);
                if (!actionResult.Success)
                {
                    // 0.9.92 之前這裡不看結果，失敗也顯示「修改成功」；0.9.93 起的並行衝突訊息也走這裡，表單維持開啟。
                    logger.LogInformation("User update rejected. UserId={UserId}, Message={Message}", CurrentRecord.Id, actionResult.Message);
                    ViewNotification.Error(notificationService, actionResult.Message);
                    return false;
                }

                logger.LogInformation("User update submitted. UserId={UserId}, Account={Account}", CurrentRecord.Id, CurrentRecord.Account);

                ViewNotification.Warning(notificationService, "修改成功");

                // 管理員改了自己的密碼、角色或管理員身分：自己的工作階段版本已換掉，換發這台裝置的 Cookie（0.9.103 起）。
                if (await SessionRefreshNavigator.KeepSignedInAsync("/myusers"))
                {
                    return true;
                }
            }

            await ReloadAsync();
            dirtyTracker.Clear();

            return true;
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
                logger.LogDebug("User modal cancelled. UserId={UserId}", CurrentRecord.Id);
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
