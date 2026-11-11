using System.Globalization;
using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MyProject.Dtos.Admins;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Ai;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Client.Commons;
using MyProject.Web.Components.Commons;
using MyProject.Web.Diagnostics;

namespace MyProject.Web.Client.Views.Analytics
{
    /// <summary>
    /// Token 用量（0.10.5 起在 WASM 執行，WASM 遷移 M6）。畫面、對話窗與提示訊息照 Server 版，資料改走 <see cref="TokenUsageApi"/>（只限管理員）：
    /// 同一份條件一次取回明細、合計、分組、趨勢與摘要卡；「今天」、檔名與 PDF 都由伺服器決定，稽核也由伺服器寫（與 Server 版相同的動作與內容）。
    /// 趨勢圖的分桶與座標走與 PDF 共用的 <see cref="TokenUsageTrendChart"/>，畫出來的 SVG 與 Server 版相同。
    /// 排序修正：Server 版比對 "ascend"/"descend"，排序從來沒生效；這裡依點選方向排序，CSV、PDF 照同一個順序。
    /// </summary>
    public partial class TokenUsageView
    {
        private const string TabByAccount = TokenUsageReportScopes.TabByAccount;
        private const string TabByOperation = TokenUsageReportScopes.TabByOperation;
        private const string TabByModel = TokenUsageReportScopes.TabByModel;
        private const string TabByCallKind = TokenUsageReportScopes.TabByCallKind;
        private const string TabDetail = TokenUsageReportScopes.TabDetail;
        private const string TabDaily = TokenUsageReportScopes.TabDaily;
        private const string TabTrend = TokenUsageReportScopes.TabTrend;

        /// <summary>趨勢清單最多顯示的天數（伺服器截斷，這裡只顯示提示）。</summary>
        private const int MaxTrendDays = TokenUsageTrendChart.MaxTrendDays;

        /// <summary>
        /// 三張摘要卡。標題上的天數與 <see cref="TokenUsageRanges"/> 是同一組常數，
        /// 不可以在這裡另外寫死數字 —— 標題說「近 7 天」就必須真的查 7 天。
        /// </summary>
        private static readonly (int Days, string Label)[] RangeCards =
        [
            (TokenUsageRanges.RecentDay, "近 1 天（今天）"),
            (TokenUsageRanges.RecentWeek, "近 7 天"),
            (TokenUsageRanges.RecentMonth, "近 30 天"),
        ];

        /// <summary>每日列的星期幾與 Server 版一樣用中文（不跟著瀏覽器的語系）。</summary>
        private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo(ClientCulture.Name);

        [Inject]
        public TokenUsageApi Api { get; set; } = default!;

        [Inject]
        public PagePermissions Permissions { get; set; } = default!;

        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        [Inject]
        public IJSRuntime JSRuntime { get; set; } = default!;

        [Inject]
        public ModalService modalService { get; set; } = default!;

        [Inject]
        public NotificationService notificationService { get; set; } = default!;

        [Inject]
        public ILogger<TokenUsageView> logger { get; set; } = default!;

        private ITable? table;

        private string accountFilter = string.Empty;
        private DateTime? startDate;
        private DateTime? endDate;
        private string selectedOperation = string.Empty;
        private string selectedCallKind = string.Empty;
        private string selectedModel = string.Empty;
        private DateTime? purgeBeforeDate;

        private int _pageIndex = 1;
        private int _pageSize = MagicObjectHelper.PageSize;
        private int _total;
        private string sortField = string.Empty;
        private string sortDirection = "None";

        private bool isLoading;
        private bool isExportingPdf;
        private string RoleMessage = string.Empty;
        private bool isAccessChecked;
        // 本頁的主題是費用走勢，所以預設停在「趨勢圖」。
        private string activeTabKey = TabTrend;

        private List<TokenUsageLogAdapterModel> rows = [];
        private IEnumerable<TokenUsageLogAdapterModel> selectedRows = [];
        private bool batchDeleting;
        private TokenUsageSummary summary = new();
        private TokenUsageOptionsDto filterOptions = new();

        private List<TokenUsageGroupRow> groupedByAccount = [];
        private List<TokenUsageGroupRow> groupedByOperation = [];
        private List<TokenUsageGroupRow> groupedByModel = [];
        private List<TokenUsageGroupRow> groupedByCallKind = [];

