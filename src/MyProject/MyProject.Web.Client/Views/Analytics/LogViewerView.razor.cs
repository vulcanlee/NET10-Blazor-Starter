using System.Globalization;
using System.Text;
using AntDesign;
using AntDesign.TableModels;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Ai;
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
    /// 日誌檢視＋AI 日誌分析（0.10.5 起在 WASM 執行，WASM 遷移 M6）。畫面、對話窗與每一句提示照 Server 版，資料改走 <see cref="LogViewerApi"/>（只限管理員）：
    /// 查詢、匯出、AI 分析（NDJSON 串流）、PDF 都在伺服器做，稽核也由伺服器寫（與 Server 版相同的動作與內容）。
    /// 「現在」一律由伺服器算（預設區間、快速區間、檔名、PDF 產生時間）；查前後 1 分鐘、展開／收合、複製用已載入的資料在瀏覽器做（複製不寫稽核，同 Server 版）。
    /// 匯出、AI 分析與 PDF 送的是上一次查詢「實際使用」的條件（<see cref="lastQuery"/>），伺服器依它重新讀日誌檔，所以分析的就是畫面上這份結果。
    /// </summary>
    public partial class LogViewerView : IDisposable
    {
        /// <summary>AI 分析對話窗的狀態。</summary>
        private enum AiModalState
        {
            /// <summary>已送出、還沒收到任何文字，顯示等待畫面。</summary>
            Running,

            /// <summary>
            /// 顯示報告。0.9.141 起串流：<c>isAiRunning</c> 為 true 時代表還在逐段產生中。
            /// </summary>
            Succeeded,

            /// <summary>還沒收到任何文字就被使用者按「停止產生」（0.9.141 起）。</summary>
            Stopped,

            /// <summary>失敗，把原因留在窗內讓使用者讀完再自己關。</summary>
            Failed,
        }

        /// <summary>
        /// 對話窗的字級選項。百分比同時是 CSS 倍率的來源與「哪一顆被選中」的識別值。
        /// </summary>
        private static readonly (int Percent, string Label)[] FontScaleOptions =
        [
            (100, "一般"),
            (125, "中"),
            (150, "大"),
        ];

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

        /// <summary>每次開窗都回到這一級。字級選擇刻意不跨工作階段保存，理由見功能文件。</summary>
        private const int DefaultFontScalePercent = 100;

        /// <summary>
        /// 「複製查詢結果」超過這個筆數先確認。上限 10,000 筆可能是數 MB 的文字，
        /// 貼上時目標程式會卡一下；這種量改用匯出比較合適，但仍讓使用者自己決定。
        /// </summary>
        private const int CopyAllConfirmThreshold = 1000;

        /// <summary>伺服器 <c>AiAnalysisFailureReason.Canceled</c> 的名稱（串流結果的 <see cref="AiResultDto.Reason"/>）。</summary>
        private const string CanceledReason = "Canceled";

        /// <summary>串流中重新轉 HTML 的節流：太頻繁會讓長報告每一段都整份重轉、重送。</summary>
        private static readonly TimeSpan AiStreamRenderInterval = TimeSpan.FromMilliseconds(150);

        // ⚠️ 本頁的提示一律走 ViewNotification（右下角卡片），不用 MessageService（頂部輕量 toast）。
        // AI 分析的階段提示與失敗原因都偏長，頂部那條會被截斷也容易被忽略。
        [Inject]
        public NotificationService NotificationService { get; set; } = default!;

        [Inject]
        public LogViewerApi Api { get; set; } = default!;

        [Inject]
        public PagePermissions Permissions { get; set; } = default!;

        [Inject]
        public ModalService ModalService { get; set; } = default!;

        [Inject]
        public IJSRuntime JSRuntime { get; set; } = default!;

        [Inject]
        public ILogger<LogViewerView> Logger { get; set; } = default!;

        private Table<LogEntryDto>? logTable;

        /// <summary>
        /// 「全部展開」的狀態。綁在 DefaultExpandAllRows 上，換頁後新渲染的列也跟著展開；
        /// 按「全部收合」或重新查詢才回到收合。
        /// </summary>
        private bool isExpandAll;

        private DateTime? startTime;
        private DateTime? endTime;
        private string minimumLevel = string.Empty;
        private int takeCount = LogSearchRequestDto.DefaultTake;
        private string keyword = string.Empty;
        private string traceCode = string.Empty;

        private bool isLoading;
        private string statusMessage = string.Empty;
        private List<string> warnings = new();

        /// <summary>查詢結果，時間正序。複製直接使用此順序（與伺服器匯出的 .log 相同）。</summary>
        private List<LogEntryDto> entriesAscending = new();

        /// <summary>畫面顯示用，最新在上。</summary>
        private List<LogEntryDto> entriesDisplay = new();

        /// <summary>
        /// 產生目前這份結果的條件（起訖是伺服器實際使用的值）。匯出、AI 分析與 PDF 都送這一份，
        /// 使用者查詢後又改了工具列但沒按查詢時，處理的仍是畫面上的這份結果（Server 版直接用畫面上的資料，效果相同）。
        /// </summary>
        private LogSearchRequestDto? lastQuery;

        private string RoleMessage = string.Empty;
        private bool isAccessChecked;

        // AI 分析狀態。
        private bool aiAvailable;
        private string aiUnavailableReason = string.Empty;
        private bool aiModalVisible;
        private bool isAiRunning;
        private bool isAiExporting;
        private string aiMarkdown = string.Empty;
        private string aiHtml = string.Empty;
        private AiResultDto? aiResult;
        private List<KeyValuePair<string, string>> aiMetaItems = new();

        // 0.9.8：對話窗改成按下按鈕就開，等待與失敗都在窗內呈現。
        private AiModalState aiModalState = AiModalState.Succeeded;
        private string aiErrorMessage = string.Empty;
        private int aiSubmittedCount;
        private int aiElapsedSeconds;
        private int aiFontScalePercent = DefaultFontScalePercent;

        /// <summary>
        /// 本次分析的取消來源。使用者在等待中關窗即取消（中斷請求，伺服器跟著取消 AI 呼叫），等待計時也綁在同一個 token 上。
        /// </summary>
        private CancellationTokenSource? aiCts;

        /// <summary>
        /// 這次取消是「停止產生」（保留已產生的內容）而不是關窗（放棄並丟棄）（0.9.141 起）。
        /// 兩者都是取消同一個 token，只能靠這個旗標區分。
        /// </summary>
        private bool aiStopRequested;

        /// <summary>目前顯示的報告是產生途中停止的（不完整）。</summary>
        private bool aiInterrupted;

        /// <summary>元件已釋放（使用者導航離開）。</summary>
        private bool isDisposed;

        /// <summary>
        /// AI 按鈕的 Tooltip。未設定時直接用服務給的原因當標題，不再包一層「AI 分析（…）」
        /// —— 原因本身就以「AI 分析尚未…」開頭，包起來會變成重複的贅字。
        /// </summary>
        private string AiButtonTitle => aiAvailable
            ? "AI 分析（將本次查詢結果送給 AI 整理）"
            : aiUnavailableReason;

        /// <summary>對話窗標題隨狀態變，讓使用者從標題就看得出現在是哪一段。</summary>
        private string AiModalTitle => aiModalState switch
        {
            AiModalState.Running => "AI 日誌分析（進行中）",
            AiModalState.Failed => "AI 日誌分析失敗",
            AiModalState.Stopped => "AI 日誌分析（已停止）",
            _ when isAiRunning => "AI 日誌分析（產生中）",
            _ when aiInterrupted => "AI 日誌分析結果（已中止）",
            _ => "AI 日誌分析結果",
        };

        /// <summary>
        /// `--ai-font-scale` 的值。
        ///
        /// ⚠️ 一定要用 <see cref="CultureInfo.InvariantCulture"/>：若當下文化把小數點寫成逗號，
        /// 產出的會是無效的 CSS 值 <c>1,25</c>，字級靜默失效 —— 開發機上永遠看不到。
        /// </summary>
        private string AiFontScaleCss
            => (aiFontScalePercent / 100.0).ToString("0.##", CultureInfo.InvariantCulture);

        protected override async Task OnInitializedAsync()
        {
            isAccessChecked = true;

            // 此頁為管理員專屬：權限鍵刻意未上架角色矩陣，以 IsAdmin 直接判斷。權限未通過前不讀取任何日誌內容；
            // 沒有權限的開頁稽核由伺服器在開頁紀錄時寫（shell/page-view）。
            if (!Permissions.IsAdmin)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                Logger.LogWarning("Log viewer access denied because the current user is not an administrator.");
                return;
            }

            var options = await Api.OptionsAsync();
            if (options.Success && options.Data is { } loaded)
            {
                aiAvailable = loaded.AiAvailable;
                aiUnavailableReason = loaded.AiUnavailableReason;
            }
            else
            {
                aiAvailable = false;
                aiUnavailableReason = options.UserMessage();
            }

            // 打開頁面的自動查詢不是使用者按的，不寫查詢稽核（開頁本身已有 Page.View）。
            // 起訖不帶：伺服器以它的「現在」查最近 1 小時，並把實際起訖寫回選擇器。
            await OnQueryAsync(audit: false);
        }

        /// <summary>快速區間：只改時間並立即查詢，等級、筆數、關鍵字、追蹤碼維持原值。起訖由伺服器以它的「現在」計算後寫回。</summary>
        private Task OnQuickRangeAsync(string quickRange) => OnQueryAsync(quickRange: quickRange);

        /// <summary>
        /// 查前後 1 分鐘：要看這筆前後「所有」發生的事，所以清掉等級、關鍵字與追蹤碼。
        /// 筆數拉到上限：服務只保留最新 N 筆，筆數不夠時較早的紀錄（可能就是這筆本身）會被切掉。
        /// </summary>
        private async Task OnAroundEntryAsync(LogEntryDto entry)
        {
            (startTime, endTime) = LogTimeRanges.Around(entry.Timestamp);
            minimumLevel = string.Empty;
            keyword = string.Empty;
            traceCode = string.Empty;
            takeCount = LogSearchRequestDto.MaxTake;
            await OnQueryAsync();
        }

        private async Task OnQueryAsync(bool audit = true, string? quickRange = null)
        {
            isLoading = true;

            // 重新查詢一律回到收合。⚠️ 只把 isExpandAll 設回 false 不夠：AntDesign 依 RowKey 記住每列的展開狀態，
            // 新結果的 Sequence 與舊結果重疊時，那些列會沿用舊的展開狀態。
            isExpandAll = false;
            logTable?.CollapseAll();
            statusMessage = string.Empty;
            warnings = new();
            StateHasChanged();

            try
            {
                var request = new LogSearchRequestDto
                {
                    // 起訖當成伺服器的本地時間送出（不帶時差）；快速區間只送代碼，由伺服器算。
                    StartTime = quickRange is null ? ClientCulture.Unzoned(startTime) : null,
                    EndTime = quickRange is null ? ClientCulture.Unzoned(endTime) : null,
                    QuickRange = quickRange,
                    MinimumLevel = minimumLevel,
                    Take = takeCount,
                    Keyword = keyword,
                    TraceCode = traceCode,
                    // 搜尋稽核由伺服器寫（只有使用者按的才帶）。
                    RecordSearch = audit,
                };

                var result = await Api.SearchAsync(request);
                if (!result.Success || result.Data is not { } data)
                {
                    Logger.LogError("Log query failed. Message={Message}", result.UserMessage());
                    statusMessage = result.UserMessage();
                    entriesAscending = new();
                    entriesDisplay = new();
                    lastQuery = null;
                    return;
                }

                entriesAscending = data.Entries;
                entriesDisplay = Enumerable.Reverse(entriesAscending).ToList();
                statusMessage = data.Message;
                warnings = data.Warnings;

                // 條件被夾住（或用了快速區間、預設區間）時把實際查詢的區間寫回選擇器，讓畫面與結果一致。
                startTime = data.AppliedStartTime;
                endTime = data.AppliedEndTime;

                lastQuery = new LogSearchRequestDto
                {
                    StartTime = data.AppliedStartTime,
                    EndTime = data.AppliedEndTime,
                    MinimumLevel = request.MinimumLevel,
                    Take = request.Take,
                    Keyword = request.Keyword,
                    TraceCode = request.TraceCode,
                };
            }
            finally
            {
                isLoading = false;
                StateHasChanged();
            }
        }

        private async Task OnExportAsync()
        {
            if (entriesAscending.Count == 0 || lastQuery is null)
            {
                ViewNotification.Warning(NotificationService, "目前沒有可匯出的日誌。");
                return;
            }

            // 伺服器依同一份條件重查、產生 .log（UTF-8 含 BOM）並寫匯出稽核。
            var outcome = await Api.ExportAsync(lastQuery);
            if (!outcome.Success)
            {
                // 讀檔失敗（500）時伺服器的訊息已是完整的一句（含錯誤追蹤碼）。
                ViewNotification.Error(NotificationService, outcome.StatusCode == 500 ? outcome.Message : $"匯出失敗：{outcome.Message}");
                return;
            }

            try
            {
                await JSRuntime.DownloadFileAsync(outcome.FileName!, outcome.Content!, outcome.ContentType!);
                Logger.LogInformation("Log export downloaded. Rows={Rows}, Bytes={Bytes}", entriesAscending.Count, outcome.Content!.Length);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Log export failed.");
                ViewNotification.UnexpectedError(NotificationService, $"匯出失敗：{ex.GetType().Name}。", ex);
            }
        }

        /// <summary>
        /// 「複製查詢結果」的內容，必須與伺服器匯出的 .log 完全一致，使用者才能把貼上的文字當成那份檔案。
        /// 服務回傳的即為時間正序，直接沿用，不依賴可能解析失敗的 Timestamp 重新排序。
        /// ⚠️ 換行用 CRLF：伺服器（Windows／IIS）匯出時用 <c>Environment.NewLine</c>，瀏覽器裡的 <c>Environment.NewLine</c> 是 LF。
        /// </summary>
        private string BuildRawText()
            => string.Join("\r\n", entriesAscending.Select(entry => entry.Raw));

        private void OnExpandAll()
        {
            isExpandAll = true;
            logTable?.ExpandAll();
        }

        private void OnCollapseAll()
        {
            isExpandAll = false;
            logTable?.CollapseAll();
        }

        /// <summary>
        /// 點一下任一列即複製該筆原始內容。只複製、不切換展開 —— 展開仍靠左邊的展開圖示。
        /// 展開圖示與展開後的原文區塊不會進到這裡（前者 AntDesign 自己擋掉冒泡，後者是另一個 tr）。
        /// </summary>
        private Task OnRowClickAsync(RowData<LogEntryDto> row)
            => CopyToClipboardAsync(row.Data.Raw, 1);

        private async Task OnCopyAllAsync()
        {
            if (entriesAscending.Count == 0)
            {
                ViewNotification.Warning(NotificationService, "目前沒有可複製的日誌。");
                return;
            }

            if (entriesAscending.Count > CopyAllConfirmThreshold)
            {
                var confirmed = await ConfirmDialog.AskAsync(
                    ModalService,
                    "確認複製",
                    $"本次查詢共 {entriesAscending.Count:N0} 筆，內容較大，貼上時可能較慢；大量資料建議改用匯出。",
                    "仍要複製");
                if (confirmed == false)
                {
                    return;
                }
            }

            await CopyToClipboardAsync(BuildRawText(), entriesAscending.Count);
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
                    Logger.LogInformation("Log entries copied to clipboard. Rows={Rows}, Characters={Characters}", rows, text.Length);
                    ViewNotification.Info(NotificationService, $"已複製 {rows:N0} 筆日誌到剪貼簿。");
                }
                else
                {
                    ViewNotification.Warning(NotificationService, "瀏覽器拒絕存取剪貼簿，請手動選取複製。");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to copy log entries to clipboard. Rows={Rows}", rows);
                ViewNotification.UnexpectedError(NotificationService, "複製失敗，請手動選取複製。", ex);
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
                Logger.LogError(ex, "Unhandled exception while running AI log analysis.");

                // 對話窗此時還停在「分析中」，不收拾的話會永遠轉下去。
                ShowAiFailure("AI 分析發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex.ToString());
            }
            finally
            {
                // 取消 token 會順便讓等待計時收工。
                aiCts?.Cancel();
                aiCts?.Dispose();
                aiCts = null;

                isAiRunning = false;
                if (!isDisposed)
                {
                    StateHasChanged();
                }
            }
        }

        /// <summary>
        /// 分析的是畫面上「現有的」查詢結果（伺服器依 <see cref="lastQuery"/> 重讀同一份），不是工具列上改過但還沒查詢的條件：
        /// 行為完全可預測，而對話窗會標明分析的時間區間、等級、關鍵字與筆數，不會讓人搞混。
        /// </summary>
        private async Task OnAiAnalyzeCoreAsync()
        {
            if (entriesAscending.Count == 0 || lastQuery is null)
            {
                ViewNotification.Warning(NotificationService, "目前沒有可分析的日誌。");
                return;
            }

            ResetAiModalState();
            var query = lastQuery;

            // ⚠️ 這幾行就是 0.9.8 的重點：對話窗在**送出之前**就開。
            // 舊版要等 AI 回來才第一次出現，而 TimeoutSeconds 是 600 秒 ——
            // 使用者盯著一個毫無變化的畫面好幾分鐘，會以為頁面沒在做事而切走。
            aiModalState = AiModalState.Running;
            isAiRunning = true;
            aiModalVisible = true;
            StateHasChanged();

            aiCts = new CancellationTokenSource();
            var token = aiCts.Token;
            _ = RunAiElapsedTickerAsync(token);

            // 呼叫前先告知階段，AI 回應可能要數十秒，不能讓畫面看起來沒反應。
            ViewNotification.Info(NotificationService, $"正在將 {aiSubmittedCount} 筆日誌送給 AI 分析，可能需要數十秒…");

            // 0.9.141 起串流：收到第一段文字就切到報告區，之後逐段長出來（節流重轉 HTML）。
            AiResultDto? result = null;
            AiStreamScopeDto? scope = null;
            var streamed = new StringBuilder();
            var renderClock = System.Diagnostics.Stopwatch.StartNew();
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var outcome = await Api.AnalyzeAsync(query, frame =>
            {
                if (isDisposed)
                {
                    return Task.CompletedTask;
                }

                switch (frame.Kind)
                {
                    case AiStreamFrameKinds.Start:
                        scope = frame.Scope;
                        break;
                    case AiStreamFrameKinds.Delta:
                        streamed.Append(frame.Text);
                        if (aiModalState == AiModalState.Running || renderClock.Elapsed >= AiStreamRenderInterval)
                        {
                            ShowStreamingMarkdown(streamed.ToString());
                            renderClock.Restart();
                            StateHasChanged();
                        }

                        break;
                    case AiStreamFrameKinds.Result:
                        result = frame.Result;
                        break;
                }

                return Task.CompletedTask;
            }, token);

            if (isDisposed)
            {
                return;
            }

            if (result is null)
            {
                if (token.IsCancellationRequested)
                {
                    // 關窗放棄：窗已經關了、toast 也發過了，這裡不要再改任何畫面狀態。
                    // 停止產生（0.9.141 起）：請求已中斷，伺服器的最後結果收不到了（它照樣寫稽核），
                    // 用已收到的文字在畫面上收尾：有文字就保留並標示已中止，沒有就在窗內說明已停止。
                    if (aiStopRequested)
                    {
                        ShowStoppedResult(StoppedResult(streamed.ToString(), scope, elapsed.Elapsed));
                    }

                    return;
                }

                // 伺服器拒絕（403、400、500）、連不上，或串流在最後結果之前就斷了。
                ShowAiFailure(outcome.Success ? "AI 分析發生未預期的錯誤，請稍後再試或聯絡系統管理員。" : outcome.UserMessage());
                return;
            }

            if (result.Reason == CanceledReason)
            {
                // 伺服器在送出最後結果之前就收到取消（請求剛好中斷）：與 Server 版相同處理。
                if (aiStopRequested)
                {
                    ShowStoppedResult(result);
                }

                return;
            }

            if (result.Success == false)
            {
                Logger.LogWarning(
                    "AI log analysis was not successful. Reason={Reason}, Entries={Entries}",
                    result.Reason,
                    result.IncludedEntryCount);
                ShowAiFailure(result.ErrorMessage, result.ErrorDetail);
                return;
            }

            var html = AiMarkdownRenderer.ToSafeHtml(result.Markdown);
            if (html.Length > MaxRenderedHtmlLength)
            {
                Logger.LogWarning(
                    "AI log analysis response was too large to render. Characters={Characters}", html.Length);
                ShowAiFailure("AI 回傳內容異常龐大，已中止顯示。請縮小查詢範圍後再試。");
                return;
            }

            aiResult = result;
            aiMarkdown = result.Markdown;
            aiHtml = html;
            aiMetaItems = BuildAiMetaItems(result);
            aiModalState = AiModalState.Succeeded;

            if (result.DroppedByEntryLimit)
            {
                ViewNotification.Caution(NotificationService,
                    $"查詢共 {result.TotalEntryCount} 筆，因筆數上限僅分析最新 {result.IncludedEntryCount} 筆。");
            }

            // 一份看起來完整、實際被切掉結論的分析報告，比明講「可能不完整」更危險。
            if (result.IsTruncatedByLength)
            {
                ViewNotification.Caution(NotificationService,
                    "回應已達長度上限，結尾可能不完整。請調高 AiSettings:MaxOutputTokens 或將它設為 null。");
            }

            ViewNotification.Info(NotificationService, $"AI 分析完成（分析了 {result.IncludedEntryCount} 筆）。");
        }

        /// <summary>
        /// 「停止產生」之後畫面自己組的結果：請求中斷後收不到伺服器的最後結果，模型名稱與用量因此不顯示
        /// （伺服器那邊照樣記在稽核、Token 用量與 AI 對話紀錄裡）；送出範圍取自開始時的 start、耗時取瀏覽器計時。
        /// </summary>
        private static AiResultDto StoppedResult(string markdown, AiStreamScopeDto? scope, TimeSpan elapsed) => new()
        {
            Success = false,
            Reason = CanceledReason,
            Markdown = markdown,
            IsInterrupted = markdown.Length > 0,
            ElapsedMilliseconds = (long)elapsed.TotalMilliseconds,
            TotalEntryCount = scope?.TotalEntryCount ?? 0,
            IncludedEntryCount = scope?.IncludedEntryCount ?? 0,
            DroppedByEntryLimit = scope?.DroppedByEntryLimit ?? false,
        };

        private async Task OnAiCopyAsync()
        {
            try
            {
                // 複製原始 Markdown 而非渲染後的 HTML：使用者會貼進工單或通訊軟體，
                // Markdown 才是有用的格式。
                var copied = await JSRuntime.InvokeAsync<bool>("appClipboard.copyText", aiMarkdown);
                if (copied)
                {
                    ViewNotification.Info(NotificationService, "已複製分析結果。");
                }
                else
                {
                    ViewNotification.Warning(NotificationService, "瀏覽器拒絕存取剪貼簿，請手動選取複製。");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to copy AI analysis result to clipboard.");
                ViewNotification.UnexpectedError(NotificationService, "複製失敗，請手動選取複製。", ex);
            }
        }

        private async Task OnAiExportPdfAsync()
        {
            if (aiResult is null || lastQuery is null)
            {
                return;
            }

            isAiExporting = true;
            StateHasChanged();

            try
            {
                await OnAiExportPdfCoreAsync(aiResult, lastQuery);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "AI analysis PDF export failed.");
                ViewNotification.UnexpectedError(NotificationService, $"PDF 匯出失敗：{ex.GetType().Name}。", ex);
            }
            finally
            {
                isAiExporting = false;
                StateHasChanged();
            }
        }

        /// <summary>伺服器產生 PDF（附錄依同一份條件重建、產生時間用伺服器時間）並寫一筆稽核，這裡只負責下載。</summary>
        private async Task OnAiExportPdfCoreAsync(AiResultDto result, LogSearchRequestDto query)
        {
            ViewNotification.Info(NotificationService, "正在產生 PDF…");

            var outcome = await Api.ExportAiReportPdfAsync(new LogAiReportPdfRequestDto
            {
                Query = query,
                // 用畫面上的那份：停止產生時已加上「已中止」註記（0.9.141 起）。
                Markdown = aiMarkdown,
                Success = result.Success,
                Reason = result.Reason,
                ModelName = result.ModelName,
                Usage = result.Usage,
                ElapsedMilliseconds = result.ElapsedMilliseconds,
            });

            if (!outcome.Success)
            {
                // 產生失敗（500）時伺服器的訊息已是完整的一句（含錯誤追蹤碼）。
                ViewNotification.Error(NotificationService, outcome.StatusCode == 500 ? outcome.Message : $"PDF 匯出失敗：{outcome.Message}");
                return;
            }

            await JSRuntime.DownloadFileAsync(outcome.FileName!, outcome.Content!, outcome.ContentType!);
            Logger.LogInformation(
                "AI analysis PDF exported. Bytes={Bytes}, Entries={Entries}",
                outcome.Content!.Length,
                result.IncludedEntryCount);

            ViewNotification.Info(NotificationService, "PDF 已產生並開始下載。");
        }

        /// <summary>
        /// 產生中逐段顯示（0.9.141 起）。超過顯示上限時先停止更新畫面，最後的完整結果仍會照常檢查。
        /// </summary>
        private void ShowStreamingMarkdown(string markdown)
        {
            var html = AiMarkdownRenderer.ToSafeHtml(markdown);
            if (html.Length > MaxRenderedHtmlLength)
            {
                return;
            }

            aiMarkdown = markdown;
            aiHtml = html;
            aiModalState = AiModalState.Succeeded;
        }

        /// <summary>
        /// 「停止產生」（0.9.141 起）：取消這次呼叫（中斷請求），但窗留著、已產生的內容保留。
        /// 已收到的文字上游照樣計費，Token 用量會記一筆「已取消」（沒有金額）。
        /// </summary>
        private void OnAiStop()
        {
            if (isAiRunning == false)
            {
                return;
            }

            aiStopRequested = true;
            aiCts?.Cancel();
        }

        /// <summary>停止產生之後的畫面：有內容就保留並標示已中止，沒有內容就說明已停止。</summary>
        private void ShowStoppedResult(AiResultDto result)
        {
            if (result.IsInterrupted == false)
            {
                aiModalState = AiModalState.Stopped;
                ViewNotification.Info(NotificationService, "已停止產生，尚未收到任何內容。");
                return;
            }

            var markdown = AiInterruption.Annotate(result.Markdown);
            var html = AiMarkdownRenderer.ToSafeHtml(markdown);
            if (html.Length > MaxRenderedHtmlLength)
            {
                ShowAiFailureAfterStop("AI 回傳內容異常龐大，已中止顯示。請縮小查詢範圍後再試。");
                return;
            }

            aiResult = result;
            aiInterrupted = true;
            aiMarkdown = markdown;
            aiHtml = html;
            aiMetaItems = BuildAiMetaItems(result);
            aiModalState = AiModalState.Succeeded;
            ViewNotification.Info(NotificationService, "已停止產生，保留已產生的內容（不完整）。");
        }

        /// <summary>停止之後才發現無法顯示：<see cref="ShowAiFailure"/> 會因為 token 已取消而略過，所以另外處理。</summary>
        private void ShowAiFailureAfterStop(string message)
        {
            aiErrorMessage = message;
            aiModalState = AiModalState.Failed;
            ViewNotification.Error(NotificationService, message);
        }

        /// <summary>
        /// 使用者關閉對話窗。
        ///
        /// ⚠️ 等待中關窗<b>等於放棄這次分析</b>：真的中斷請求（伺服器跟著取消 AI 呼叫），不留在背景。
        /// 這是刻意的 —— 讓一條沒人要的請求繼續佔著連線最多 10 分鐘沒有意義，
        /// 而「關掉之後結果突然跳出來」對使用者更難理解。等待畫面上有明講這件事。
        /// </summary>
        private void OnAiModalCancel()
        {
            if (isAiRunning)
            {
                aiCts?.Cancel();
                ViewNotification.Info(NotificationService, "已取消本次 AI 分析。");
            }

            aiModalVisible = false;
        }

        /// <summary>開新的一輪之前，把上一輪的殘留清乾淨。</summary>
        private void ResetAiModalState()
        {
            aiStopRequested = false;
            aiInterrupted = false;
            aiResult = null;
            aiMarkdown = string.Empty;
            aiHtml = string.Empty;
            aiMetaItems = new();
            aiErrorMessage = string.Empty;
            aiFontScalePercent = DefaultFontScalePercent;
            aiElapsedSeconds = 0;
            aiSubmittedCount = entriesAscending.Count;
        }

        /// <summary>
        /// 失敗時把原因留在窗內，同時照舊發一則 toast。
        ///
        /// 0.9.8 起窗不再自動關閉：toast 幾秒就消失，而失敗訊息常常是「要改哪個設定」
        /// 這種需要照著做的內容，看不完就沒了等於沒說。
        /// </summary>
        private void ShowAiFailure(string message, string? detail = null)
        {
            // ⚠️ 使用者已經放棄（關窗）的話，什麼都不要做（否則使用者關掉的視窗會在一兩秒後自己跳回來 —— 看起來像鬧鬼）。
            // 「停止產生」也會取消但窗還在：剛好收到失敗結果時照常顯示，否則窗會一直停在「進行中」（0.10.5 獨立審查）。
            if (aiCts?.IsCancellationRequested == true && !aiStopRequested)
            {
                return;
            }

            aiErrorMessage = message;
            aiModalState = AiModalState.Failed;
            aiModalVisible = true;
            ViewNotification.Error(NotificationService, message, detail);
        }

        /// <summary>
        /// 等待中每秒更新一次「已等待 N 秒」。
        ///
        /// 轉圈圈只證明瀏覽器還活著，跳動的秒數才證明**這次呼叫**還在進行中 ——
        /// 在最長可以等 10 分鐘的情境下，這是使用者願意繼續等下去的唯一依據。
        /// </summary>
        private async Task RunAiElapsedTickerAsync(CancellationToken token)
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                while (await timer.WaitForNextTickAsync(token))
                {
                    aiElapsedSeconds++;
                    await InvokeAsync(StateHasChanged);
                }
            }
            catch (OperationCanceledException)
            {
                // 分析結束或使用者放棄，正常收工。
            }
            catch (ObjectDisposedException)
            {
                // 元件已經釋放，沒有畫面可以更新了。
            }
        }

        /// <summary>
        /// 使用者在等待中直接離開頁面時，取消還在飛的請求（伺服器跟著取消 AI 呼叫）。
        /// 沒有這一段，一條沒人要的呼叫會繼續佔著連線直到 TimeoutSeconds（預設 600 秒）。
        /// </summary>
        public void Dispose()
        {
            isDisposed = true;
            aiCts?.Cancel();
        }

        /// <summary>
        /// 對話窗頁首的中介資訊。沿用 MainLayout「關於」視窗的 aboutItems 模式：
        /// 「有值才顯示」等於「不加那一列」，不必在 Razor 裡寫一堆條件判斷。
        /// 查詢條件取自 <see cref="lastQuery"/>（分析的那份結果的條件）。
        /// </summary>
        private List<KeyValuePair<string, string>> BuildAiMetaItems(AiResultDto result)
        {
            var query = lastQuery ?? new LogSearchRequestDto();
            var items = new List<KeyValuePair<string, string>>
            {
                new("查詢區間",
                    $"{query.StartTime:yyyy-MM-dd HH:mm:ss} ～ {query.EndTime:yyyy-MM-dd HH:mm:ss}"),
                new("最低等級", string.IsNullOrWhiteSpace(query.MinimumLevel) ? "不限" : query.MinimumLevel),
            };

            if (string.IsNullOrWhiteSpace(query.Keyword) == false)
            {
                items.Add(new KeyValuePair<string, string>("關鍵字", query.Keyword));
            }

            if (string.IsNullOrWhiteSpace(query.TraceCode) == false)
            {
                items.Add(new KeyValuePair<string, string>("錯誤追蹤碼", query.TraceCode.Trim()));
            }

            if (result.IsInterrupted)
            {
                items.Add(new KeyValuePair<string, string>("狀態", "已中止（產生途中停止，內容可能不完整）"));
            }

            var scope = $"分析 {result.IncludedEntryCount} 筆／查詢 {result.TotalEntryCount} 筆";
            if (result.DroppedByEntryLimit)
            {
                scope += "（已依筆數上限取最新資料）";
            }

            items.Add(new KeyValuePair<string, string>("分析範圍", scope));

            if (string.IsNullOrWhiteSpace(result.ModelName) == false)
            {
                items.Add(new KeyValuePair<string, string>("使用模型", result.ModelName));
            }

            var usage = AiTokenUsage.ToDisplayText(AiTokenUsage.FromDto(result.Usage));
            if (string.IsNullOrEmpty(usage) == false)
            {
                items.Add(new KeyValuePair<string, string>("用量", usage));
            }

            items.Add(new KeyValuePair<string, string>("耗時", $"{result.ElapsedMilliseconds / 1000.0:F1} 秒"));
            return items;
        }

        /// <summary>等級 Tag 的顏色依據（與伺服器解析日誌檔的規則相同：無法辨識的等級視為 Unknown）。</summary>
        private static LogLevelRank RankOf(LogEntryDto entry)
            => LogLevelRankHelper.FromLevelText(entry.Level, LogLevelRank.Unknown);
    }
}
