using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MyProject.Dtos.Admins;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Client.Commons;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Client.Views.Admins
{
    /// <summary>
    /// 系統例外紀錄（0.10.5 起在 WASM 執行，WASM 遷移 M6）。畫面、對話窗與提示訊息照 Server 版，資料改走 <see cref="ExceptionLogApi"/>（只限管理員）：
    /// 查詢、明細、複製、匯出、刪除、清除與清空都在伺服器做，稽核也由伺服器寫（與 Server 版相同的動作與內容）。
    /// 排序修正：Server 版比對 "ascend"/"descend"（AntDesign 給的是 Ascending/Descending），排序從來沒生效；這裡照稽核紀錄頁的做法讓它生效，匯出與複製也與畫面同序。
    /// </summary>
    public partial class ExceptionLogView
    {
        /// <summary>
        /// 「複製目前查詢結果」超過這個筆數先確認。每筆都含完整堆疊（數 KB～數十 KB），
        /// 比日誌檢視的單行內容大得多，所以門檻也低得多；大量資料建議改用匯出。
        /// </summary>
        private const int CopyAllConfirmThreshold = 100;

        [Inject]
        public ExceptionLogApi Api { get; set; } = default!;

        [Inject]
        public PagePermissions Permissions { get; set; } = default!;

        [Inject]
        public ModalService modalService { get; set; } = default!;

        [Inject]
        public NotificationService notificationService { get; set; } = default!;

        [Inject]
        public IJSRuntime JSRuntime { get; set; } = default!;

        [Inject]
        public ILogger<ExceptionLogView> logger { get; set; } = default!;

        /// <summary>「清除很久沒再發生的紀錄」門檻（伺服器的 LogRetentionSettings，讀不到時顯示 0）。</summary>
        private int PurgeDays;

        /// <summary>匯出與「複製目前查詢結果」的筆數上限（伺服器給的）。</summary>
        private int maxRows;

        // AI 例外分析的設定（伺服器給的；系統參數頁可改，所以重新整理時也重讀）。
        private bool aiAvailable;
        private string aiUnavailableReason = string.Empty;
        private int maxFollowUpRounds;

        private ITable? table;

        private DateTime? startTime;
        private DateTime? endTime;
        private string selectedSource = string.Empty;
        private string accountFilter = string.Empty;
        private string keyword = string.Empty;

        private int _pageIndex = 1;
        private int _pageSize = MagicObjectHelper.PageSize;
        private int _total;
        private string sortField = string.Empty;
        private string sortDirection = "None";

        private bool isLoading;
        private string RoleMessage = string.Empty;
        private bool isAccessChecked;

        private List<ExceptionLogAdapterModel> exceptionLogAdapterModels = [];
        private IEnumerable<ExceptionLogAdapterModel> selectedRows = [];
        private bool batchDeleting;

        // 明細窗：清單放不下的欄位與完整堆疊都在這裡看。開窗當下才向伺服器讀堆疊。
        private bool detailVisible;
        private ExceptionLogAdapterModel? detailItem;
        private string detailStackTrace = string.Empty;
        private bool isStackTraceLoading;

        private ExceptionAiAnalysisModal? aiAnalysisModal;

        protected override async Task OnInitializedAsync()
        {
            isAccessChecked = true;

            // 此頁為管理員專屬：權限鍵刻意未上架角色矩陣，以 IsAdmin 直接判斷。權限未通過前不讀取任何例外內容；
            // 沒有權限的開頁稽核由伺服器在開頁紀錄時寫（shell/page-view）。
            if (!Permissions.IsAdmin)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                logger.LogWarning("Exception log view denied because the current user is not an administrator.");
                return;
            }

            await ReloadOptionsAsync();
            await ReloadAsync();
        }

        /// <summary>清除天數、筆數上限與 AI 設定。</summary>
        private async Task ReloadOptionsAsync()
        {
            var result = await Api.OptionsAsync();
            if (!result.Success || result.Data is not { } options)
            {
                logger.LogWarning("Failed to load exception log options. Message={Message}", result.UserMessage());
                aiAvailable = false;
                aiUnavailableReason = result.UserMessage();
                return;
            }

            PurgeDays = options.PurgeDays;
            maxRows = options.MaxRows;
            aiAvailable = options.AiAvailable;
            aiUnavailableReason = options.AiUnavailableReason;
            maxFollowUpRounds = options.MaxFollowUpRounds;
        }

        private async Task ReloadAsync(bool recordSearch = false)
        {
            // 換頁、排序、查詢與刪除後都會走到這裡：勾選只在目前這一頁有效（0.9.132 起）。
            selectedRows = [];
            isLoading = true;
            StateHasChanged();

            try
            {
                var result = await Api.SearchAsync(BuildRequest(_pageIndex, _pageSize, recordSearch));
                if (result.Success && result.Data is { } page)
                {
                    exceptionLogAdapterModels = page.Items.Select(ExceptionLogDtoMapper.ToModel).ToList();
                    _total = page.TotalCount;
                }
                else
                {
                    ViewNotification.Error(notificationService, $"載入例外紀錄失敗：{result.UserMessage()}");
                    exceptionLogAdapterModels = [];
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
        /// 由目前的工具列狀態組出查詢條件。清單、匯出與「複製目前查詢結果」共用，三者條件與排序一致
        /// （匯出與複製的是「目前畫面條件下的全部資料」，只有分頁不同）。
        /// </summary>
        private ExceptionLogSearchRequestDto BuildRequest(int pageIndex, int pageSize, bool recordSearch = false) => new()
        {
            // 起訖時間當成伺服器的本地時間送出（不帶時差）。
            StartTime = ClientCulture.Unzoned(startTime),
            EndTime = ClientCulture.Unzoned(endTime),
            Source = selectedSource,
            Account = accountFilter,
            Keyword = keyword,
            PageIndex = pageIndex,
            PageSize = pageSize,
            SortBy = sortDirection is "Ascending" or "Descending" ? sortField : null,
            SortDescending = sortDirection == "Descending",
            RecordSearch = recordSearch,
        };

        private async Task OnTableChange(QueryModel<ExceptionLogAdapterModel> args)
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
            logger.LogInformation("Exception log search triggered.");
            // 搜尋稽核由伺服器寫（只有按「查詢」才帶 RecordSearch）。
            await ReloadAsync(recordSearch: true);
        }

        private async Task OnRefreshAsync()
        {
            logger.LogInformation("Exception log refresh triggered.");
            await ReloadOptionsAsync();
            await ReloadAsync();
        }

        /// <summary>先開窗、再向伺服器讀堆疊（伺服器寫一筆查看稽核）。清單查詢不應該一次把所有檔案讀進來。</summary>
        private async Task OnViewAsync(ExceptionLogAdapterModel item)
        {
            detailItem = item;
            detailStackTrace = "讀取中…";
            isStackTraceLoading = true;
            detailVisible = true;
            StateHasChanged();

            var result = await Api.DetailAsync(item.Id);
            if (result.Success && result.Data is { } detail)
            {
                detailStackTrace = string.IsNullOrWhiteSpace(detail.StackTrace)
                    ? "堆疊檔案不存在（可能已被清除，或當初寫檔失敗）。完整日誌請改由「日誌檢視」查詢。"
                    : detail.StackTrace;
            }
            else
            {
                detailStackTrace = $"讀取堆疊失敗。{result.UserMessage()}";
            }

            isStackTraceLoading = false;
            StateHasChanged();
        }

        /// <summary>AI 按鈕的 Tooltip。未設定時直接用伺服器給的原因當標題，讓人知道少填了什麼。</summary>
        private string AiButtonTitle
            => aiAvailable
                ? "AI 分析（產生分析報告，可再追問）"
                : aiUnavailableReason;

        private async Task OnAiAnalyzeAsync()
        {
            if (detailItem is null || aiAnalysisModal is null || isStackTraceLoading)
            {
                return;
            }

            await aiAnalysisModal.OpenAsync(detailItem, maxFollowUpRounds);
        }

        /// <summary>明細窗的「使用者」顯示文字；與複製文字共用同一個格式，只有帳號與 UserId，不含姓名／Email。</summary>
        private string DetailAccountText
            => detailItem is null ? "—" : ExceptionLogClipboardText.FormatAccount(detailItem);

        /// <summary>
        /// 點一下任一列即複製該筆（明細欄位＋完整堆疊），不開明細窗、不寫稽核（與 Server 版相同）。
        /// 「查看」「刪除」按鈕外層在 razor 裡擋掉冒泡，不會進到這裡。
        /// </summary>
        private async Task OnRowClickAsync(RowData<ExceptionLogAdapterModel> row)
        {
            var result = await Api.StackTraceAsync(row.Data.Id);
            if (!result.Success || result.Data is not { } stack)
            {
                logger.LogWarning("Failed to copy exception log to clipboard. ExceptionLogId={ExceptionLogId}, Message={Message}", row.Data.Id, result.UserMessage());
                ViewNotification.Error(notificationService, "複製失敗，請改按「查看」手動選取複製。");
                return;
            }

            await CopyToClipboardAsync(ExceptionLogClipboardText.Build(row.Data, stack.StackTrace), 1);
        }

        /// <summary>
        /// 複製整次查詢結果（與 CSV 匯出同範圍、同排序），每筆與點列複製同格式。
        /// 先問筆數（每筆含完整堆疊，超過 <see cref="CopyAllConfirmThreshold"/> 筆先確認），確認後才向伺服器取全文。
        /// </summary>
        private async Task OnCopyAllAsync()
        {
            var request = BuildRequest(1, 1);
            var counted = await Api.SearchAsync(request);
            if (!counted.Success || counted.Data is not { } page)
            {
                ViewNotification.Error(notificationService, $"複製失敗：{counted.UserMessage()}");
                return;
            }

            var count = maxRows > 0 ? Math.Min(page.TotalCount, maxRows) : page.TotalCount;
            if (count == 0)
            {
                ViewNotification.Warning(notificationService, "目前沒有可複製的例外紀錄。");
                return;
            }

            if (count > CopyAllConfirmThreshold)
            {
                var confirmed = await ConfirmDialog.AskAsync(
                    modalService,
                    "確認複製",
                    $"本次查詢共 {count:N0} 筆（每筆含完整堆疊），內容較大，貼上時可能較慢；大量資料建議改用匯出。",
                    "仍要複製");
                if (confirmed == false)
                {
                    return;
                }
            }

            var result = await Api.CopyTextAsync(request);
            if (!result.Success || result.Data is not { } copy)
            {
                ViewNotification.Error(notificationService, $"複製失敗：{result.UserMessage()}");
                return;
            }

            if (copy.Count == 0)
            {
                ViewNotification.Warning(notificationService, "目前沒有可複製的例外紀錄。");
                return;
            }

            await CopyToClipboardAsync(copy.Text, copy.Count);
        }

        /// <summary>
        /// 走 appClipboard.copyText：https 用 navigator.clipboard，http 內網退回 execCommand，兩種部署都能用。
        /// </summary>
        private async Task CopyToClipboardAsync(string text, int rows)
        {
            try
            {
                var copied = await JSRuntime.InvokeAsync<bool>("appClipboard.copyText", text);
                if (copied)
                {
                    logger.LogInformation("Exception logs copied to clipboard. Rows={Rows}, Characters={Characters}", rows, text.Length);
                    ViewNotification.Info(notificationService, $"已複製 {rows:N0} 筆例外紀錄到剪貼簿。");
                }
                else
                {
                    ViewNotification.Warning(notificationService, "瀏覽器拒絕存取剪貼簿，請手動選取複製。");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to copy exception logs to clipboard. Rows={Rows}", rows);
                ViewNotification.UnexpectedError(notificationService, rows == 1 ? "複製失敗，請改按「查看」手動選取複製。" : "複製失敗，請手動選取複製。", ex);
            }
        }

        private async Task OnDeleteAsync(ExceptionLogAdapterModel item)
        {
            var confirmed = await ConfirmDialog.AskDestructiveAsync(
                modalService,
                "刪除例外紀錄",
                "確定要刪除這一列嗎？相同的例外若再次發生，會重新出現並從 1 次開始計算。",
                "刪除");

            if (confirmed == false)
            {
                return;
            }

            var result = await Api.DeleteAsync(item.Id);
            if (result.Success)
            {
                ViewNotification.Warning(notificationService, "刪除成功");
                await ReloadAsync();
            }
            else
            {
                ViewNotification.Error(notificationService, result.Message);
            }
        }

        /// <summary>「刪除選取」（0.9.132 起）：逐筆呼叫刪除 API，每筆一列稽核（與單筆刪除相同）。</summary>
        private async Task OnBatchDeleteAsync()
        {
            try
            {
                var items = selectedRows.ToList();
                if (items.Count == 0 || !await BatchDelete.AskAsync(modalService, items.Select(Describe).ToList(), restorable: false,
                        note: "相同的例外若再次發生，會重新出現並從 1 次開始計算。"))
                {
                    return;
                }

                batchDeleting = true;
                var outcome = await BatchDelete.RunAsync(items, x => x.Id.ToString(), Describe, x => Api.DeleteAsync(x.Id), logger);
                BatchDelete.Notify(notificationService, outcome);
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while batch deleting exception logs.");
                ViewNotification.UnexpectedError(notificationService, "刪除例外紀錄時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
            finally
            {
                batchDeleting = false;
            }

            static string Describe(ExceptionLogAdapterModel x) => $"{x.LastOccurredAt:yyyy-MM-dd HH:mm:ss} {x.ShortExceptionType}";
        }

        private async Task OnPurgeAsync()
        {
            var confirmed = await ConfirmDialog.AskDestructiveAsync(
                modalService,
                $"清除 {PurgeDays} 天未再發生的紀錄",
                $"將刪除「最後發生」早於 {PurgeDays} 天前的所有紀錄與其堆疊檔案。此動作無法復原。",
                "清除");

            if (confirmed == false)
            {
                return;
            }

            var result = await Api.PurgeAsync();
            if (result.Success)
            {
                ViewNotification.Warning(notificationService, string.IsNullOrWhiteSpace(result.Message) ? "清除完成" : result.Message);
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
                "清空全部例外紀錄",
                "將刪除所有紀錄與整個堆疊檔案目錄。此動作無法復原。",
                "清空");

            if (confirmed == false)
            {
                return;
            }

            var result = await Api.ClearAllAsync();
            if (result.Success)
            {
                ViewNotification.Warning(notificationService, "已清空全部例外紀錄。");
                await ReloadAsync();
            }
            else
            {
                ViewNotification.Error(notificationService, result.UserMessage());
            }
        }

        private async Task OnExportAsync()
        {
            // 匯出目前查詢條件下的全部資料，而非只有當頁 —— 管理員要的是整份對照（伺服器產生檔案並寫匯出稽核）。
            var outcome = await Api.ExportAsync(BuildRequest(1, MagicObjectHelper.PageSize));
            if (!outcome.Success)
            {
                ViewNotification.Error(notificationService, $"匯出失敗：{outcome.Message}");
                return;
            }

            try
            {
                await JSRuntime.DownloadFileAsync(outcome.FileName!, outcome.Content!, outcome.ContentType!);
                logger.LogInformation("Exception log export downloaded. Bytes={Bytes}", outcome.Content!.Length);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Exception log export failed.");
                ViewNotification.UnexpectedError(notificationService, $"匯出失敗：{ex.GetType().Name}。", ex);
            }
        }

        /// <summary>次數越多顏色越重，讓「重複數百次」的噪音一眼可辨。</summary>
        private static string GetCountColor(long count) => count switch
        {
            >= 100 => "red",
            >= 10 => "orange",
            _ => "default",
        };

        private static string GetSourceColor(string source) => source switch
        {
            ExceptionSources.Ui => "blue",
            ExceptionSources.WebApi => "purple",
            ExceptionSources.Startup => "gold",
            ExceptionSources.Background => "cyan",
            ExceptionSources.Process => "volcano",
            ExceptionSources.Browser => "geekblue",
            _ => "default",
        };
    }
}