        /// <summary>「每日趨勢」的資料：伺服器已依有效區間補零，由舊到新，可直接照順序畫。</summary>
        private List<TokenUsageDailyRow> dailyRows = [];

        /// <summary>「趨勢圖」的點：由 <see cref="dailyRows"/> 分桶而來，不另查資料庫。</summary>
        private List<TokenUsageTrendPoint> trendPoints = [];
        private TokenUsageTrendGrain trendGrain;

        /// <summary>近 1／7／30 天三張卡（伺服器以它的「今天」算好，含各自的起訖日）。</summary>
        private List<TokenUsageRangeCardDto> rangeCards = [];

        private double maxDailyCostTwd;
        private long maxDailyTotalCount;
        private bool isTrendTruncated;

        private bool detailVisible;

        /// <summary>這筆用量是否還有對應的 AI 對話紀錄（保留天數不同，舊的用量通常已沒有對話可看）。</summary>
        private bool detailHasConversation;
        private TokenUsageLogAdapterModel? detailItem;
        private List<KeyValuePair<string, string>> detailRawRows = [];
        private string detailRawMessage = string.Empty;
        private List<KeyValuePair<string, string>> detailCostRateRows = [];

        /// <summary>AI 用量上限（伺服器組好的文字與標籤）。</summary>
        private List<TokenUsageQuotaItemDto> quotaStatus = [];
        private string perUserDailyLimit = string.Empty;
        private string perUserMonthlyLimit = string.Empty;

        private static string FormatCompact(long value) => TokenUsageFormat.Compact(value);

        private static string FormatCell(int? value) => TokenUsageFormat.Cell(value);

        private static string FormatCostTwdCell(double? value) => TokenUsageFormat.CostTwdCell(value);

        private static string FormatCostUsdCell(double? value) => TokenUsageFormat.CostUsdCell(value);

        private static string FormatCostTwdTotal(double value) => TokenUsageFormat.CostTwdTotal(value);

        private static string FormatCostUsdTotal(double value) => TokenUsageFormat.CostUsdTotal(value);

        private static string FormatExchangeRate(double? value) => TokenUsageFormat.ExchangeRate(value);

        /// <summary>
        /// 明細列「費用」欄的 Tooltip：美金原價、當時匯率、實際套用的費率鍵。
        ///
        /// ⚠️ 標示「前綴比對」是刻意的，而且是對「套錯兄弟模型費率」唯一的實務防線 ——
        /// 費率若是從別的模型名稱推來的，使用者必須看得見。
        /// </summary>
        private static string BuildCostTooltip(TokenUsageLogAdapterModel item)
        {
            var parts = new List<string>
            {
                $"US$ {FormatCostUsdCell(item.CostUsd)}",
            };

            if (item.CostExchangeRate.HasValue)
            {
                parts.Add($"匯率 {FormatExchangeRate(item.CostExchangeRate)}");
            }

            if (string.IsNullOrEmpty(item.CostPriceKey) == false)
            {
                parts.Add(item.IsPrefixMatched
                    ? $"費率 {item.CostPriceKey}（前綴比對）"
                    : $"費率 {item.CostPriceKey}");
            }

            if (item.CostLongContext)
            {
                parts.Add("長脈絡費率");
            }

            return string.Join("　", parts);
        }

        private string DetailAccountText
        {
            get
            {
                if (detailItem is null)
                {
                    return "—";
                }

                var account = string.IsNullOrWhiteSpace(detailItem.Account) ? "（系統自動）" : detailItem.Account;
                return detailItem.UserId is null ? account : $"{account}（UserId={detailItem.UserId}）";
            }
        }

        /// <summary>計價依據。費率若是前綴比對來的，必須在這裡說清楚是從哪個鍵套來的。</summary>
        private string DetailPriceKeyText
        {
            get
            {
                if (detailItem is null || string.IsNullOrEmpty(detailItem.CostPriceKey))
                {
                    return "—";
                }

                return detailItem.IsPrefixMatched
                    ? $"{detailItem.CostPriceKey}（以前綴比對自 {detailItem.Model}）"
                    : detailItem.CostPriceKey;
            }
        }

