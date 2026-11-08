using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Components.Commons;
using MyProject.Business.Helpers;
using MyProject.Web.Export;
using MyProject.Web.Diagnostics;

namespace MyProject.Web.Components.Views.Admins
{
    public partial class AuditLogView
    {
        /// <summary>
        /// 「清除很久以前的紀錄」門檻，讀 <see cref="LogRetentionSettings.AuditLogDays"/>（0.9.78 起與自動清理同一個設定，LOG-13）。
        /// 停用自動清理（0）時退回預設 365 天。
        ///
        /// 預設 365 天而非例外紀錄的 90 天：例外紀錄清的是噪音，
        /// 稽核軌跡清的是責任證據，保存期預期以「年」為單位。
        /// </summary>
        private int PurgeDays => RetentionOptions.CurrentValue.ManualAuditLogDays;

        /// <summary>瀏覽類（換頁、搜尋、查看）的清除門檻（0.9.174 起），讀 <see cref="LogRetentionSettings.AuditBrowsingLogDays"/>。</summary>
        private int BrowsingPurgeDays => RetentionOptions.CurrentValue.ManualAuditBrowsingLogDays;

        [Inject]
        public Microsoft.Extensions.Options.IOptionsMonitor<LogRetentionSettings> RetentionOptions { get; set; } = default!;

        [Inject]
        public Microsoft.Extensions.Options.IOptionsMonitor<AuditLogSettings> ScopeOptions { get; set; } = default!;

        /// <summary>目前沒有記錄的範圍（0.9.174 起，兩個開關預設都關）；都有記錄時為 null。</summary>
        private string? ScopeNotice
        {
            get
            {
                var scope = ScopeOptions.CurrentValue;
                var off = new List<string>();
                if (scope.RecordFieldChanges == false)
                {
                    off.Add("修改前後差異");
                }

                if (scope.RecordBrowsing == false)
                {
                    off.Add("瀏覽類事件（換頁、搜尋、查看）");
                }

                return off.Count == 0
                    ? null
                    : $"目前沒有記錄{string.Join("與", off)}，可在「系統參數」頁開啟。";
            }
        }

        private readonly ILogger<AuditLogView> logger;
        private readonly AuditLogQueryService auditLogQueryService;
        private readonly IAuditLogService auditLogService;
        private readonly CurrentUserService currentUserService;
        private readonly ModalService modalService;
        private readonly NotificationService notificationService;

        private ITable? table;

        private DateTime? startTime;
        private DateTime? endTime;
        private string selectedAction = string.Empty;
        private string selectedSuccess = string.Empty;
        private string accountFilter = string.Empty;
        private string ipFilter = string.Empty;
        private string keyword = string.Empty;
        private string selectedTargetType = string.Empty;
        private string targetIdFilter = string.Empty;

        private List<string> actionOptions = [];
        private List<string> targetTypeOptions = [];

        private int _pageIndex = 1;
        private int _pageSize = MagicObjectHelper.PageSize;
        private int _total;
        private string sortField = string.Empty;
        private string sortDirection = "None";

        private bool isLoading;
        private string RoleMessage = string.Empty;
        private bool isAccessChecked;

        private List<AuditLogAdapterModel> auditLogAdapterModels = [];

        private bool detailVisible;
        private AuditLogAdapterModel? detailItem;
        /// <summary>明細窗的「詳細內容」（修改差異、查詢條件；0.9.131 起）。null 表示這筆沒有內容檔。</summary>
        private string? detailContent;
        private bool isContentLoading;

        [Inject]
        public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

        [Inject]
        public AuthenticationStateProvider authStateProvider { get; set; } = default!;

        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        [Inject]
        public IJSRuntime JSRuntime { get; set; } = default!;

        public AuditLogView(
            ILogger<AuditLogView> logger,
            AuditLogQueryService auditLogQueryService,
            IAuditLogService auditLogService,
            CurrentUserService currentUserService,
            ModalService modalService,
            NotificationService notificationService)
        {
            this.logger = logger;
            this.auditLogQueryService = auditLogQueryService;
            this.auditLogService = auditLogService;
            this.currentUserService = currentUserService;
            this.modalService = modalService;
            this.notificationService = notificationService;
        }

