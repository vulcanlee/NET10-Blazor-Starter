using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MyProject.Dtos.Admins;
using MyProject.Models.AdapterModel;
using MyProject.Share.Helpers;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Client.Commons;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Client.Views.Admins
{
    /// <summary>
    /// 稽核紀錄（0.10.4 起在 WASM 執行，WASM 遷移 M5）。畫面、對話窗與提示訊息照 Server 版，資料改走 <see cref="AuditLogApi"/>（只限管理員）：
    /// 查詢、明細、匯出、清除、清空與完整性驗證都在伺服器做，稽核也由伺服器寫（與 Server 版相同的動作與內容）。
    /// 時間由伺服器換算好帶來（本地與 UTC），不依瀏覽器的時區。
    /// </summary>
    public partial class AuditLogView
    {
        [Inject]
        public AuditLogApi Api { get; set; } = default!;

        [Inject]
        public PagePermissions Permissions { get; set; } = default!;

        [Inject]
        public ModalService ModalService { get; set; } = default!;

        [Inject]
        public NotificationService NotificationService { get; set; } = default!;

        [Inject]
        public IJSRuntime JSRuntime { get; set; } = default!;

        [Inject]
        public ILogger<AuditLogView> Logger { get; set; } = default!;

        /// <summary>「清除很久以前的紀錄」的兩個門檻（伺服器的 LogRetentionSettings，讀不到時顯示 0）。</summary>
        private int PurgeDays;
        private int BrowsingPurgeDays;

        /// <summary>目前沒有記錄的範圍（0.9.174 起，兩個開關預設都關）；都有記錄時為 null。</summary>
        private string? ScopeNotice;

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

        /// <summary>清單每一列由伺服器換算的 UTC 時間與簽章文字（明細窗用）。</summary>
        private Dictionary<int, AuditLogItemDto> rowInfo = [];

        private bool detailVisible;
        private AuditLogAdapterModel? detailItem;
        private DateTime detailUtc;
        private string detailSignature = string.Empty;
        /// <summary>明細窗的「詳細內容」（修改差異、查詢條件；0.9.131 起）。null 表示這筆沒有內容檔。</summary>
        private string? detailContent;
        private bool isContentLoading;

        private bool verifyVisible;
        private bool isVerifying;
        private AuditIntegrityReportDto? verifyReport;

        protected override async Task OnInitializedAsync()
        {
            isAccessChecked = true;

            // 此頁為管理員專屬：權限鍵刻意未上架角色矩陣，以 IsAdmin 直接判斷。權限未通過前不讀取任何稽核內容；
            // 沒有權限的開頁稽核由伺服器在開頁紀錄時寫（shell/page-view）。
            if (!Permissions.IsAdmin)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                Logger.LogWarning("Audit log view denied because the current user is not an administrator.");
                return;
            }

            await ReloadActionOptionsAsync();
            await ReloadAsync();
        }

        /// <summary>
        /// 重新載入下拉選項與清除門檻。清除／清空之後也要重跑：
        /// 某個代碼的最後一筆被刪掉時，選項就該跟著消失，否則會留下「選了查無資料」的死選項。
        /// </summary>
        private async Task ReloadActionOptionsAsync()
        {
            var result = await Api.OptionsAsync();
            if (!result.Success || result.Data is not { } options)
            {
                Logger.LogWarning("Failed to load audit log action options. Message={Message}", result.UserMessage());
                actionOptions = [];
                targetTypeOptions = [];
                return;
            }

            actionOptions = options.Actions;
            targetTypeOptions = options.TargetTypes;
            PurgeDays = options.PurgeDays;
            BrowsingPurgeDays = options.BrowsingPurgeDays;
            ScopeNotice = options.ScopeNotice;

            // 目前選取的代碼若已不存在，退回「不限」，避免畫面停在查不到東西的狀態。
            if (!string.IsNullOrEmpty(selectedAction) && !actionOptions.Contains(selectedAction))
            {
                selectedAction = string.Empty;
            }

            if (!string.IsNullOrEmpty(selectedTargetType) && !targetTypeOptions.Contains(selectedTargetType))
            {
                selectedTargetType = string.Empty;
            }
        }

        private async Task ReloadAsync(bool recordSearch = false)
        {
            isLoading = true;
            StateHasChanged();

            try
            {
                var result = await Api.SearchAsync(BuildRequest(_pageIndex, _pageSize, recordSearch));
                if (result.Success && result.Data is { } page)
                {
                    auditLogAdapterModels = page.Items.Select(AuditLogApi.ToModel).ToList();
                    rowInfo = page.Items.ToDictionary(x => x.Id);
                    _total = page.TotalCount;
                }
                else
                {
                    ViewNotification.Error(NotificationService, $"載入稽核紀錄失敗：{result.UserMessage()}");
                    auditLogAdapterModels = [];
                    rowInfo = [];
                    _total = 0;
                }
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
        private AuditLogSearchRequestDto BuildRequest(int pageIndex, int pageSize, bool recordSearch = false) => new()
        {
            // 起訖時間當成伺服器的本地時間送出（不帶時差）：帶了瀏覽器的時差，伺服器會先換算而偏移（審查發現）。
            StartTime = ClientCulture.Unzoned(startTime),
            EndTime = ClientCulture.Unzoned(endTime),
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
            PageIndex = pageIndex,
            PageSize = pageSize,
            SortBy = sortDirection is "Ascending" or "Descending" ? sortField : null,
            SortDescending = sortDirection == "Descending",
            RecordSearch = recordSearch,
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
            Logger.LogInformation("Audit log search triggered.");
            // 搜尋稽核由伺服器寫（只有按「查詢」才帶 RecordSearch）。
            await ReloadAsync(recordSearch: true);
        }

        private async Task OnRefreshAsync()
        {
            Logger.LogInformation("Audit log refresh triggered.");
            await ReloadActionOptionsAsync();
            await ReloadAsync();
        }

        /// <summary>先開窗、再向伺服器讀明細與詳細內容（伺服器寫一筆查看稽核；檔案可能很大，只在看明細時才讀）。</summary>
        private async Task OnViewAsync(AuditLogAdapterModel item)
        {
            detailItem = item;
            detailUtc = rowInfo.TryGetValue(item.Id, out var row) ? row.OccurredAtUtc : item.OccurredAt;
            detailSignature = row?.Signature ?? string.Empty;
            detailContent = null;
            isContentLoading = item.ContentFile is not null;
            detailVisible = true;
            StateHasChanged();

            var result = await Api.DetailAsync(item.Id);
            if (result.Success && result.Data is { } detail)
            {
                detailUtc = detail.OccurredAtUtc;
                detailSignature = detail.Signature;
                detailContent = detail.Content;
            }
            else if (item.ContentFile is not null)
            {
                detailContent = $"讀取詳細內容失敗。{result.UserMessage()}";
            }

            isContentLoading = false;
        }

        private async Task OnPurgeAsync()
        {
            // 0.9.174 起兩類各用自己的保留天數：異動與安全事件 PurgeDays、瀏覽類（換頁、搜尋、查看）BrowsingPurgeDays。
            var confirmed = await ConfirmDialog.AskDestructiveAsync(
                ModalService,
                "清除超過保留天數的紀錄",
                $"將刪除早於 {PurgeDays} 天前的異動與安全紀錄，以及早於 {BrowsingPurgeDays} 天前的瀏覽紀錄（換頁、搜尋、查看）。"
                    + "此動作無法復原，且本次清除會另外留下稽核紀錄。",
                "清除");

            if (!confirmed)
            {
                return;
            }

            var result = await Api.PurgeAsync();
            if (!result.Success || result.Data is not { } outcome)
            {
                ViewNotification.Error(NotificationService, result.UserMessage());
                return;
            }

            if (outcome.Succeeded)
            {
                ViewNotification.Warning(NotificationService, outcome.Message);
            }
            else
            {
                ViewNotification.Error(NotificationService, outcome.Message);
            }

            // 只要有一段刪到資料，清單就要重新整理，不能留著已刪掉的列。
            if (outcome.Changed)
            {
                await ReloadActionOptionsAsync();
                await ReloadAsync();
            }
        }

        private async Task OnClearAllAsync()
        {
            var confirmed = await ConfirmDialog.AskDestructiveAsync(
                ModalService,
                "清空全部稽核紀錄",
                "將刪除所有稽核紀錄。此動作無法復原，且本次清空會另外留下一筆稽核紀錄；完整性驗證會從下一筆紀錄重新起算。",
                "清空");

            if (!confirmed)
            {
                return;
            }

            var result = await Api.ClearAllAsync();
            if (result.Success)
            {
                ViewNotification.Warning(NotificationService, "已清空全部稽核紀錄。");
                await ReloadActionOptionsAsync();
                await ReloadAsync();
            }
            else
            {
                ViewNotification.Error(NotificationService, result.UserMessage());
            }
        }

        /// <summary>
        /// 驗證完整性（0.9.175 起）：伺服器重算全部簽章，結果顯示在視窗；只比對、不更新檢查點（檢查點只由每天的排程更新）。
        /// 伺服器寫一筆 <c>Audit.Verify</c>。
        /// </summary>
        private async Task OnVerifyAsync()
        {
            verifyReport = null;
            isVerifying = true;
            verifyVisible = true;
            StateHasChanged();

            try
            {
                var result = await Api.VerifyAsync();
                if (result.Success && result.Data is { } report)
                {
                    verifyReport = report;
                }
                else
                {
                    verifyVisible = false;
                    ViewNotification.Error(NotificationService, result.StatusCode == 500
                        ? "驗證完整性失敗，詳細原因已記錄在系統日誌。"
                        : result.UserMessage());
                }
            }
            finally
            {
                isVerifying = false;
            }
        }

        private async Task OnExportAsync()
        {
            // 匯出目前查詢條件下的全部資料，而非只有當頁 —— 管理員要的是整份對照（伺服器產生檔案並寫匯出稽核）。
            var outcome = await Api.ExportAsync(BuildRequest(1, MagicObjectHelper.PageSize));
            if (!outcome.Success)
            {
                ViewNotification.Error(NotificationService, $"匯出失敗：{outcome.Message}");
                return;
            }

            try
            {
                await JSRuntime.DownloadFileAsync(outcome.FileName!, outcome.Content!, outcome.ContentType!);
                Logger.LogInformation("Audit log export downloaded. Bytes={Bytes}", outcome.Content!.Length);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Audit log export failed.");
                ViewNotification.UnexpectedError(NotificationService, $"匯出失敗：{ex.GetType().Name}。", ex);
            }
        }

        /// <summary>操作者顯示文字（規則在 <see cref="AuditLogDisplay.ActorText"/>，與異動歷程對話窗共用）。</summary>
        internal static string ActorText(AuditLogAdapterModel item) => AuditLogDisplay.ActorText(item);

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

        /// <summary>依動作的第一節上色（規則在 <see cref="AuditLogDisplay.GetActionColor"/>，與異動歷程對話窗共用）。</summary>
        internal static string GetActionColor(string category) => AuditLogDisplay.GetActionColor(category);
    }
}