        protected override async Task OnInitializedAsync()
        {
            isAccessChecked = true;

            // 此頁為管理員專屬：權限鍵刻意未上架角色矩陣，以 IsAdmin 直接判斷。權限未通過前不讀取任何用量資訊；
            // 沒有權限的開頁稽核由伺服器在開頁紀錄時寫（shell/page-view）。
            if (!Permissions.IsAdmin)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                logger.LogWarning("Usage page denied because the current user is not an administrator.");
                return;
            }

            await ReloadFilterOptionsAsync();
            await ReloadAsync();
        }

        private async Task ReloadFilterOptionsAsync()
        {
            var result = await Api.OptionsAsync();
            if (result.Success && result.Data is { } options)
            {
                filterOptions = options;
            }
            else
            {
                logger.LogWarning("Failed to load usage filter options. Message={Message}", result.UserMessage());
            }
        }

        /// <summary>由目前的工具列狀態組出查詢條件。清單、CSV 與 PDF 共用，避免條件與順序默默分歧（匯出只有分頁不同）。</summary>
        private T BuildRequest<T>(int pageIndex, int pageSize, bool recordSearch = false)
            where T : TokenUsageSearchRequestDto, new() => new()
            {
                // 起訖日當成伺服器的本地日期送出（不帶時差）。
                StartDate = ClientCulture.Unzoned(startDate),
                EndDate = ClientCulture.Unzoned(endDate),
                Account = accountFilter,
                Operation = selectedOperation,
                CallKind = selectedCallKind,
                Model = selectedModel,
                PageIndex = pageIndex,
                PageSize = pageSize,
                SortBy = sortDirection is "Ascending" or "Descending" ? sortField : null,
                SortDescending = sortDirection == "Descending",
                RecordSearch = recordSearch,
            };

        private async Task ReloadAsync(bool recordSearch = false)
        {
            // 換頁、排序、查詢與刪除後都會走到這裡：勾選只在目前這一頁有效（0.9.132 起）。
            selectedRows = [];
            isLoading = true;
            StateHasChanged();

            try
            {
                var result = await Api.DashboardAsync(BuildRequest<TokenUsageSearchRequestDto>(_pageIndex, _pageSize, recordSearch));
                if (result.Success && result.Data is { } data)
                {
                    rows = data.Page.Items.Select(TokenUsageDtoMapper.ToModel).ToList();
                    _total = data.Page.TotalCount;
                    summary = TokenUsageDtoMapper.ToModel(data.Summary);
                    quotaStatus = data.Quota;
                    perUserDailyLimit = data.PerUserDailyLimit;
                    perUserMonthlyLimit = data.PerUserMonthlyLimit;

                    groupedByAccount = data.ByAccount.Select(TokenUsageDtoMapper.ToModel).ToList();
                    groupedByOperation = data.ByOperation.Select(TokenUsageDtoMapper.ToModel).ToList();
                    groupedByModel = data.ByModel.Select(TokenUsageDtoMapper.ToModel).ToList();
                    groupedByCallKind = data.ByCallKind.Select(TokenUsageDtoMapper.ToModel).ToList();

                    rangeCards = data.RangeCards;
                    ApplyTrend(data.Daily.Select(TokenUsageDtoMapper.ToModel).ToList(), data.IsTrendTruncated);
                }
                else
                {
                    ViewNotification.Error(notificationService, $"載入用量紀錄失敗：{result.UserMessage()}");
                    rows = [];
                    _total = 0;
                    summary = new TokenUsageSummary();
                    rangeCards = [];
                    ClearTrend();
                }
            }
            finally
            {
                isLoading = false;
                StateHasChanged();
            }
        }

        private void ApplyTrend(List<TokenUsageDailyRow> daily, bool truncated)
        {
            dailyRows = daily;
            isTrendTruncated = truncated;
            maxDailyCostTwd = dailyRows.Count == 0 ? 0 : dailyRows.Max(x => x.CostTwd);
            maxDailyTotalCount = dailyRows.Count == 0 ? 0 : dailyRows.Max(x => x.TotalCount);

            trendGrain = TokenUsageTrendChart.GrainFor(dailyRows.Count);
            trendPoints = TokenUsageTrendChart.Bucket(dailyRows);
        }

