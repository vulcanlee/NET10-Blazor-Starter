using System.Text;
using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Ai;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Components.Views.Analytics
{
    /// <summary>
    /// AI 對話紀錄清單（0.9.72 起）。管理員專屬：內容含日誌、例外堆疊與使用者帳號。
    /// 清單只顯示中繼資料；內文在明細視窗（<see cref="AiCallLogDetailModal"/>）才讀檔。
    /// </summary>
    public partial class AiCallLogView
    {
        private readonly ILogger<AiCallLogView> logger;
        private readonly AiCallLogService aiCallLogService;
        private readonly IAuditLogService auditLogService;
        private readonly CurrentUserService currentUserService;
        private readonly ModalService modalService;
        private readonly NotificationService notificationService;
        private readonly IOptionsMonitor<AiCallLogSettings> settings;

        private ITable? table;
        private AiCallLogDetailModal? detailModal;

        private DateTime? startDate;
        private DateTime? endDate;
        private DateTime? purgeBeforeDate;
        private string selectedOperation = string.Empty;
        private string selectedSuccess = string.Empty;
        private string selectedModel = string.Empty;
        private string accountFilter = string.Empty;
        private string keyword = string.Empty;

        private AiCallLogFilterOptions filterOptions = new();

        private int _pageIndex = 1;
        private int _pageSize = MagicObjectHelper.PageSize;
        private int _total;
        private string sortField = string.Empty;
        private string sortDirection = "None";

        private bool isLoading;
        private string RoleMessage = string.Empty;
        private bool isAccessChecked;

        private List<AiCallLogAdapterModel> items = [];

        /// <summary>從 Token 用量明細帶過來的呼叫識別碼；有值時載入後自動開啟該筆明細。</summary>
        [Parameter]
        public Guid? InitialCallId { get; set; }

        [Inject]
        public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

        [Inject]
        public AuthenticationStateProvider authStateProvider { get; set; } = default!;

        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        [Inject]
        public IJSRuntime JSRuntime { get; set; } = default!;

        public AiCallLogView(
            ILogger<AiCallLogView> logger,
            AiCallLogService aiCallLogService,
            IAuditLogService auditLogService,
            CurrentUserService currentUserService,
            ModalService modalService,
            NotificationService notificationService,
            IOptionsMonitor<AiCallLogSettings> settings)
        {
            this.logger = logger;
            this.aiCallLogService = aiCallLogService;
            this.auditLogService = auditLogService;
            this.currentUserService = currentUserService;
            this.modalService = modalService;
            this.notificationService = notificationService;
            this.settings = settings;
        }

        private bool IsRecordingEnabled => settings.CurrentValue.Enabled;

        private int RetentionDays => settings.CurrentValue.RetentionDays;

        protected override async Task OnInitializedAsync()
        {
            var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
            if (checkResult != AuthenticationCheckResult.Succeeded)
            {
                return;
            }

            isAccessChecked = true;

            // 管理員專屬：權限鍵刻意未上架角色矩陣，以 CheckIsAdmin 直接判斷（與同群組的 Token 用量頁一致）。
            // 權限未通過前不讀取任何紀錄。
            if (AuthenticationStateHelper.CheckIsAdmin() == false)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                logger.LogWarning("AI call log view denied because the current user is not an administrator.");
                return;
            }

            await ReloadFilterOptionsAsync();
            await ReloadAsync();
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            // 深連結：等畫面（含明細元件）渲染完才開，detailModal 的 @ref 才有值。
            if (InitialCallId is not { } callId || detailModal is null)
            {
                return;
            }

            InitialCallId = null;

            try
            {
                var id = await aiCallLogService.FindIdByCallIdAsync(callId);
                if (id is null)
                {
                    ViewNotification.Warning(
                        notificationService,
                        "找不到這次呼叫的對話紀錄（可能已被清除、超過保留天數，或當時未啟用記錄）。");
                    return;
                }

                await detailModal.OpenAsync(id.Value);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to open AI call log from deep link.");
                ViewNotification.Error(notificationService, $"開啟對話紀錄失敗：{ex.GetType().Name}。");
            }
        }

        private async Task ReloadFilterOptionsAsync()
        {
            try
            {
                filterOptions = await aiCallLogService.GetFilterOptionsAsync();

                // 目前選取的值若已不存在（例如被清除），退回「不限」，避免停在查不到東西的狀態。
                if (string.IsNullOrEmpty(selectedOperation) == false && filterOptions.Operations.Contains(selectedOperation) == false)
                {
                    selectedOperation = string.Empty;
                }

                if (string.IsNullOrEmpty(selectedModel) == false && filterOptions.Models.Contains(selectedModel) == false)
                {
                    selectedModel = string.Empty;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load AI call log filter options.");
                filterOptions = new();
            }
        }

        private async Task ReloadAsync()
        {
            isLoading = true;
            StateHasChanged();

            try
            {
                var result = await aiCallLogService.GetAsync(BuildQuery(_pageIndex, _pageSize));
                items = result.Result.ToList();
                _total = result.Count;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load AI call logs.");
                ViewNotification.Error(notificationService, $"載入對話紀錄失敗：{ex.GetType().Name}。");
                items = [];
                _total = 0;
            }
            finally
            {
                isLoading = false;
                StateHasChanged();
            }
        }

        /// <summary>清單與匯出共用同一組條件，只有分頁不同。</summary>
        private AiCallLogQuery BuildQuery(int currentPage, int pageSize) => new()
        {
            StartDate = startDate,
            EndDate = endDate,
            Operation = selectedOperation,
            Account = accountFilter,
            Success = selectedSuccess switch
            {
                "true" => true,
                "false" => false,
                _ => null,
            },
            Model = selectedModel,
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

        private async Task OnTableChange(QueryModel<AiCallLogAdapterModel> args)
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
            await ReloadAsync();
        }

        private async Task OnRefreshAsync()
        {
            await ReloadFilterOptionsAsync();
            await ReloadAsync();
        }

        private async Task OnViewAsync(int id)
        {
            if (detailModal is not null)
            {
                await detailModal.OpenAsync(id);
            }
        }

        private async Task OnDeleteAsync(AiCallLogAdapterModel item)
        {
            var confirmed = await ConfirmDialog.AskDestructiveAsync(
                modalService,
                "刪除對話紀錄",
                $"將刪除 {item.OccurredAt:yyyy-MM-dd HH:mm:ss} 的「{item.Operation}」對話紀錄與內容檔。此動作無法復原。",
                "刪除");

            if (confirmed == false)
            {
                return;
            }

            var result = await aiCallLogService.DeleteAsync(item.Id);
            if (result.Success)
            {
                await WriteSelfAuditAsync("AiCallLog.Delete", item.Id.ToString(), $"刪除 AI 對話紀錄：{item.Operation}");
                ViewNotification.Warning(notificationService, "已刪除對話紀錄");
                await ReloadFilterOptionsAsync();
                await ReloadAsync();
            }
            else
            {
                ViewNotification.Error(notificationService, result.Message);
            }
        }

        private async Task OnPurgeAsync()
        {
            if (purgeBeforeDate is not { } beforeDate)
            {
                return;
            }

            var confirmed = await ConfirmDialog.AskDestructiveAsync(
                modalService,
                "批次清除對話紀錄",
                $"將刪除 {beforeDate:yyyy-MM-dd}（不含）之前的所有 AI 對話紀錄與內容檔。此動作無法復原。",
                "清除");

            if (confirmed == false)
            {
                return;
            }

            var result = await aiCallLogService.PurgeBeforeAsync(beforeDate);
            if (result.Success)
            {
                await WriteSelfAuditAsync("AiCallLog.Purge", "*", $"清除 {beforeDate:yyyy-MM-dd} 之前的 AI 對話紀錄：{result.Message}");
                ViewNotification.Warning(notificationService, string.IsNullOrWhiteSpace(result.Message) ? "清除完成" : result.Message);
                await ReloadFilterOptionsAsync();
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
                "清空全部對話紀錄",
                "將刪除所有 AI 對話紀錄與內容檔（Token 用量不受影響）。此動作無法復原。",
                "清空");

            if (confirmed == false)
            {
                return;
            }

            var result = await aiCallLogService.ClearAllAsync();
            if (result.Success)
            {
                await WriteSelfAuditAsync("AiCallLog.ClearAll", "*", $"清空全部 AI 對話紀錄：{result.Message}");
                ViewNotification.Warning(notificationService, "已清空全部對話紀錄");
                await ReloadFilterOptionsAsync();
                await ReloadAsync();
            }
            else
            {
                ViewNotification.Error(notificationService, result.Message);
            }
        }

        /// <summary>
        /// 刪除對話紀錄本身留下一筆稽核紀錄（只記動作與筆數，絕不記內容）。
        /// 稽核寫入失敗不推翻「已經刪除」這個事實，因此只記錯誤、不向使用者報錯。
        /// </summary>
        private async Task WriteSelfAuditAsync(string action, string targetId, string detail)
        {
            try
            {
                var currentUser = currentUserService.CurrentUser;
                await auditLogService.WriteAsync(
                    action,
                    success: true,
                    actorUserId: currentUser.Id,
                    actorAccount: currentUser.Account,
                    targetType: "AiCallLog",
                    targetId: targetId,
                    detail: detail);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to write audit log for AI call log maintenance. Action={Action}", action);
            }
        }

        private async Task OnExportAsync()
        {
            try
            {
                var result = await aiCallLogService.GetAsync(BuildQuery(1, AiCallLogService.MaxExportRows));

                // ⚠️ 只匯出中繼資料，不含內文：CSV 常被帶出系統，內文請在明細視窗逐筆下載。
                var builder = new StringBuilder();
                builder.AppendLine("送出時間（本地）,結果,失敗原因,作業,帳號,供應商,模型,HTTP,結束原因,耗時ms,送出字元,回應字元,關聯說明,呼叫識別碼");
                foreach (var item in result.Result)
                {
                    builder.AppendLine(string.Join(
                        ',',
                        Csv(item.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss")),
                        Csv(AiCallLogPdfBuilder.DescribeOutcome(item)),
                        Csv(item.FailureReason),
                        Csv(item.Operation),
                        Csv(item.Account),
                        Csv(item.Provider),
                        Csv(item.Model),
                        Csv(item.HttpStatus?.ToString()),
                        Csv(item.FinishReason),
                        Csv(item.ElapsedMilliseconds.ToString()),
                        Csv(item.RequestCharacters.ToString()),
                        Csv(item.ResponseCharacters.ToString()),
                        Csv(item.RelatedInfo),
                        Csv(item.CallId.ToString())));
                }

                var bytes = TextDownloadPayload.Utf8WithBom(builder.ToString());

                using var stream = new MemoryStream(bytes);
                using var streamReference = new DotNetStreamReference(stream);

                var fileName = $"MyProject.Web-ai-call-logs-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
                await JSRuntime.InvokeVoidAsync("appFileDownload.downloadFromStream", fileName, streamReference, "text/csv");

                logger.LogInformation("AI call log export downloaded. Rows={Rows}", result.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AI call log export failed.");
                ViewNotification.Error(notificationService, $"匯出失敗：{ex.GetType().Name}。");
            }
        }

        /// <summary>CSV 欄位跳脫：雙引號加倍，整欄以雙引號包住，換行才不會把一列拆成兩列。</summary>
        private static string Csv(string? value)
            => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";

        private static StatusTone ResolveTone(AiCallLogAdapterModel item)
            => item.Success ? StatusTone.Positive : item.IsCanceled ? StatusTone.Muted : StatusTone.Warning;

        private static string FormatElapsed(long milliseconds)
            => milliseconds >= 1000 ? $"{milliseconds / 1000d:N1} 秒" : $"{milliseconds:N0} ms";
    }
}
