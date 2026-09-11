using System.Text;
using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Ai;
using MyProject.Web.Components.Commons;
using MyProject.Web.Diagnostics;

namespace MyProject.Web.Components.Views.Analytics
{
    public partial class LogViewerView
    {
        private static readonly (string Key, string Label)[] LevelOptions =
        [
            ("", "不限"),
            ("TRACE", "TRACE"),
            ("DEBUG", "DEBUG"),
            ("INFO", "INFO"),
            ("WARN", "WARN"),
            ("ERROR", "ERROR"),
            ("FATAL", "FATAL"),
        ];

        /// <summary>
        /// AI 回傳內容轉成 HTML 之後的顯示上限。
        ///
        /// 0.9.7 起 MaxOutputTokens 預設不送（交給模型決定），所以這裡是**主要**防線而非第二道：
        /// 異常龐大的 HTML 會讓單次 render diff 過肥、瀏覽器記憶體升高、對話窗開啟卡頓。
        /// </summary>
        private const int MaxRenderedHtmlLength = 512 * 1024;

        private readonly ILogger<LogViewerView> logger;
        private readonly ILogQueryService logQueryService;

        // ⚠️ 本頁的提示一律走 ViewNotification（右下角卡片），不用 MessageService（頂部輕量 toast）。
        // AI 分析的階段提示與失敗原因都偏長，頂部那條會被截斷也容易被忽略。
        private readonly NotificationService notificationService;
        private readonly IAiLogAnalysisService aiLogAnalysisService;
        private readonly IAuditLogService auditLogService;
        private readonly CurrentUserService currentUserService;

        private DateTime? startTime;
        private DateTime? endTime;
        private string minimumLevel = string.Empty;
        private int takeCount = LogQueryRequest.DefaultTake;
        private string keyword = string.Empty;

        private bool isLoading;
        private string statusMessage = string.Empty;
        private List<string> warnings = new();

        /// <summary>查詢結果，時間正序。匯出直接使用此順序。</summary>
        private List<LogEntry> entriesAscending = new();

        /// <summary>畫面顯示用，最新在上。</summary>
        private List<LogEntry> entriesDisplay = new();

        private string RoleMessage = string.Empty;

        // AI 分析狀態。
        private bool aiAvailable;
        private string aiUnavailableReason = string.Empty;
        private bool aiModalVisible;
        private bool isAiRunning;
        private bool isAiExporting;
        private string aiMarkdown = string.Empty;
        private string aiHtml = string.Empty;
        private AiAnalysisResult? aiResult;
        private List<KeyValuePair<string, string>> aiMetaItems = new();

        /// <summary>
        /// AI 按鈕的 Tooltip。未設定時直接用服務給的原因當標題，不再包一層「AI 分析（…）」
        /// —— 原因本身就以「AI 分析尚未…」開頭，包起來會變成重複的贅字。
        /// </summary>
        private string AiButtonTitle => aiAvailable
            ? "AI 分析（將本次查詢結果送給 AI 整理）"
            : aiUnavailableReason;

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

        public LogViewerView(
            ILogger<LogViewerView> logger,
            ILogQueryService logQueryService,
            NotificationService notificationService,
            IAiLogAnalysisService aiLogAnalysisService,
            IAuditLogService auditLogService,
            CurrentUserService currentUserService)
        {
            this.logger = logger;
            this.logQueryService = logQueryService;
            this.notificationService = notificationService;
            this.aiLogAnalysisService = aiLogAnalysisService;
            this.auditLogService = auditLogService;
            this.currentUserService = currentUserService;
        }

        protected override async Task OnInitializedAsync()
        {
            var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
            if (checkResult != AuthenticationCheckResult.Succeeded)
            {
                return;
            }

            // 此頁為管理員專屬：權限鍵刻意未上架角色矩陣，因此以 CheckIsAdmin 直接判斷，
            // 與既有的系統健康監控頁一致。權限未通過前不讀取任何日誌內容。
            if (AuthenticationStateHelper.CheckIsAdmin() == false)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                logger.LogWarning("Log viewer access denied because the current user is not an administrator.");
                return;
            }

            aiAvailable = aiLogAnalysisService.IsAvailable;
            aiUnavailableReason = aiLogAnalysisService.UnavailableReason;

            endTime = DateTime.Now;
            startTime = endTime.Value.AddHours(-1);

            await OnQueryAsync();
        }