        private void ClearTrend()
        {
            dailyRows = [];
            trendPoints = [];
            maxDailyCostTwd = 0;
            maxDailyTotalCount = 0;
            isTrendTruncated = false;
        }

        /// <summary>近 N 天的摘要卡（伺服器算好）；還沒載入時是全 0。</summary>
        private TokenUsageRangeCardDto CardOf(int days)
            => rangeCards.FirstOrDefault(x => x.Days == days) ?? new TokenUsageRangeCardDto { Days = days };

        /// <summary>點卡片＝把該區間（伺服器的日期）套進起訖日再重查，其餘篩選條件維持不動。</summary>
        private async Task OnApplyRangeAsync(int days)
        {
            if (rangeCards.FirstOrDefault(x => x.Days == days) is not { } card)
            {
                return;
            }

            startDate = card.Date;
            endDate = card.End;
            _pageIndex = 1;
            await ReloadAsync();
        }

        private bool IsRangeActive(int days)
            => rangeCards.FirstOrDefault(x => x.Days == days) is { } card
                && startDate?.Date == card.Date.Date && endDate?.Date == card.End.Date;

        /// <summary>
        /// 上排「篩選範圍合計」那行字：<b>含日期</b>。
        ///
        /// ⚠️ 與 <see cref="ActiveFilterText"/> 的差別就在日期。上排統計的是完整篩選範圍，
        /// 下排的近 N 天卡則會把日期換成自己的窗 —— 兩排都寫「套用條件」卻指不同範圍會誤導，
        /// 所以兩份字串必須看得出差別。
        /// </summary>
        private string FilterScopeText => BuildFilterText(includeDates: true);

        /// <summary>下排卡片上方那行字：目前<b>除了日期以外</b>還套了哪些條件。</summary>
        private string ActiveFilterText => BuildFilterText(includeDates: false);

        /// <summary>
        /// 兩排共用的條件字串組法。抽出來是為了不讓上下排各留一份、日後各自漂移。
        /// </summary>
        private string BuildFilterText(bool includeDates)
        {
            var parts = new List<string>();

            if (includeDates)
            {
                // 兩端都沒設時也要寫出來，否則看不出這排「含日期」而下排不含。
                var start = startDate?.ToString("yyyy-MM-dd") ?? "不限";
                var end = endDate?.ToString("yyyy-MM-dd") ?? "不限";
                parts.Add($"日期 {start} ～ {end}");
            }

            if (string.IsNullOrWhiteSpace(accountFilter) == false)
            {
                parts.Add($"帳號含「{accountFilter}」");
            }

            if (string.IsNullOrEmpty(selectedOperation) == false)
            {
                parts.Add($"作業：{selectedOperation}");
            }

            if (string.IsNullOrEmpty(selectedCallKind) == false)
            {
                parts.Add($"型別：{selectedCallKind}");
            }

            if (string.IsNullOrEmpty(selectedModel) == false)
            {
                parts.Add($"模型：{selectedModel}");
            }

            return parts.Count == 0 ? "全部條件" : string.Join("、", parts);
        }

        /// <summary>
        /// 長條寬度。
        ///
        /// ⚠️ 一定要 <see cref="CultureInfo.InvariantCulture"/> —— CSS 的百分比不接受
        /// 逗號小數點，在以逗號為小數點的地區整條長條會消失（而且不會有任何錯誤訊息）。
        /// </summary>
        private static string BarWidth(double value, double max)
            => max <= 0 ? "0%" : string.Create(CultureInfo.InvariantCulture, $"{value / max * 100:F1}%");

        /// <summary>每日列的日期：「11/11 (週三)」，星期幾固定中文（與 Server 版相同）。</summary>
        private static string DailyLabel(DateTime date) => date.ToString("MM/dd (ddd)", DisplayCulture);

        // 趨勢圖的 viewBox 與繪圖區（SVG 使用者座標）。左邊留給 Y 軸刻度、下面留給日期。
        private const int ChartWidth = 1000;
        private const int ChartHeight = 200;
        private const double PlotLeft = 72;
        private const double PlotRight = ChartWidth - 16;
        private const double PlotTop = 12;
        private const double PlotHeight = ChartHeight - PlotTop - 30;
        private const double PlotWidth = PlotRight - PlotLeft;

