using System.Text;
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

namespace MyProject.Web.Components.Views.Admins
{
    public partial class AuditLogView
    {
        /// <summary>
        /// 「清除很久以前的紀錄」門檻。刻意是程式常數而非設定鍵 ——
        /// 這是管理員按一下才會發生的動作，不需要每個部署各自調整。
        ///
        /// 這裡是 365 天而非例外紀錄的 90 天：例外紀錄清的是噪音，
        /// 稽核軌跡清的是責任證據，保存期預期以「年」為單位。
        /// </summary>
        private const int PurgeDays = 365;

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
        private string keyword = string.Empty;

        private List<string> actionOptions = [];

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
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load audit log action options.");
                actionOptions = [];
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
                ViewNotification.Error(notificationService, $"載入稽核紀錄失敗：{ex.GetType().Name}。");
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
            Action = selectedAction,
            Success = selectedSuccess switch
            {
                "true" => true,
                "false" => false,
                _ => null,
            },
            Keyword = keyword,
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
        }

        private async Task OnRefreshAsync()
        {
            logger.LogInformation("Audit log refresh triggered.");
            await ReloadActionOptionsAsync();
            await ReloadAsync();
        }

        private void OnView(AuditLogAdapterModel item)
        {
            detailItem = item;
            detailVisible = true;
        }

        private async Task OnPurgeAsync()
        {
            var confirmed = await ConfirmDialog.AskDestructiveAsync(
                modalService,
                $"清除 {PurgeDays} 天前的紀錄",
                $"將刪除發生時間早於 {PurgeDays} 天前的所有稽核紀錄。此動作無法復原，"
                    + "且本次清除會另外留下一筆稽核紀錄。",
                "清除");

            if (confirmed == false)
            {
                return;
            }

            var result = await auditLogQueryService.PurgeAsync(PurgeDays);
            if (result.Success)
            {
                await WriteSelfAuditAsync("Audit.Purge", $"清除 {PurgeDays} 天前的稽核紀錄：{result.Message}");
                ViewNotification.Warning(
                    notificationService,
                    string.IsNullOrWhiteSpace(result.Message) ? "清除完成" : result.Message);
                await ReloadActionOptionsAsync();
                await ReloadAsync();
            }
            else
            {
                ViewNotification.Error(notificationService, result.Message);
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
                await WriteSelfAuditAsync("Audit.ClearAll", $"清空全部稽核紀錄：{result.Message}");
                ViewNotification.Warning(notificationService, "已清空全部稽核紀錄");
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

                var builder = new StringBuilder();

                // 標頭明講「本地」：稽核檔常被帶出系統比對，讀檔的人不會知道資料庫存的是 UTC。
                builder.AppendLine("發生時間（本地）,結果,動作,操作者帳號,操作者Id,目標類型,目標識別,摘要");
                foreach (var item in result.Result)
                {
                    builder.AppendLine(string.Join(
                        ',',
                        Csv(item.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss")),
                        Csv(item.Success ? "成功" : "失敗"),
                        Csv(item.Action),
                        Csv(item.ActorAccount),
                        Csv(item.ActorUserId?.ToString()),
                        Csv(item.TargetType),
                        Csv(item.TargetId),
                        Csv(item.Detail)));
                }

                // 匯出檔的 BOM 一律走 TextDownloadPayload；自己接 UTF8Encoding 容易寫成「看起來有、其實沒有」。
                var bytes = TextDownloadPayload.Utf8WithBom(builder.ToString());

                using var stream = new MemoryStream(bytes);
                using var streamReference = new DotNetStreamReference(stream);

                var fileName = $"MyProject.Web-audit-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
                await JSRuntime.InvokeVoidAsync("appFileDownload.downloadFromStream", fileName, streamReference, "text/csv");

                logger.LogInformation("Audit log export downloaded. Rows={Rows}", result.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Audit log export failed.");
                ViewNotification.Error(notificationService, $"匯出失敗：{ex.GetType().Name}。");
            }
        }

        /// <summary>CSV 欄位跳脫：雙引號加倍，整欄以雙引號包住，換行才不會把一列拆成兩列。</summary>
        private static string Csv(string? value)
            => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";

        /// <summary>操作者顯示文字；系統或匿名事件（例如帳號不存在的登入失敗）沒有 Id。</summary>
        private static string ActorText(AuditLogAdapterModel item)
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
        private static string GetActionColor(string category) => category switch
        {
            "Login" => "blue",
            "User" => "green",
            "Role" => "purple",
            "Permission" => "red",
            "Audit" => "volcano",
            _ => "default",
        };
    }
}