        private async Task OnQueryAsync()
        {
            isLoading = true;
            statusMessage = string.Empty;
            warnings = new();
            StateHasChanged();

            try
            {
                var request = new LogQueryRequest
                {
                    StartTime = startTime ?? DateTime.Now.AddHours(-1),
                    EndTime = endTime ?? DateTime.Now,
                    Take = takeCount,
                    MinimumLevel = ToRank(minimumLevel),
                    Keyword = keyword,
                };

                var result = await logQueryService.QueryAsync(request);

                entriesAscending = result.Entries.ToList();
                entriesDisplay = Enumerable.Reverse(entriesAscending).ToList();
                statusMessage = result.Message;
                warnings = result.Warnings;

                // 條件被夾住時把實際查詢的區間寫回選擇器，讓畫面與結果一致。
                startTime = result.AppliedStartTime;
                endTime = result.AppliedEndTime;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Log query failed.");
                statusMessage = $"查詢日誌失敗：{ex.GetType().Name}。";
                entriesAscending = new();
                entriesDisplay = new();
            }
            finally
            {
                isLoading = false;
                StateHasChanged();
            }
        }

        private async Task OnExportAsync()
        {
            if (entriesAscending.Count == 0)
            {
                ViewNotification.Warning(notificationService, "目前沒有可匯出的日誌。");
                return;
            }

            try
            {
                // 服務回傳的即為時間正序，直接沿用，不依賴可能解析失敗的 Timestamp 重新排序。
                var text = string.Join(Environment.NewLine, entriesAscending.Select(entry => entry.Raw));

                // 加 BOM，避免記事本／Excel 開啟時繁體中文亂碼。
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(text);

                using var stream = new MemoryStream(bytes);
                using var streamReference = new DotNetStreamReference(stream);

                var fileName = $"MyProject.Web-logs-{DateTime.Now:yyyyMMdd-HHmmss}.log";
                await JSRuntime.InvokeVoidAsync("appFileDownload.downloadFromStream", fileName, streamReference);

                logger.LogInformation(
                    "Log export downloaded. Rows={Rows}, Bytes={Bytes}", entriesAscending.Count, bytes.Length);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Log export failed.");
                ViewNotification.Error(notificationService, $"匯出失敗：{ex.GetType().Name}。");
            }
        }