        /// <summary>
        /// 上下兩張圖：費用與合計 token。兩者單位不同，所以各自一張、各自一個 Y 軸，
        /// 不做雙 Y 軸 —— 雙軸圖上兩條線的交叉與相對高低沒有意義，只會誤導。
        /// </summary>
        private IEnumerable<(string Title, string CssClass, IReadOnlyList<double> Values, Func<double, string> FormatTick)> TrendCharts =>
        [
            ("費用（NT$）", "token-usage-chart-cost", trendPoints.Select(x => x.CostTwd).ToList(), FormatCostTwdTotal),
            ("合計 token", "token-usage-chart-count", trendPoints.Select(x => (double)x.TotalCount).ToList(), v => FormatCompact((long)v)),
        ];

        private static string TrendTooltip(TokenUsageTrendPoint point)
        {
            var text = $"{TokenUsageTrendChart.PeriodLabel(point)}\n"
                + $"費用 NT$ {FormatCostTwdTotal(point.CostTwd)}（US$ {FormatCostUsdTotal(point.CostUsd)}）\n"
                + $"合計 {FormatCompact(point.TotalCount)} token\n"
                + $"呼叫 {point.CallCount:N0} 次";

            return point.UnpricedCount > 0 ? $"{text}\n其中 {point.UnpricedCount:N0} 筆未定價，未計入費用" : text;
        }

        private Task OnTabChangedAsync(string key)
        {
            activeTabKey = key;
            return Task.CompletedTask;
        }