        protected override async Task OnInitializedAsync()
        {
            var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
            if (checkResult != AuthenticationCheckResult.Succeeded)
            {
                return;
            }

            isAccessChecked = true;

            // 此頁為管理員專屬：權限鍵刻意未上架角色矩陣，因此以 CheckIsAdmin 直接判斷，
            // 與同子功能表的其他頁面一致。權限未通過前不讀取任何稽核內容。
            if (AuthenticationStateHelper.CheckIsAdmin() == false)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/audit-logs");
                logger.LogWarning("Audit log view denied because the current user is not an administrator.");
                return;
            }

            await ReloadActionOptionsAsync();
            await ReloadAsync();
        }

        /// <summary>
        /// 重新載入動作代碼下拉選項。清除／清空之後也要重跑：
        /// 某個代碼的最後一筆被刪掉時，選項就該跟著消失，否則會留下「選了查無資料」的死選項。
        /// </summary>
        private async Task ReloadActionOptionsAsync()
        {
            try
            {
                actionOptions = await auditLogQueryService.GetDistinctActionsAsync();

                // 目前選取的代碼若已不存在，退回「不限」，避免畫面停在查不到東西的狀態。
                if (string.IsNullOrEmpty(selectedAction) == false
                    && actionOptions.Contains(selectedAction) == false)
                {
                    selectedAction = string.Empty;
                }

                // 目標類型（0.9.173 起）同理。
                targetTypeOptions = await auditLogQueryService.GetDistinctTargetTypesAsync();
                if (string.IsNullOrEmpty(selectedTargetType) == false
                    && targetTypeOptions.Contains(selectedTargetType) == false)
                {
                    selectedTargetType = string.Empty;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load audit log action options.");
                actionOptions = [];
                targetTypeOptions = [];
            }
        }

        private async Task ReloadAsync()
        {
            isLoading = true;
            StateHasChanged();

            try
            {
                var query = BuildQuery(_pageIndex, _pageSize);

                var result = await auditLogQueryService.GetAsync(query);
                auditLogAdapterModels = result.Result.ToList();
                _total = result.Count;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load audit logs.");
                ViewNotification.UnexpectedError(notificationService, $"載入稽核紀錄失敗：{ex.GetType().Name}。", ex);
                auditLogAdapterModels = [];
                _total = 0;
            }
            finally
            {
                isLoading = false;
                StateHasChanged();
            }
        }

        /// <summary>
        /// 由目前的工具列狀態組出查詢條件。清單與匯出共用，避免兩邊條件默默分歧
        /// （匯出的是「目前畫面條件下的全部資料」，只有分頁不同）。
        /// </summary>
        private AuditLogQuery BuildQuery(int currentPage, int pageSize) => new()
        {
            StartTime = startTime,
            EndTime = endTime,
            Account = accountFilter,
            ClientIp = ipFilter,
            Action = selectedAction,
            Success = selectedSuccess switch
            {
                "true" => true,
                "false" => false,
                _ => null,
            },
            Keyword = keyword,
            TargetType = selectedTargetType,
            TargetId = targetIdFilter,
            CurrentPage = currentPage,
            PageSize = pageSize,
            SortField = sortField,
            SortDescending = sortDirection switch
            {
                "descend" => true,
                "ascend" => false,
                _ => null,
            },
        };

        private async Task OnTableChange(QueryModel<AuditLogAdapterModel> args)
        {
            _pageIndex = args.PageIndex;
            _pageSize = args.PageSize;

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

            await ReloadAsync();
        }

        private async Task OnSearchAsync()
        {
            _pageIndex = 1;
            logger.LogInformation("Audit log search triggered.");
            await ReloadAsync();
            await ViewAudit.WriteSearchAsync(auditLogService, currentUserService, AuditActions.Audit.Search, "AuditLog",
                AuditSearchCondition.Of("start", startTime), AuditSearchCondition.Of("end", endTime),
                AuditSearchCondition.Of("action", selectedAction), AuditSearchCondition.Of("success", selectedSuccess),
                AuditSearchCondition.Text("account", accountFilter), AuditSearchCondition.Text("ip", ipFilter),
                AuditSearchCondition.Text("keyword", keyword),
                AuditSearchCondition.Of("targetType", selectedTargetType), AuditSearchCondition.Text("targetId", targetIdFilter));
        }

        private async Task OnRefreshAsync()
        {
            logger.LogInformation("Audit log refresh triggered.");
            await ReloadActionOptionsAsync();
            await ReloadAsync();
        }

        /// <summary>
        /// 先開窗、再讀詳細內容檔（0.9.131 起）：清單查詢不讀檔，檔案可能很大，只在看明細時才讀。
        /// </summary>
        private async Task OnViewAsync(AuditLogAdapterModel item)
        {
            detailItem = item;
            detailContent = null;
            isContentLoading = item.ContentFile is not null;
            detailVisible = true;
            StateHasChanged();

            if (item.ContentFile is not null)
            {
                try
                {
                    detailContent = await auditLogQueryService.GetContentAsync(item.Id)
                        ?? "內容檔不存在（可能已被清除，或當初寫檔失敗）。";
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to read audit content file. AuditLogId={AuditLogId}", item.Id);
                    detailContent = "讀取詳細內容失敗。" + TraceCode.Suffix(TraceCode.Current);
                }
                finally
                {
                    isContentLoading = false;
                }
            }

            await ViewAudit.WriteAsync(auditLogService, currentUserService, AuditActions.Audit.View, "AuditLog", item.Id.ToString(), $"action={item.Action}");
        }

        private async Task OnPurgeAsync()
        {
            // 0.9.174 起兩類各用自己的保留天數：異動與安全事件 PurgeDays、瀏覽類（換頁、搜尋、查看）BrowsingPurgeDays。
            var confirmed = await ConfirmDialog.AskDestructiveAsync(
                modalService,
                "清除超過保留天數的紀錄",
                $"將刪除早於 {PurgeDays} 天前的異動與安全紀錄，以及早於 {BrowsingPurgeDays} 天前的瀏覽紀錄（換頁、搜尋、查看）。"
                    + "此動作無法復原，且本次清除會另外留下稽核紀錄。",
                "清除");

            if (confirmed == false)
            {
                return;
            }

            var change = await auditLogQueryService.PurgeAsync(PurgeDays, AuditLogKind.Change);
            var browsing = change.Success
                ? await auditLogQueryService.PurgeAsync(BrowsingPurgeDays, AuditLogKind.Browsing)
                : change;
            if (change.Success)
            {
                await WriteSelfAuditAsync(AuditActions.Audit.Purge, $"清除 {PurgeDays} 天前的異動與安全紀錄：{change.Message}");
            }

            if (change.Success && browsing.Success)
            {
                await WriteSelfAuditAsync(AuditActions.Audit.Purge, $"清除 {BrowsingPurgeDays} 天前的瀏覽紀錄：{browsing.Message}");

                // 兩類都清過，才用較舊的門檻收空月份目錄與孤兒檔（手動天數一定大於 0，所以一定安全）。
                auditLogQueryService.DeleteContentMonthsBefore(DateTime.UtcNow.AddDays(-Math.Max(PurgeDays, BrowsingPurgeDays)));
                ViewNotification.Warning(notificationService, $"異動與安全紀錄：{change.Message}瀏覽紀錄：{browsing.Message}");
            }
            else
            {
                ViewNotification.Error(notificationService, browsing.Message);
            }

            // 只要有一段刪到資料，清單就要重新整理，不能留著已刪掉的列。
            if (change.Success)
            {
                await ReloadActionOptionsAsync();
                await ReloadAsync();
            }
        }

        private async Task OnClearAllAsync()
        {
            var confirmed = await ConfirmDialog.AskDestructiveAsync(
                modalService,
                "清空全部稽核紀錄",
                "將刪除所有稽核紀錄。此動作無法復原，且本次清空會另外留下一筆稽核紀錄。",
                "清空");

            if (confirmed == false)
            {
                return;
            }

            var result = await auditLogQueryService.ClearAllAsync();
            if (result.Success)
            {
                await WriteSelfAuditAsync(AuditActions.Audit.ClearAll, $"清空全部稽核紀錄：{result.Message}");
                ViewNotification.Warning(notificationService, "已清空全部稽核紀錄。");
                await ReloadActionOptionsAsync();
                await ReloadAsync();
            }
            else
            {
                ViewNotification.Error(notificationService, result.Message);
            }
        }

        /// <summary>
        /// 把「清除／清空稽核紀錄」這件事本身寫成一筆稽核紀錄。
        ///
        /// ⚠️ 這是刻意的：稽核軌跡可被管理員一鍵抹除，會讓「誰抹除了稽核紀錄」成為
        /// 系統裡唯一查不到的事。寫入排在刪除之後，所以這一筆會留在清空後的資料表裡。
        ///
        /// 稽核寫入失敗不應推翻「已經刪除」這個事實，因此只記錯誤、不向使用者報錯。
        /// </summary>
        private async Task WriteSelfAuditAsync(string action, string detail)
        {
            try
            {
                var currentUser = currentUserService.CurrentUser;
                await auditLogService.WriteAsync(
                    action,
                    success: true,
                    actorUserId: currentUser.Id,
                    actorAccount: currentUser.Account,
                    targetType: "AuditLog",
                    targetId: "*",
                    detail: detail);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to write audit log for audit maintenance. Action={Action}", action);
            }
        }

        private async Task OnExportAsync()
        {
            try
            {
                // 匯出目前查詢條件下的全部資料，而非只有當頁 —— 管理員要的是整份對照。
                var query = BuildQuery(1, AuditLogQueryService.MaxExportRows);
                var result = await auditLogQueryService.GetAsync(query);

                // 0.9.107 起經共用的 TabularExport（BOM、跳脫、換行與之前手寫的相同，ExportCsvCompatibilityTests 守門）。
                var bytes = TabularExport.ToCsv(CsvColumns, result.Result);
                var fileName = $"MyProject.Web-audit-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
                await JSRuntime.DownloadAsync(fileName, bytes, TabularExport.CsvContentType);

                logger.LogInformation("Audit log export downloaded. Rows={Rows}", result.Count);
                await WriteSelfAuditAsync(AuditActions.Audit.Export, $"format=csv; rows={result.Count}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Audit log export failed.");
                ViewNotification.UnexpectedError(notificationService, $"匯出失敗：{ex.GetType().Name}。", ex);
            }
        }

        /// <summary>CSV 欄位。標頭明講「本地」：稽核檔常被帶出系統比對，讀檔的人不會知道資料庫存的是 UTC。</summary>
        internal static readonly IReadOnlyList<ExportColumn<AuditLogAdapterModel>> CsvColumns =
        [
            new("發生時間（本地）", x => x.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss")),
            new("結果", x => x.Success ? "成功" : "失敗"),
            new("動作", x => x.Action),
            new("操作者帳號", x => x.ActorAccount),
            new("操作者Id", x => x.ActorUserId?.ToString()),
            new("目標類型", x => x.TargetType),
            new("目標識別", x => x.TargetId),
            new("摘要", x => x.Detail),
            // 0.9.117 起：放在最後，不打亂既有依欄位位置讀檔的下游。
            new("來源IP", x => x.ClientIp),
        ];

        /// <summary>操作者顯示文字；系統或匿名事件（例如帳號不存在的登入失敗）沒有 Id。</summary>
        internal static string ActorText(AuditLogAdapterModel item)
        {
            var account = string.IsNullOrWhiteSpace(item.ActorAccount) ? "（系統／匿名）" : item.ActorAccount;
            return item.ActorUserId is null ? account : $"{account}（Id={item.ActorUserId}）";
        }

        /// <summary>目標顯示文字，型別＋識別；兩者皆空顯示破折號。</summary>
        private static string TargetText(AuditLogAdapterModel item)
        {
            if (string.IsNullOrWhiteSpace(item.TargetType) && string.IsNullOrWhiteSpace(item.TargetId))
            {
                return "—";
            }

            var type = string.IsNullOrWhiteSpace(item.TargetType) ? "—" : item.TargetType;
            return string.IsNullOrWhiteSpace(item.TargetId) ? type : $"{type}#{item.TargetId}";
        }

        /// <summary>
        /// 依動作的第一節上色，讓「登入」「帳號異動」「權限拒絕」三類一眼分得開。
        /// 用 AntDesign Tag 的具名顏色，不寫色碼字面值（ThemeConventionTests 守門）。
        /// </summary>
        internal static string GetActionColor(string category) => category switch
        {
            "Login" => "blue",
            "User" => "green",
            "Role" => "purple",
            "Permission" => "red",
            "Audit" => "volcano",
            "Logout" => "blue",
            "Token" => "red",
            "Project" => "geekblue",
            "Category" => "lime",
            "Team" => "gold",
            "ExceptionLog" => "magenta",
            "EmailLog" => "magenta",
            "AiCallLog" => "magenta",
            "AiAgentConversation" => "magenta",
            "AdminPatrol" => "magenta",
            "TokenUsage" => "magenta",
            "LogLevel" => "cyan",
            "LogViewer" => "cyan",
            "Email" => "cyan",
            "Password" => "orange",
            "Job" => "cyan",
            "SystemParameter" => "cyan",
            "Backup" => "orange",
            "Announcement" => "cyan",
            "Notification" => "cyan",
            // 開頁（0.9.131 起）筆數最多，刻意用灰色，不搶增刪改的視線。
            "Page" => "default",
            _ => "default",
        };
    }
}