        private async Task OnAiAnalyzeAsync()
        {
            try
            {
                await OnAiAnalyzeCoreAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while running AI log analysis.");
                ViewNotification.Error(notificationService, "AI 分析發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
            }
            finally
            {
                isAiRunning = false;
                StateHasChanged();
            }
        }

        /// <summary>
        /// 送出的是畫面上「現有的」查詢結果，刻意不重新查詢：行為完全可預測，
        /// 而對話窗會標明分析的時間區間、等級、關鍵字與筆數，不會讓人搞混。
        /// </summary>
        private async Task OnAiAnalyzeCoreAsync()
        {
            if (entriesAscending.Count == 0)
            {
                ViewNotification.Warning(notificationService, "目前沒有可分析的日誌。");
                return;
            }

            isAiRunning = true;
            StateHasChanged();

            // 呼叫前先告知階段，AI 回應可能要數十秒，不能讓畫面看起來沒反應。
            ViewNotification.Info(notificationService, $"正在將 {entriesAscending.Count} 筆日誌送給 AI 分析，可能需要數十秒…");

            var result = await aiLogAnalysisService.AnalyzeAsync(entriesAscending);
            await WriteAiAuditAsync("LogViewer.AiAnalyze", result);

            if (result.Success == false)
            {
                logger.LogWarning(
                    "AI log analysis was not successful. Reason={Reason}, Entries={Entries}",
                    result.Reason,
                    result.Prompt.IncludedEntryCount);
                ViewNotification.Error(notificationService, result.ErrorMessage);
                return;
            }

            var html = AiMarkdownRenderer.ToSafeHtml(result.Markdown);
            if (html.Length > MaxRenderedHtmlLength)
            {
                logger.LogWarning(
                    "AI log analysis response was too large to render. Characters={Characters}", html.Length);
                ViewNotification.Error(notificationService, "AI 回傳內容異常龐大，已中止顯示。請縮小查詢範圍後再試。");
                return;
            }

            aiResult = result;
            aiMarkdown = result.Markdown;
            aiHtml = html;
            aiMetaItems = BuildAiMetaItems(result);
            aiModalVisible = true;

            if (result.Prompt.DroppedByEntryLimit)
            {
                ViewNotification.Warning(notificationService,
                    $"查詢共 {result.Prompt.TotalEntryCount} 筆，因筆數上限僅分析最新 {result.Prompt.IncludedEntryCount} 筆。");
            }

            // 一份看起來完整、實際被切掉結論的分析報告，比明講「可能不完整」更危險。
            if (result.IsTruncatedByLength)
            {
                ViewNotification.Warning(notificationService,
                    "回應已達長度上限，結尾可能不完整。請調高 AiSettings:MaxOutputTokens 或將它設為 null。");
            }

            ViewNotification.Info(notificationService, $"AI 分析完成（分析了 {result.Prompt.IncludedEntryCount} 筆）。");
        }

        private async Task OnAiCopyAsync()
        {
            try
            {
                // 複製原始 Markdown 而非渲染後的 HTML：使用者會貼進工單或通訊軟體，
                // Markdown 才是有用的格式。
                var copied = await JSRuntime.InvokeAsync<bool>("appClipboard.copyText", aiMarkdown);
                if (copied)
                {
                    ViewNotification.Info(notificationService, "已複製分析結果。");
                }
                else
                {
                    ViewNotification.Warning(notificationService, "瀏覽器拒絕存取剪貼簿，請手動選取複製。");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to copy AI analysis result to clipboard.");
                ViewNotification.Error(notificationService, "複製失敗，請手動選取複製。");
            }
        }

        private async Task OnAiExportPdfAsync()
        {
            if (aiResult is null)
            {
                return;
            }

            isAiExporting = true;
            StateHasChanged();

            try
            {
                await OnAiExportPdfCoreAsync(aiResult);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError(ex, "AI analysis PDF export failed because the embedded font is missing.");
                ViewNotification.Error(notificationService, "PDF 匯出失敗：缺少內建中文字型，請確認建置產物完整。");
            }
            catch (JSDisconnectedException ex)
            {
                logger.LogWarning(ex, "AI analysis PDF export aborted because the circuit disconnected.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AI analysis PDF export failed.");
                ViewNotification.Error(notificationService, $"PDF 匯出失敗：{ex.GetType().Name}。");
            }
            finally
            {
                isAiExporting = false;
                StateHasChanged();
            }
        }

        private async Task OnAiExportPdfCoreAsync(AiAnalysisResult result)
        {
            ViewNotification.Info(notificationService, "正在產生 PDF…");

            var information = SystemSettingsOptions.Value.SystemInformation;
            var currentUser = currentUserService.CurrentUser;

            var bytes = AiReportPdfBuilder.Build(new AiReportPdfRequest
            {
                SystemName = information.SystemName,
                SystemVersion = information.SystemVersion,
                OperatorAccount = currentUser.Account ?? string.Empty,
                GeneratedAt = DateTime.Now,
                QueryStartTime = startTime ?? DateTime.Now.AddHours(-1),
                QueryEndTime = endTime ?? DateTime.Now,
                MinimumLevel = minimumLevel,
                Keyword = keyword,
                Markdown = result.Markdown,
                Prompt = result.Prompt,
                Usage = result.Usage,
                ModelName = result.ModelName,
            });

            using var stream = new MemoryStream(bytes);
            using var streamReference = new DotNetStreamReference(stream);

            var fileName = $"MyProject.Web-ai-log-report-{DateTime.Now:yyyyMMdd-HHmmss}.pdf";
            await JSRuntime.InvokeVoidAsync(
                "appFileDownload.downloadFromStream", fileName, streamReference, "application/pdf");

            logger.LogInformation(
                "AI analysis PDF exported. Bytes={Bytes}, Entries={Entries}",
                bytes.Length,
                result.Prompt.IncludedEntryCount);

            await WriteAiAuditAsync("LogViewer.AiAnalyzeExportPdf", result);

            ViewNotification.Info(notificationService, "PDF 已產生並開始下載。");
        }

        private void OnAiModalCancel()
        {
            aiModalVisible = false;
        }

        /// <summary>
        /// 對話窗頁首的中介資訊。沿用 MainLayout「關於」視窗的 aboutItems 模式：
        /// 「有值才顯示」等於「不加那一列」，不必在 Razor 裡寫一堆條件判斷。
        /// </summary>
        private List<KeyValuePair<string, string>> BuildAiMetaItems(AiAnalysisResult result)
        {
            var items = new List<KeyValuePair<string, string>>
            {
                new("查詢區間",
                    $"{startTime:yyyy-MM-dd HH:mm:ss} ～ {endTime:yyyy-MM-dd HH:mm:ss}"),
                new("最低等級", string.IsNullOrWhiteSpace(minimumLevel) ? "不限" : minimumLevel),
            };

            if (string.IsNullOrWhiteSpace(keyword) == false)
            {
                items.Add(new KeyValuePair<string, string>("關鍵字", keyword));
            }

            var scope = $"分析 {result.Prompt.IncludedEntryCount} 筆／查詢 {result.Prompt.TotalEntryCount} 筆";
            if (result.Prompt.DroppedByEntryLimit)
            {
                scope += "（已依筆數上限取最新資料）";
            }

            items.Add(new KeyValuePair<string, string>("分析範圍", scope));

            if (string.IsNullOrWhiteSpace(result.ModelName) == false)
            {
                items.Add(new KeyValuePair<string, string>("使用模型", result.ModelName));
            }

            var usage = BuildUsageText(result.Usage);
            if (string.IsNullOrEmpty(usage) == false)
            {
                items.Add(new KeyValuePair<string, string>("用量", usage));
            }

            items.Add(new KeyValuePair<string, string>("耗時", $"{result.Elapsed.TotalSeconds:F1} 秒"));
            return items;
        }

        /// <summary>只列出 API 真的有回的欄位；全都沒回就回空字串，呼叫端不加這一列。</summary>
        private static string BuildUsageText(AiTokenUsage? usage)
        {
            if (usage is null || usage.HasAny == false)
            {
                return string.Empty;
            }

            var parts = new List<string>(5);
            Append(parts, "輸入", usage.InputCount);
            Append(parts, "輸出", usage.OutputCount);
            Append(parts, "合計", usage.TotalCount);
            Append(parts, "快取輸入", usage.CachedInputCount);
            Append(parts, "推論", usage.ReasoningCount);
            return string.Join("　", parts);

            static void Append(List<string> parts, string label, int? value)
            {
                if (value.HasValue)
                {
                    parts.Add($"{label} {value.Value:N0}");
                }
            }
        }

        /// <summary>
        /// 稽核寫入失敗不應推翻「分析已經完成」這個事實，因此只記錯誤不向使用者報錯。
        ///
        /// ⚠️ detail 只寫數量、模型、用量與耗時，<b>絕不</b>寫入任何日誌內容或 AI 輸出：
        /// AuditLog 的可視範圍比本頁更廣，把日誌內容複寫進去等於繞過本頁的管理員限制。
        /// </summary>
        private async Task WriteAiAuditAsync(string action, AiAnalysisResult? result)
        {
            if (result is null)
            {
                return;
            }

            try
            {
                var currentUser = currentUserService.CurrentUser;
                await auditLogService.WriteAsync(
                    action,
                    success: result.Success,
                    actorUserId: currentUser.Id,
                    actorAccount: currentUser.Account,
                    targetType: "LogQuery",
                    targetId: $"{startTime:yyyyMMddHHmmss}-{endTime:yyyyMMddHHmmss}",
                    detail: BuildAuditDetail(result));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to write audit log for AI log analysis. Action={Action}", action);
            }
        }

        private static string BuildAuditDetail(AiAnalysisResult result)
        {
            var builder = new StringBuilder();
            builder.Append("送出 ").Append(result.Prompt.IncludedEntryCount)
                .Append('/').Append(result.Prompt.TotalEntryCount)
                .Append(" 筆、").Append(result.Prompt.CharacterCount).Append(" 字元");

            if (string.IsNullOrWhiteSpace(result.ModelName) == false)
            {
                builder.Append("；模型 ").Append(result.ModelName);
            }

            var usage = BuildUsageText(result.Usage);
            if (string.IsNullOrEmpty(usage) == false)
            {
                builder.Append("；用量 ").Append(usage);
            }

            builder.Append("；耗時 ").Append(result.Elapsed.TotalMilliseconds.ToString("F0")).Append(" ms");

            if (result.Success == false)
            {
                builder.Append("；失敗原因 ").Append(result.Reason);
            }

            return builder.ToString();
        }

        // 解析使用者的篩選輸入時，空字串代表「不限」，因此 fallback 為 Any。
        private static LogLevelRank ToRank(string level)
            => LogLevelRankHelper.FromLevelText(level, LogLevelRank.Any);

        private static string GetLevelColor(LogLevelRank rank) => rank switch
        {
            LogLevelRank.Fatal => "red",
            LogLevelRank.Error => "red",
            LogLevelRank.Warn => "orange",
            LogLevelRank.Info => "blue",
            LogLevelRank.Debug => "cyan",
            _ => "default",
        };
    }
}
