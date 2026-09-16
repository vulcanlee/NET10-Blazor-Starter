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

        protected override async Task OnInitializedAsync()
        {
            var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
            if (checkResult != AuthenticationCheckResult.Succeeded)
            {
                return;
            }

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
            var confirmed = await modalService.ConfirmAsync(new ConfirmOptions
            {
                Title = "刪除用量紀錄",
                Content = "確定要刪除這一筆嗎？原始明細檔也會一併移除。",
                OkText = "刪除",
                CancelText = "取消",
            });

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

            var confirmed = await modalService.ConfirmAsync(new ConfirmOptions
            {
                Title = "批次清除",
                Content = $"將刪除 {purgeBeforeDate.Value:yyyy-MM-dd} 之前的所有用量紀錄與原始明細檔。此動作無法復原。",
                OkText = "清除",
                CancelText = "取消",
            });

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
            var confirmed = await modalService.ConfirmAsync(new ConfirmOptions
            {
                Title = "清空全部用量紀錄",
                Content = "將刪除所有紀錄與整個原始明細目錄。此動作無法復原。",
                OkText = "清空",
                CancelText = "取消",
            });

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
                builder.AppendLine("時間,使用者,作業,型別,供應商,模型,輸入,輸出,推理,快取,合計,音訊時長秒,耗時ms,結果,失敗原因");
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
                        Csv(item.TotalCount?.ToString()),
                        Csv(item.DurationSeconds?.ToString()),
                        Csv(item.ElapsedMilliseconds.ToString()),
                        Csv(item.Success ? "成功" : "失敗"),
                        Csv(item.FailureReason)));
                }

                // 加 BOM，否則 Excel 開啟繁體中文會亂碼。
                // ⚠️ UTF8Encoding.GetBytes 不會輸出前導碼（那個旗標只影響 GetPreamble 與 StreamWriter），
                // 必須自己把 GetPreamble() 接在前面，否則設了旗標也還是沒有 BOM。
                var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
                var preamble = encoding.GetPreamble();
                var body = encoding.GetBytes(builder.ToString());
                var bytes = new byte[preamble.Length + body.Length];
                preamble.CopyTo(bytes, 0);
                body.CopyTo(bytes, preamble.Length);

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

        /// <summary>CSV 欄位跳脫：雙引號加倍、整欄以雙引號包住，換行才不會把一列拆成兩列。</summary>
        private static string Csv(string? value)
            => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";
    }
}
