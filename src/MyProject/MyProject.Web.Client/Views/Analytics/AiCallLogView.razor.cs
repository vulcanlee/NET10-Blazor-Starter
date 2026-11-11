using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MyProject.Dtos.Ai;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Client.Commons;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Client.Views.Analytics
{
    /// <summary>
    /// AI 對話紀錄清單（0.9.72 起；0.10.5 起在 WASM 執行，WASM 遷移 M6）。管理員專屬：內容含日誌、例外堆疊與使用者帳號。
    /// 畫面、對話窗與提示訊息照 Server 版，資料改走 <see cref="AiCallLogApi"/>：查詢（含內文搜尋）、匯出、刪除、清除都在伺服器做，稽核也由伺服器寫。
    /// 清單只顯示中繼資料；內文在明細視窗（<see cref="AiCallLogDetailModal"/>）才向伺服器讀。
    /// </summary>
    public partial class AiCallLogView
    {
        [Inject]
        public AiCallLogApi Api { get; set; } = default!;

        [Inject]
        public PagePermissions Permissions { get; set; } = default!;

        [Inject]
        public ModalService modalService { get; set; } = default!;

        [Inject]
        public NotificationService notificationService { get; set; } = default!;

        [Inject]
        public IJSRuntime JSRuntime { get; set; } = default!;

        [Inject]
        public ILogger<AiCallLogView> logger { get; set; } = default!;

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

        // 內文搜尋（0.9.169 起）：按「查詢」或「重新整理」時由伺服器掃描一次，記下符合的 Id；換頁、排序、匯出都帶這份結果。
        private string contentKeyword = string.Empty;
        private string searchedContentKeyword = string.Empty;
        private AiCallLogContentSearchDto? contentSearch;
        private string scannedSignature = string.Empty;
        private CancellationTokenSource? scanCancellation;
        private bool isScanning;
        private bool mergedExporting;

        /// <summary>內文搜尋一次最多掃描幾筆、記錄開關與保留天數（伺服器的設定，由選項 API 帶來）。</summary>
        private int ContentScanLimit = AiCallLogSearchRequestDto.MaxContentIds;
        private bool IsRecordingEnabled = true;
        private int RetentionDays;

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
        private IEnumerable<AiCallLogAdapterModel> selectedRows = [];
        private bool batchDeleting;

        /// <summary>從 Token 用量明細帶過來的呼叫識別碼；有值時載入後自動開啟該筆明細。</summary>
        [Parameter]
        public Guid? InitialCallId { get; set; }

        protected override async Task OnInitializedAsync()
        {
            // 管理員專屬：權限鍵刻意未上架角色矩陣，以 IsAdmin 直接判斷（與同群組的 Token 用量頁一致）。
            // 權限未通過前不讀取任何紀錄；沒有權限的開頁稽核由伺服器在開頁紀錄時寫（shell/page-view）。
            if (!Permissions.IsAdmin)
            {
                isAccessChecked = true;
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                logger.LogWarning("AI call log view denied because the current user is not an administrator.");
                return;
            }

            // 先取得記錄開關與保留天數再開始渲染，說明框不會先閃一下錯的天數。
            await ReloadFilterOptionsAsync();
            isAccessChecked = true;
            await ReloadAsync();
        }

        /// <summary>
        /// 這次內文搜尋時的條件。之後改了任何條件（含內文關鍵字）卻沒按「查詢」，清單上方會提示結果已過期，
        /// 匯出也會先請使用者重新查詢（審查發現：舊的符合清單會和新條件取交集，放寬日期後漏掉紀錄）。
        /// </summary>
        private string FilterSignature()
            => string.Join(
                "\u001f",
                startDate?.ToString("yyyy-MM-dd") ?? string.Empty,
                endDate?.ToString("yyyy-MM-dd") ?? string.Empty,
                selectedOperation,
                selectedSuccess,
                selectedModel,
                accountFilter?.Trim() ?? string.Empty,
                keyword?.Trim() ?? string.Empty,
                contentKeyword?.Trim() ?? string.Empty);

        private bool IsContentSearchStale => contentSearch is not null && FilterSignature() != scannedSignature;

        /// <summary>內文搜尋結果已過期時提示並回 true（匯出前呼叫）。</summary>
        private bool WarnIfContentSearchStale()
        {
            if (IsContentSearchStale == false)
            {
                return false;
            }

            ViewNotification.Caution(notificationService, "條件已變更，請先按「查詢」重新搜尋內文再匯出。");
            return true;
        }

        public void Dispose()
        {
            // 離開頁面時停止內文掃描（伺服器端同步停止）。
            scanCancellation?.Cancel();
            scanCancellation?.Dispose();
            scanCancellation = null;
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            // 深連結：等畫面（含明細元件）渲染完才開，detailModal 的 @ref 才有值。
            if (InitialCallId is not { } callId || detailModal is null)
            {
                return;
            }

            InitialCallId = null;

            var result = await Api.FindByCallIdAsync(callId);
            if (result.Success)
            {
                await detailModal.OpenAsync(result.Data);
            }
            else if (result.StatusCode == 404)
            {
                ViewNotification.Caution(notificationService, result.UserMessage());
            }
            else
            {
                ViewNotification.Error(notificationService, $"開啟對話紀錄失敗：{result.UserMessage()}");
            }
        }

        private async Task ReloadFilterOptionsAsync()
        {
            var result = await Api.OptionsAsync();
            if (!result.Success || result.Data is not { } options)
            {
                logger.LogWarning("Failed to load AI call log filter options. Message={Message}", result.UserMessage());
                filterOptions = new();
                return;
            }

            filterOptions = new AiCallLogFilterOptions { Operations = options.Operations, Models = options.Models };
            IsRecordingEnabled = options.Enabled;
            RetentionDays = options.RetentionDays;
            ContentScanLimit = options.ContentScanLimit;

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

        /// <summary>換頁、排序、刪除之後：沿用上次內文搜尋的結果，不重掃。</summary>
        private Task ReloadAsync() => LoadAsync(scan: false, recordSearch: false);

        /// <summary>
        /// 載入清單。<paramref name="scan"/> 為 true 時重新做內文搜尋：有填內文關鍵字就由伺服器逐檔掃描（長時間請求、離開頁面即取消），
        /// 沒填就清掉上次的結果。掃描失敗時提示並退回只用其他條件（與 Server 版相同）。
        /// </summary>
        private async Task LoadAsync(bool scan, bool recordSearch)
        {
            // 換頁、排序、查詢與刪除後都會走到這裡：勾選只在目前這一頁有效（0.9.132 起）。
            selectedRows = [];
            var request = BuildRequest(_pageIndex, _pageSize);
            request.RecordSearch = recordSearch;

            CancellationTokenSource? cancellation = null;
            var signature = string.Empty;
            if (scan)
            {
                scanCancellation?.Cancel();
                scanCancellation?.Dispose();
                scanCancellation = null;
                contentSearch = null;
                searchedContentKeyword = contentKeyword?.Trim() ?? string.Empty;
                request.ContentIds = null;
                if (searchedContentKeyword.Length > 0)
                {
                    // 掃描期間停用「查詢」「重新整理」（審查發現：連按兩次會讓兩次掃描互相覆蓋）；離開頁面時取消。
                    cancellation = new CancellationTokenSource();
                    scanCancellation = cancellation;
                    signature = FilterSignature();
                    request.ContentKeyword = searchedContentKeyword;
                    isScanning = true;
                }
            }

            isLoading = true;
            StateHasChanged();

            try
            {
                var result = cancellation is null ? await Api.SearchAsync(request) : await Api.ScanAsync(request, cancellation.Token);
                if (cancellation?.IsCancellationRequested == true)
                {
                    return;
                }

                if (cancellation is not null && !result.Success)
                {
                    // 內文搜尋失敗：清掉結果並提示，清單退回只用其他條件。
                    ViewNotification.Error(notificationService, result.StatusCode == 500 ? result.UserMessage() : $"內文搜尋失敗：{result.UserMessage()}");
                    isScanning = false;
                    request.ContentKeyword = null;
                    // 搜尋稽核在掃描之前已由伺服器記下（含「內文有填」），退回重查不再記一筆。
                    request.RecordSearch = false;
                    result = await Api.SearchAsync(request);
                }
                else if (cancellation is not null)
                {
                    contentSearch = result.Data?.ContentSearch;
                    scannedSignature = signature;
                }

                if (result.Success && result.Data is { } page)
                {
                    items = page.Items.Select(AiCallLogDtoMapper.ToModel).ToList();
                    _total = page.TotalCount;
                }
                else
                {
                    ViewNotification.Error(notificationService, $"載入對話紀錄失敗：{result.UserMessage()}");
                    items = [];
                    _total = 0;
                }
            }
            finally
            {
                if (cancellation?.IsCancellationRequested != true)
                {
                    isScanning = false;
                    isLoading = false;
                    StateHasChanged();
                }
            }
        }

        /// <summary>清單與匯出共用同一組條件，只有分頁不同（日期當成伺服器的本地日期送出，不帶時差）。</summary>
        private AiCallLogSearchRequestDto BuildRequest(int pageIndex, int pageSize) => new()
        {
            StartDate = ClientCulture.Unzoned(startDate),
            EndDate = ClientCulture.Unzoned(endDate),
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
            ContentIds = contentSearch?.Ids,
            PageIndex = pageIndex,
            PageSize = pageSize,
            // 排序修正：AntDesign 給的方向是 Ascending／Descending（Server 版比對 ascend／descend，排序從來沒生效）。
            SortBy = sortDirection is "Ascending" or "Descending" ? sortField : null,
            SortDescending = sortDirection == "Descending",
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
            if (isScanning)
            {
                return;
            }

            _pageIndex = 1;
            // 搜尋稽核由伺服器寫（只有按「查詢」才帶 RecordSearch）。
            await LoadAsync(scan: true, recordSearch: true);
        }

        private async Task OnRefreshAsync()
        {
            if (isScanning)
            {
                return;
            }

            await ReloadFilterOptionsAsync();

            // 重新整理要看到新的紀錄：有內文關鍵字時重新掃描。
            await LoadAsync(scan: contentSearch is not null || string.IsNullOrWhiteSpace(contentKeyword) == false, recordSearch: false);
        }

        /// <summary>合併匯出勾選的紀錄（0.9.169 起）。</summary>
        private async Task OnExportSelectedPdfAsync()
        {
            mergedExporting = true;
            try
            {
                await ExportMergedPdfAsync(new AiCallLogMergedPdfRequestDto { Source = "selected", Ids = selectedRows.Select(x => x.Id).ToList() });
            }
            finally
            {
                mergedExporting = false;
            }
        }

        /// <summary>合併匯出目前查詢結果（0.9.169 起）：最多 50 筆（伺服器檢查）；有內文搜尋時只含符合的。</summary>
        private async Task OnExportQueryPdfAsync()
        {
            if (WarnIfContentSearchStale())
            {
                return;
            }

            mergedExporting = true;
            try
            {
                await ExportMergedPdfAsync(new AiCallLogMergedPdfRequestDto { Source = "query", Query = BuildRequest(1, MagicObjectHelper.PageSize) });
            }
            finally
            {
                mergedExporting = false;
            }
        }

        /// <summary>
        /// 合併匯出（清單勾選、目前查詢結果、明細窗的整段對話共用）：伺服器產生 PDF 並寫匯出稽核。
        /// 筆數超過上限、找不到紀錄這類可預期的情況用提醒（與 Server 版相同），其他失敗用錯誤。
        /// </summary>
        internal static async Task ExportMergedPdfAsync(
            AiCallLogApi api, IJSRuntime js, NotificationService notifications, ILogger logger, AiCallLogMergedPdfRequestDto request)
        {
            var outcome = await api.MergedPdfAsync(request);
            if (!outcome.Success)
            {
                if (outcome.StatusCode is 400 or 404)
                {
                    ViewNotification.Caution(notifications, outcome.Message);
                }
                else
                {
                    ViewNotification.Error(notifications, outcome.Message);
                }

                return;
            }

            try
            {
                await js.DownloadFileAsync(outcome.FileName!, outcome.Content!, outcome.ContentType!);
                logger.LogInformation("AI call logs merged PDF downloaded. Source={Source}, Bytes={Bytes}", request.Source, outcome.Content!.Length);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AI call logs merged PDF download failed.");
                ViewNotification.UnexpectedError(notifications, $"PDF 匯出失敗：{ex.GetType().Name}。", ex);
            }
        }

        private Task ExportMergedPdfAsync(AiCallLogMergedPdfRequestDto request)
            => ExportMergedPdfAsync(Api, JSRuntime, notificationService, logger, request);

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

            // 伺服器刪除並寫刪除稽核。
            var result = await Api.DeleteAsync(item.Id);
            if (result.Success)
            {
                ViewNotification.Warning(notificationService, "已刪除對話紀錄。");
                await ReloadFilterOptionsAsync();
                await ReloadAsync();
            }
            else
            {
                ViewNotification.Error(notificationService, result.Message);
            }
        }

        /// <summary>「刪除選取」（0.9.132 起）：逐筆呼叫刪除 API，規則與稽核同單筆刪除。</summary>
        private async Task OnBatchDeleteAsync()
        {
            try
            {
                var selected = selectedRows.ToList();
                if (selected.Count == 0 || !await BatchDelete.AskAsync(modalService, selected.Select(Describe).ToList(), restorable: false,
                        note: "對話紀錄的內容檔也會一併移除。"))
                {
                    return;
                }

                batchDeleting = true;
                var outcome = await BatchDelete.RunAsync(selected, x => x.Id.ToString(), Describe, x => Api.DeleteAsync(x.Id), logger);
                BatchDelete.Notify(notificationService, outcome);
                await ReloadFilterOptionsAsync();
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while batch deleting AI call logs.");
                ViewNotification.UnexpectedError(notificationService, "刪除對話紀錄時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
            finally
            {
                batchDeleting = false;
            }

            static string Describe(AiCallLogAdapterModel x) => $"{x.OccurredAt:yyyy-MM-dd HH:mm:ss}「{x.Operation}」";
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

            // 伺服器清除並寫清除稽核；成功時的訊息就是畫面要顯示的那一句（「已清除 N 筆紀錄。」）。
            var result = await Api.PurgeAsync(ClientCulture.Unzoned(beforeDate)!.Value);
            if (result.Success)
            {
                ViewNotification.Warning(notificationService, string.IsNullOrWhiteSpace(result.Message) ? "清除完成" : result.Message);
                await ReloadFilterOptionsAsync();
                await ReloadAsync();
            }
            else
            {
                ViewNotification.Error(notificationService, result.UserMessage());
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

            var result = await Api.ClearAllAsync();
            if (result.Success)
            {
                ViewNotification.Warning(notificationService, "已清空全部對話紀錄。");
                await ReloadFilterOptionsAsync();
                await ReloadAsync();
            }
            else
            {
                ViewNotification.Error(notificationService, result.UserMessage());
            }
        }

        private async Task OnExportAsync()
        {
            if (WarnIfContentSearchStale())
            {
                return;
            }

            // ⚠️ 只匯出中繼資料，不含內文：CSV 常被帶出系統，內文請在明細視窗逐筆下載（伺服器產生檔案並寫匯出稽核）。
            var outcome = await Api.ExportAsync(BuildRequest(1, MagicObjectHelper.PageSize));
            if (!outcome.Success)
            {
                ViewNotification.Error(notificationService, $"匯出失敗：{outcome.Message}");
                return;
            }

            try
            {
                await JSRuntime.DownloadFileAsync(outcome.FileName!, outcome.Content!, outcome.ContentType!);
                logger.LogInformation("AI call log export downloaded. Bytes={Bytes}", outcome.Content!.Length);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AI call log export failed.");
                ViewNotification.UnexpectedError(notificationService, $"匯出失敗：{ex.GetType().Name}。", ex);
            }
        }

        private static StatusTone ResolveTone(AiCallLogAdapterModel item)
            => item.Success ? StatusTone.Positive : item.IsCanceled ? StatusTone.Muted : StatusTone.Warning;

        private static string FormatElapsed(long milliseconds)
            => milliseconds >= 1000 ? $"{milliseconds / 1000d:N1} 秒" : $"{milliseconds:N0} ms";
    }
}
