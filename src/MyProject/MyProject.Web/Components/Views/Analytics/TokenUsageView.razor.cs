using System.Globalization;
using System.Text;
using System.Text.Json;
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
using MyProject.Web.Diagnostics;

namespace MyProject.Web.Components.Views.Analytics
{
    public partial class TokenUsageView
    {
        private const string TabByAccount = "account";
        private const string TabByOperation = "operation";
        private const string TabByModel = "model";
        private const string TabByCallKind = "callkind";
        private const string TabDetail = "detail";

        private readonly ILogger<TokenUsageView> logger;
        private readonly TokenUsageLogService tokenUsageLogService;
        private readonly ModalService modalService;
        private readonly NotificationService notificationService;

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
        private string activeTabKey = TabByAccount;

        private List<TokenUsageLogAdapterModel> rows = [];
        private TokenUsageSummary summary = new();
        private TokenUsageFilterOptions filterOptions = new();

        private List<TokenUsageGroupRow> groupedByAccount = [];
        private List<TokenUsageGroupRow> groupedByOperation = [];
        private List<TokenUsageGroupRow> groupedByModel = [];
        private List<TokenUsageGroupRow> groupedByCallKind = [];

        private bool detailVisible;
        private TokenUsageLogAdapterModel? detailItem;
        private List<KeyValuePair<string, string>> detailRawRows = [];
        private string detailRawMessage = string.Empty;
        private List<KeyValuePair<string, string>> detailCostRateRows = [];

        [Inject]
        public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

        [Inject]
        public AuthenticationStateProvider authStateProvider { get; set; } = default!;

        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;

        [Inject]
        public IJSRuntime JSRuntime { get; set; } = default!;

        [Inject]
        public IOptions<SystemSettings> SystemSettingsOptions { get; set; } = default!;

        [Inject]
        public CurrentUserService CurrentUserService { get; set; } = default!;

        public TokenUsageView(
            ILogger<TokenUsageView> logger,
            TokenUsageLogService tokenUsageLogService,
            ModalService modalService,
            NotificationService notificationService)
        {
            this.logger = logger;
            this.tokenUsageLogService = tokenUsageLogService;
            this.modalService = modalService;
            this.notificationService = notificationService;
        }

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
            var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
            if (checkResult != AuthenticationCheckResult.Succeeded)
            {
                return;
            }

            isAccessChecked = true;

            // 此頁為管理員專屬：權限鍵刻意未上架角色矩陣，因此以 CheckIsAdmin 直接判斷，
            // 與同子功能表的其他頁面一致。權限未通過前不讀取任何用量資訊。
            if (AuthenticationStateHelper.CheckIsAdmin() == false)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                logger.LogWarning("Usage page denied because the current user is not an administrator.");
                return;
            }

