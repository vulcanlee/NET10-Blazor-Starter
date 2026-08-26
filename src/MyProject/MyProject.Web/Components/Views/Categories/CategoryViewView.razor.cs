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

namespace MyProject.Web.Components.Views.Categories
{
    public partial class CategoryViewView
    {
        private readonly ILogger<CategoryViewView> logger;
        private readonly CategoryService categoryService;
        private readonly TeamService teamService;
        private readonly IRecordAccessScopeProvider accessScope;
        private readonly ModalService modalService;
        private readonly MessageService messageService;
        private readonly NotificationService notificationService;
        ITable? table;
        int _pageIndex = 1;
        int _pageSize = MagicObjectHelper.PageSize;
        int _total = 0;
        string searchText = string.Empty;
        string sortField = string.Empty;
        string sortDirection = "None";

        List<CategoryAdapterModel> categoryAdapterModels = new();
        List<string> availableTeams = new();

        /// <summary>目前使用者的存取範圍，用於判斷「存檔後自己就看不到這筆」的提醒。</summary>
        RecordAccessScope currentScope = new(false, []);

        string modalTitle = "分類維護";
        bool modalVisible = false;
        CategoryAdapterModel CurrentRecord = new();
        public EditContext? LocalEditContext { get; set; }
        bool isNewRecordMode;
        string RoleMessage = string.Empty;

        [Inject]
        public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;
        [Inject]
        public AuthenticationStateProvider authStateProvider { get; set; } = default!;
        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        public CategoryViewView(
            ILogger<CategoryViewView> logger,
            CategoryService categoryService,
            TeamService teamService,
            IRecordAccessScopeProvider accessScope,
            ModalService modalService,
            MessageService messageService,
            NotificationService notificationService)
        {
            this.logger = logger;
            this.categoryService = categoryService;
            this.teamService = teamService;
            this.accessScope = accessScope;
            this.modalService = modalService;
            this.messageService = messageService;
            this.notificationService = notificationService;
        }