        private async Task OnTableChange(QueryModel<TokenUsageLogAdapterModel> args)
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
            logger.LogInformation("Usage search triggered.");
            // 搜尋稽核由伺服器寫（只有按「查詢」才帶 RecordSearch）。
            await ReloadAsync(recordSearch: true);
        }

        private async Task OnRefreshAsync()
        {
            await ReloadFilterOptionsAsync();
            await ReloadAsync();
        }

        /// <summary>先開窗（列上已有的資料），再向伺服器讀原始明細、費率快照與對話紀錄是否存在（伺服器寫一筆查看稽核）。</summary>
        private async Task OnViewAsync(TokenUsageLogAdapterModel item)
        {
            detailItem = item;
            detailRawRows = [];
            detailRawMessage = "讀取中…";
            detailCostRateRows = [];
            detailHasConversation = false;
            detailVisible = true;
            StateHasChanged();

            var result = await Api.DetailAsync(item.Id);
            if (result.Success && result.Data is { } detail)
            {
                detailCostRateRows = ToPairs(detail.CostRateRows);
                detailHasConversation = detail.HasConversation;
                detailRawRows = ToPairs(detail.RawRows);
                detailRawMessage = detail.RawMessage;
            }
            else
            {
                detailRawMessage = $"讀取原始明細失敗。{result.UserMessage()}";
            }

            StateHasChanged();
        }

        private static List<KeyValuePair<string, string>> ToPairs(List<TokenUsageFieldDto> fields)
            => fields.Select(x => new KeyValuePair<string, string>(x.Name, x.Value)).ToList();

        /// <summary>到 AI 對話紀錄頁開啟這次呼叫的完整對話（0.9.72 起，以 CallId 關聯）。</summary>
        private void OnViewConversation()
        {
            if (detailItem?.CallId is { } callId)
            {
                NavigationManager.NavigateTo($"/ai-call-logs?callId={callId:D}");
            }
        }

        private async Task OnDeleteAsync(TokenUsageLogAdapterModel item)
        {
            var confirmed = await ConfirmDialog.AskDestructiveAsync(
                modalService,
                "刪除用量紀錄",
                "確定要刪除這一筆嗎？原始明細檔也會一併移除。",
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

        /// <summary>「刪除選取」（0.9.132 起）：逐筆呼叫刪除 API，規則與稽核同單筆刪除。</summary>
        private async Task OnBatchDeleteAsync()
        {
            try
            {
                var items = selectedRows.ToList();
                if (items.Count == 0 || !await BatchDelete.AskAsync(modalService, items.Select(Describe).ToList(), restorable: false,
                        note: "原始明細檔也會一併移除。"))
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
                logger.LogError(ex, "Unhandled exception while batch deleting token usage logs.");
                ViewNotification.UnexpectedError(notificationService, "刪除用量紀錄時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
            finally
            {
                batchDeleting = false;
            }

            static string Describe(TokenUsageLogAdapterModel x) => $"{x.OccurredAt:yyyy-MM-dd HH:mm:ss} {x.Operation}";
        }

        private async Task OnPurgeAsync()
        {
            if (purgeBeforeDate is null)
            {
                return;
            }

            var confirmed = await ConfirmDialog.AskDestructiveAsync(
                modalService,
                "批次清除",
                $"將刪除 {purgeBeforeDate.Value:yyyy-MM-dd} 之前的所有用量紀錄與原始明細檔。此動作無法復原。",
                "清除");

            if (confirmed == false)
            {
                return;
            }

            var result = await Api.PurgeAsync(ClientCulture.Unzoned(purgeBeforeDate)!.Value);
            if (result.Success)
            {
                ViewNotification.Warning(notificationService, result.Message);
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
                "清空全部用量紀錄",
                "將刪除所有紀錄與整個原始明細目錄。此動作無法復原。",
                "清空");

            if (confirmed == false)
            {
                return;
            }

            var result = await Api.ClearAllAsync();
            if (result.Success)
            {
                ViewNotification.Warning(notificationService, result.Message);
                await ReloadAsync();
            }
            else
            {
                ViewNotification.Error(notificationService, result.UserMessage());
            }
        }

        private async Task OnExportCsvAsync()
        {
            // 匯出目前篩選條件下的全部資料，而非只有當頁（伺服器產生檔案並寫匯出稽核）。
            var outcome = await Api.ExportCsvAsync(BuildRequest<TokenUsageSearchRequestDto>(1, MagicObjectHelper.PageSize));
            if (!outcome.Success)
            {
                ViewNotification.Error(notificationService, $"匯出失敗：{outcome.Message}");
                return;
            }

            try
            {
                await JSRuntime.DownloadFileAsync(outcome.FileName!, outcome.Content!, outcome.ContentType!);
                logger.LogInformation("Usage export downloaded. Bytes={Bytes}", outcome.Content!.Length);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Usage CSV export failed.");
                ViewNotification.UnexpectedError(notificationService, $"匯出失敗：{ex.GetType().Name}。", ex);
            }
        }

        /// <summary>目前選中的頁籤對應的報表範圍。</summary>
        private TokenUsageReportScope ActiveScope => TokenUsageReportScopes.FromTab(activeTabKey) ?? TokenUsageReportScope.Detail;

        /// <summary>按鈕提示用的頁籤名稱，與報表標題共用同一份對照表。</summary>
        private string ActiveTabLabel => TokenUsageReportScopes.Describe(ActiveScope);

        /// <summary>
        /// 匯出 PDF。<paramref name="scope"/> 為 <see cref="TokenUsageReportScope.All"/> 時是整份報表，
        /// 否則只輸出目前這一個頁籤（標題、條件區與合計仍然保留）。報表由伺服器依同一份條件產生（伺服器寫匯出稽核）。
        /// </summary>
        private async Task OnExportPdfAsync(TokenUsageReportScope scope)
        {
            isExportingPdf = true;
            StateHasChanged();

            try
            {
                var request = BuildRequest<TokenUsagePdfExportRequestDto>(1, MagicObjectHelper.PageSize);
                request.Tab = scope is TokenUsageReportScope.All ? null : activeTabKey;
                var outcome = await Api.ExportPdfAsync(request);
                if (!outcome.Success)
                {
                    ViewNotification.Error(notificationService, $"PDF 匯出失敗：{outcome.Message}");
                    return;
                }

                await JSRuntime.DownloadFileAsync(outcome.FileName!, outcome.Content!, outcome.ContentType!);
                logger.LogInformation("Usage PDF exported. Scope={Scope}, Bytes={Bytes}", scope, outcome.Content!.Length);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Usage PDF export failed.");
                ViewNotification.UnexpectedError(notificationService, $"PDF 匯出失敗：{ex.GetType().Name}。", ex);
            }
            finally
            {
                isExportingPdf = false;
                StateHasChanged();
            }
        }
    }
}