            filterOptions = await tokenUsageLogService.GetFilterOptionsAsync();
            await ReloadAsync();
        }

        private TokenUsageQuery BuildQuery(int pageSize) => new()
        {
            StartDate = startDate,
            EndDate = endDate,
            Account = accountFilter,
            Operation = selectedOperation,
            CallKind = selectedCallKind,
            Model = selectedModel,
            CurrentPage = _pageIndex,
            PageSize = pageSize,
            SortField = sortField,
            SortDescending = sortDirection switch
            {
                "descend" => true,
                "ascend" => false,
                _ => null,
            },
        };

        private async Task ReloadAsync()
        {
            isLoading = true;
            StateHasChanged();

            try
            {
                var query = BuildQuery(_pageSize);

                var page = await tokenUsageLogService.GetAsync(query);
                rows = page.Result.ToList();
                _total = page.Count;

                summary = await tokenUsageLogService.GetSummaryAsync(query);

                groupedByAccount = await tokenUsageLogService.GetGroupedAsync(query, TokenUsageGroupBy.Account);
                groupedByOperation = await tokenUsageLogService.GetGroupedAsync(query, TokenUsageGroupBy.Operation);
                groupedByModel = await tokenUsageLogService.GetGroupedAsync(query, TokenUsageGroupBy.Model);
                groupedByCallKind = await tokenUsageLogService.GetGroupedAsync(query, TokenUsageGroupBy.CallKind);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load LLM usage records.");
                ViewNotification.Error(notificationService, $"載入用量紀錄失敗：{ex.GetType().Name}。");
                rows = [];
                _total = 0;
                summary = new TokenUsageSummary();
            }
            finally
            {
                isLoading = false;
                StateHasChanged();
            }
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
            await ReloadAsync();
        }

        private async Task OnRefreshAsync()
        {
            filterOptions = await tokenUsageLogService.GetFilterOptionsAsync();
            await ReloadAsync();
        }

        /// <summary>開窗當下才讀原始 JSON 檔，清單查詢不會一次把所有檔案讀進記憶體。</summary>
        private async Task OnViewAsync(TokenUsageLogAdapterModel item)
        {
            detailItem = item;
            detailRawRows = [];
            detailRawMessage = "讀取中…";
            // 費率快照就在資料列上，不必再讀檔；沿用同一支平攤器讓呈現與原始明細一致。
            detailCostRateRows = string.IsNullOrWhiteSpace(item.CostRateSnapshot)
                ? []
                : FlattenJson(item.CostRateSnapshot);
            detailVisible = true;
            StateHasChanged();

            var raw = await tokenUsageLogService.GetRawUsageAsync(item.Id);
            if (string.IsNullOrWhiteSpace(raw))
            {
                detailRawMessage = "原始明細不存在（可能已被清除，或當初這次呼叫沒有回傳用量）。";
            }
            else
            {
                detailRawRows = FlattenJson(raw);
                detailRawMessage = detailRawRows.Count == 0 ? "原始明細無法解析。" : string.Empty;
            }

            StateHasChanged();
        }

        /// <summary>
        /// 把 usage JSON 平攤成「路徑 → 值」逐列顯示，例如
        /// <c>prompt_tokens_details.cached_tokens</c>。
        /// 供應商日後新增欄位時不用改程式就看得到。
        /// </summary>
        private static List<KeyValuePair<string, string>> FlattenJson(string json)
        {
            var result = new List<KeyValuePair<string, string>>();

            try
            {
                using var document = JsonDocument.Parse(json);
                Walk(document.RootElement, string.Empty, result);
            }
            catch (JsonException)
            {
                // 檔案內容不是合法 JSON（理論上不會發生），交給呼叫端顯示友善訊息。
            }

            return result;

            static void Walk(JsonElement element, string prefix, List<KeyValuePair<string, string>> output)
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in element.EnumerateObject())
                    {
                        var name = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";
                        Walk(property.Value, name, output);
                    }

                    return;
                }

                var text = element.ValueKind switch
                {
                    JsonValueKind.Number => element.TryGetInt64(out var number)
                        ? TokenUsageFormat.Compact(number)
                        : element.ToString(),
                    JsonValueKind.Null => "—",
                    _ => element.ToString(),
                };

                output.Add(new KeyValuePair<string, string>(prefix, text));
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

            var result = await tokenUsageLogService.DeleteAsync(item.Id);
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

            var result = await tokenUsageLogService.PurgeBeforeAsync(purgeBeforeDate.Value);
            if (result.Success)
            {
                ViewNotification.Warning(notificationService, result.Message);
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
                "清空全部用量紀錄",
                "將刪除所有紀錄與整個原始明細目錄。此動作無法復原。",
                "清空");

            if (confirmed == false)
            {
                return;
            }

            var result = await tokenUsageLogService.ClearAllAsync();
            if (result.Success)
            {
                ViewNotification.Warning(notificationService, result.Message);
                await ReloadAsync();
            }
            else
            {
                ViewNotification.Error(notificationService, result.Message);
            }
        }

        private async Task OnExportCsvAsync()
        {
            try
            {
                // 匯出目前篩選條件下的全部資料，而非只有當頁。
                var query = BuildQuery(int.MaxValue);
                query.CurrentPage = 1;
                var all = await tokenUsageLogService.GetAsync(query);

                var builder = new StringBuilder();
                builder.AppendLine("時間,使用者,作業,型別,供應商,模型,輸入,輸出,推理,快取,圖片輸入,圖片快取,圖片輸出,合計,費用USD,費用TWD,匯率,計價依據,長脈絡,字元數,音訊時長秒,耗時ms,結果,失敗原因");
                foreach (var item in all.Result)
                {
                    builder.AppendLine(string.Join(',',
                        Csv(item.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss")),
                        Csv(item.Account ?? "（系統自動）"),
                        Csv(item.Operation),
                        Csv(item.CallKind),
                        Csv(item.Provider),
                        Csv(item.Model),
                        Csv(item.InputCount?.ToString()),
                        Csv(item.OutputCount?.ToString()),
                        Csv(item.ReasoningCount?.ToString()),
                        Csv(item.CachedInputCount?.ToString()),
                        Csv(item.ImageInputCount?.ToString()),
                        Csv(item.ImageCachedInputCount?.ToString()),
                        Csv(item.ImageOutputCount?.ToString()),
                        Csv(item.TotalCount?.ToString()),
                        Csv(CsvCost(item.CostUsd, "F6")),
                        Csv(CsvCost(item.CostTwd, "F4")),
                        Csv(CsvCost(item.CostExchangeRate, "F4")),
                        Csv(item.CostPriceKey),
                        Csv(item.IsUnpriced ? null : (item.CostLongContext ? "是" : "否")),
                        Csv(item.CharacterCount?.ToString()),
                        Csv(item.DurationSeconds?.ToString()),
                        Csv(item.ElapsedMilliseconds.ToString()),
                        Csv(item.Success ? "成功" : "失敗"),
                        Csv(item.FailureReason)));
                }

                // 匯出檔的 BOM 一律走 TextDownloadPayload；自己接 UTF8Encoding 容易寫成「看起來有、其實沒有」。
                var bytes = TextDownloadPayload.Utf8WithBom(builder.ToString());

                using var stream = new MemoryStream(bytes);
                using var streamReference = new DotNetStreamReference(stream);

                var fileName = $"MyProject.Web-llm-usage-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
                await JSRuntime.InvokeVoidAsync("appFileDownload.downloadFromStream", fileName, streamReference, "text/csv");

                logger.LogInformation("Usage export downloaded. Rows={Rows}", all.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Usage CSV export failed.");
                ViewNotification.Error(notificationService, $"匯出失敗：{ex.GetType().Name}。");
            }
        }

        private async Task OnExportPdfAsync()
        {
            isExportingPdf = true;
            StateHasChanged();

            try
            {
                var query = BuildQuery(int.MaxValue);
                query.CurrentPage = 1;
                var all = await tokenUsageLogService.GetAsync(query);

                var information = SystemSettingsOptions.Value.SystemInformation;
                var bytes = TokenUsageReportPdfBuilder.Build(new TokenUsageReportRequest
                {
                    SystemName = information.SystemName,
                    SystemVersion = information.SystemVersion,
                    OperatorAccount = CurrentUserService.CurrentUser.Account ?? string.Empty,
                    GeneratedAt = DateTime.Now,
                    StartDate = startDate,
                    EndDate = endDate,
                    Account = accountFilter,
                    Operation = selectedOperation,
                    CallKind = selectedCallKind,
                    Model = selectedModel,
                    Summary = summary,
                    ByAccount = groupedByAccount,
                    ByOperation = groupedByOperation,
                    ByModel = groupedByModel,
                    ByCallKind = groupedByCallKind,
                    Details = all.Result.ToList(),
                });

                using var stream = new MemoryStream(bytes);
                using var streamReference = new DotNetStreamReference(stream);

                var fileName = $"MyProject.Web-llm-usage-{DateTime.Now:yyyyMMdd-HHmmss}.pdf";
                await JSRuntime.InvokeVoidAsync(
                    "appFileDownload.downloadFromStream", fileName, streamReference, "application/pdf");

                logger.LogInformation("Usage PDF exported. Bytes={Bytes}, Rows={Rows}", bytes.Length, all.Count);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError(ex, "Usage PDF export failed because the embedded font is missing.");
                ViewNotification.Error(notificationService, "PDF 匯出失敗：缺少內建中文字型，請確認建置產物完整。");
            }
            catch (JSDisconnectedException ex)
            {
                logger.LogWarning(ex, "Usage PDF export aborted because the circuit disconnected.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Usage PDF export failed.");
                ViewNotification.Error(notificationService, $"PDF 匯出失敗：{ex.GetType().Name}。");
            }
            finally
            {
                isExportingPdf = false;
                StateHasChanged();
            }
        }

        /// <summary>
        /// CSV 的金額欄。
        ///
        /// ⚠️ 一律用 F 格式 + InvariantCulture，<b>不能用 N</b>：N 會插千分位逗號，
        /// Excel 會把 "1,234.5678" 當文字匯入，使用者做 SUM 得到 0。
        ///
        /// ⚠️ 未定價輸出空字串而不是 0，否則試算表加總會靜默少算。
        /// </summary>
        private static string? CsvCost(double? value, string format)
            => value?.ToString(format, CultureInfo.InvariantCulture);

        /// <summary>CSV 欄位跳脫：雙引號加倍、整欄以雙引號包住，換行才不會把一列拆成兩列。</summary>
        private static string Csv(string? value)
            => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";
    }
}