        protected override async Task OnInitializedAsync()
        {
            logger.LogDebug("Initializing category management view.");
            var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
            if (checkResult != AuthenticationCheckResult.Succeeded)
            {
                logger.LogWarning("Category view initialization stopped because authentication check failed.");
                return;
            }

            if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_分類清單) == false)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                logger.LogWarning("Category view denied because current user has not this role permission.");
                return;
            }

            availableTeams = await teamService.GetAllEnabledNamesAsync();
            currentScope = await accessScope.GetAsync();

            await ReloadAsync();
        }

        public async Task ReloadAsync()
        {
            logger.LogDebug(
                "Reloading categories. Search={Search}, SortField={SortField}, SortDirection={SortDirection}, PageIndex={PageIndex}, PageSize={PageSize}",
                searchText,
                sortField,
                sortDirection,
                _pageIndex,
                _pageSize);

            DataRequestResult<CategoryAdapterModel> dataRequestResult = await categoryService.GetAsync(new DataRequest
            {
                Search = searchText,
                SortField = sortField,
                SortDescending = sortDirection == "Descending" ? true : sortDirection == "Ascending" ? false : (bool?)null,
                CurrentPage = _pageIndex,
                PageSize = _pageSize,
                Take = 0,
            });

            categoryAdapterModels = dataRequestResult.Result.ToList();
            _total = dataRequestResult.Count;
            logger.LogDebug("Category list reloaded successfully. Count={Count}", _total);
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

            logger.LogDebug("Category table changed. PageIndex={PageIndex}, SortField={SortField}, SortDirection={SortDirection}", _pageIndex, sortField, sortDirection);
            await ReloadAsync();
        }


        async Task OnSearchAsync()
        {
            _pageIndex = 1;
            logger.LogInformation("Category search triggered. Search={Search}", searchText);
            await ReloadAsync();
        }

        async Task OnRefreshAsync()
        {
            logger.LogInformation("Category refresh triggered.");
            await ReloadAsync();

            ViewNotification.Warning(notificationService, "已更新最新資料");
        }

        /// <summary>
        /// 一律指派新清單：CurrentRecord 是 MemberwiseClone 出來的，
        /// 直接異動原清單會連帶改到表格上那一列的資料。
        /// </summary>
        void OnRecordTeamsChanged(IEnumerable<string> values)
        {
            CurrentRecord.Teams = values?.ToList() ?? new List<string>();
        }

        async Task OnEditAsync(CategoryAdapterModel categoryAdapterModel)
        {
            isNewRecordMode = false;
            modalTitle = "修改分類";
            CurrentRecord = categoryAdapterModel.Clone();
            modalVisible = true;
            logger.LogInformation("Opened edit modal for category. CategoryId={CategoryId}, Name={Name}", categoryAdapterModel.Id, categoryAdapterModel.Name);
        }

        /// <summary>
        /// 包住實際邏輯以捕捉未預期的例外：先前這些寫入操作完全沒有 try/catch，
        /// 例外會直接拆掉 Blazor circuit，使用者只看到畫面斷線、日誌上也留不下任何痕跡。
        /// </summary>
        async Task OnDeleteAsync(CategoryAdapterModel categoryAdapterModel)
        {
            try
            {
                await OnDeleteCoreAsync(categoryAdapterModel);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while deleting category.");
                ViewNotification.Error(notificationService, "刪除分類時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
            }
        }

        async Task OnDeleteCoreAsync(CategoryAdapterModel categoryAdapterModel)
        {
            logger.LogInformation("Delete category requested. CategoryId={CategoryId}, Name={Name}", categoryAdapterModel.Id, categoryAdapterModel.Name);

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
                logger.LogDebug("Category delete cancelled by user. CategoryId={CategoryId}", categoryAdapterModel.Id);
                return;
            }

            await categoryService.DeleteAsync(categoryAdapterModel.Id);
            logger.LogInformation("Category delete completed. CategoryId={CategoryId}", categoryAdapterModel.Id);

            ViewNotification.Warning(notificationService, "刪除成功");

            await ReloadAsync();
        }

        async Task OnAddAsync()
        {
            CurrentRecord = new();
            isNewRecordMode = true;
            modalTitle = "新增分類";
            modalVisible = true;
            logger.LogInformation("Opened create modal for category.");
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
                logger.LogError(ex, "Unhandled exception while saving category.");
                ViewNotification.Error(notificationService, "儲存分類時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
            }
        }

        private async Task OnModalOKHandleCoreAsync(MouseEventArgs args)
        {
            if (LocalEditContext?.Validate() == false)
            {
                IEnumerable<string> allErrors = LocalEditContext.GetValidationMessages();
                foreach (var error in allErrors)
                {
                    logger.LogInformation("Category form validation failed. Error={Error}", error);
                    ViewNotification.ValidationError(notificationService, error);
                }

                modalVisible = true;
                return;
            }

            // 名稱重複檢查排在團隊確認對話窗之前：這個檢查便宜、且失敗時必定不能儲存，
            // 沒有理由讓使用者先回答一個對話窗才被告知名稱重複。
            if (isNewRecordMode)
            {
                var beforeAddCheckResult = await categoryService.BeforeAddCheckAsync(CurrentRecord);
                if (!beforeAddCheckResult.Success)
                {
                    logger.LogInformation("Category create pre-check failed. Name={Name}, Message={Message}", CurrentRecord.Name, beforeAddCheckResult.Message);
                    ViewNotification.Error(notificationService, beforeAddCheckResult.Message);

                    modalVisible = true;
                    return;
                }
            }
            else
            {
                var beforeUpdateCheckResult = await categoryService.BeforeUpdateCheckAsync(CurrentRecord);
                if (!beforeUpdateCheckResult.Success)
                {
                    logger.LogInformation("Category update pre-check failed. CategoryId={CategoryId}, Message={Message}", CurrentRecord.Id, beforeUpdateCheckResult.Message);
                    ViewNotification.Error(notificationService, beforeUpdateCheckResult.Message);

                    modalVisible = true;
                    return;
                }
            }

            if (await ConfirmTeamBindingAsync() == false)
            {
                logger.LogDebug("Category save cancelled at team confirmation. CategoryId={CategoryId}", CurrentRecord.Id);

                // 保持 Modal 開啟，讓使用者回到原本的編輯內容重新指定團隊。
                modalVisible = true;
                return;
            }

            VerifyRecordResult actionResult;

            if (isNewRecordMode)
            {
                CurrentRecord.CreatedAt = DateTime.Now;
                CurrentRecord.UpdatedAt = DateTime.Now;

                actionResult = await categoryService.AddAsync(CurrentRecord);
                logger.LogInformation("Category create submitted. Name={Name}", CurrentRecord.Name);
            }
            else
            {
                CurrentRecord.UpdatedAt = DateTime.Now;
                actionResult = await categoryService.UpdateAsync(CurrentRecord);
                logger.LogInformation("Category update submitted. CategoryId={CategoryId}, Name={Name}", CurrentRecord.Id, CurrentRecord.Name);
            }

            // 前置檢查通過不代表寫得進去：唯一索引在並發時仍會擋下，
            // 忽略這個回傳值會讓失敗的儲存顯示成「新增成功」。
            if (!actionResult.Success)
            {
                ViewNotification.Error(notificationService, actionResult.Message);

                modalVisible = true;
                return;
            }

            ViewNotification.Warning(notificationService, isNewRecordMode ? "新增成功" : "修改成功");

            if (isNewRecordMode)
            {
                _ = messageService.SuccessAsync("新增成功");
            }

            await ReloadAsync();
            modalVisible = false;
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
                    modalService,
                    "此分類未指定適用團隊，將出現在所有使用者的分類下拉清單中（視同公用分類）。確定要這樣儲存嗎？");
            }

            if (currentScope.IsAdmin == false
                && currentScope.Teams.Count > 0
                && CurrentRecord.Teams.Any(x => currentScope.Teams.Any(
                    team => string.Equals(x, team, StringComparison.OrdinalIgnoreCase))) == false)
            {
                return await TeamBindingConfirm.AskAsync(
                    modalService,
                    "此分類指定的團隊不包含你所屬的任何團隊，儲存後你將無法在分類清單中看到這筆資料。確定要這樣儲存嗎？");
            }

            return true;
        }

        private Task OnModalCancelHandleAsync(MouseEventArgs args)
        {
            modalVisible = false;
            logger.LogDebug("Category modal cancelled.");
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
    }
}
