using System.Diagnostics;
using System.Globalization;
using System.Text;
using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Ai;
using MyProject.Models.AdapterModel;
using MyProject.Web.Ai;
using MyProject.Web.Client.Api;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Client.Views.Admins
{
    /// <summary>
    /// AI 例外分析對話窗：第一次送出取得分析報告，之後可在同一個視窗內多輪追問（0.10.5 起在 WASM 執行，WASM 遷移 M6）。
    ///
    /// 狀態機、等待、停止、失敗與渲染安全的處理照 Server 版。差別在呼叫方式：每一輪把例外明細之後的對話送到
    /// <c>POST api/v1/exception-logs/{id}/ai</c>（NDJSON 串流），第一則例外明細與 system 提示由伺服器重組，
    /// 稽核與 AI 對話紀錄也由伺服器寫。伺服器不保存對話，所以對話留在這個窗裡，關窗即丟棄。
    /// </summary>
    public partial class ExceptionAiAnalysisModal : IDisposable
    {
        private enum ModalState
        {
            /// <summary>第一次分析進行中，顯示整窗等待畫面。</summary>
            Running,

            /// <summary>第一次分析失敗，原因留在窗內。</summary>
            Failed,

            /// <summary>已有報告，可追問（追問進行中也是這個狀態，由 isRunning 區分）。</summary>
            Chatting,

            /// <summary>第一次分析還沒收到任何文字就被使用者按「停止產生」（0.9.141 起）。</summary>
            Stopped,
        }

        private enum EntryKind
        {
            User,
            Assistant,

            /// <summary>追問失敗的訊息。只顯示在畫面上，不進入送給 AI 的對話。</summary>
            Error,
        }

        /// <param name="Round">0 代表第一次分析報告；1 起為第 N 輪追問與其回覆。</param>
        private sealed record ChatEntry(EntryKind Kind, int Round, string Text, string Html);

        private static readonly (int Percent, string Label)[] FontScaleOptions =
        [
            (100, "一般"),
            (125, "中"),
            (150, "大"),
        ];

        /// <summary>伺服器 <c>AiAnalysisFailureReason.Canceled</c> 的名稱（<see cref="AiResultDto.Reason"/>）。</summary>
        private const string CanceledReason = "Canceled";

        /// <summary>對話角色（<see cref="AiChatMessageDto.Role"/>）。</summary>
        private const string UserRole = "user";
        private const string AssistantRole = "assistant";

        /// <summary>
        /// 單則 AI 回覆轉成 HTML 之後的顯示上限（同日誌檢視頁）。
        /// MaxOutputTokens 預設不送，所以這裡是防止異常龐大回應拖垮畫面的主要防線。
        /// </summary>
        private const int MaxRenderedHtmlLength = 512 * 1024;

        /// <summary>單則追問的字數上限（伺服器也擋）。追問是給人打的問題，不是貼整份檔案的地方。</summary>
        private const int MaxFollowUpLength = ExceptionAiRequestDto.MaxQuestionLength;

        private const int DefaultFontScalePercent = 100;

        /// <summary>串流中重新轉 HTML 的節流，同日誌檢視頁。</summary>
        private static readonly TimeSpan StreamRenderInterval = TimeSpan.FromMilliseconds(150);

        [Inject]
        public ExceptionLogApi Api { get; set; } = default!;

        [Inject]
        public IJSRuntime JSRuntime { get; set; } = default!;

        [Inject]
        public ILogger<ExceptionAiAnalysisModal> logger { get; set; } = default!;

        // ⚠️ 本窗的提示一律走 ViewNotification（右下角卡片），理由同日誌檢視頁：訊息偏長。
        [Inject]
        public NotificationService notificationService { get; set; } = default!;

        private bool visible;
        private ModalState modalState = ModalState.Chatting;
        private bool isRunning;
        private bool isExporting;
        private int elapsedSeconds;
        private int fontScalePercent = DefaultFontScalePercent;
        private string errorMessage = string.Empty;
        private string followUpText = string.Empty;

        private ExceptionLogAdapterModel? item;

        /// <summary>
        /// 這段對話在 AI 對話紀錄裡的識別碼（0.9.72 起）：開窗時產生，第一次分析與每輪追問共用，
        /// 對話紀錄頁才能把它們串成同一段。伺服器無狀態，所以由持有對話的這個窗來產生。
        /// </summary>
        private Guid conversationId;

        /// <summary>追問輪數上限（開窗時由頁面帶入伺服器的設定；伺服器也擋）。</summary>
        private int MaxFollowUpRounds;

        /// <summary>
        /// 例外明細<b>之後</b>的對話（第一則例外明細由伺服器重組，不在這裡）：AI 回覆與追問交替。
        /// 產生途中被停止的回覆保留模型原文並標 <see cref="AiChatMessageDto.Interrupted"/>：送回給 AI 的脈絡用原文，PDF 才加註「已中止」。
        /// </summary>
        private readonly List<AiChatMessageDto> conversation = [];

        /// <summary>畫面上的對話泡泡（含追問失敗訊息）。</summary>
        private readonly List<ChatEntry> entries = [];

        private readonly List<AiTokenUsage?> usages = [];
        private string modelName = string.Empty;
        private TimeSpan totalElapsed;

        private ElementReference latestAnchor;
        private bool scrollToLatestPending;

        /// <summary>本次呼叫的取消來源。使用者在等待中關窗即取消（中斷請求，伺服器同步取消 AI 呼叫），等待計時也綁在同一個 token 上。</summary>
        private CancellationTokenSource? cts;

        /// <summary>
        /// 這次取消是「停止產生」（保留已產生的內容）而不是關窗（放棄並丟棄整段對話）（0.9.141 起）。
        /// 兩者都是取消同一個 token，只能靠這個旗標區分。
        /// </summary>
        private bool stopRequested;

        /// <summary>正在進行的是第幾輪（0 為第一次分析），產生中的泡泡標題用。</summary>
        private int runningRound;

        /// <summary>產生中逐段長出來的回覆（已轉成安全 HTML）；還沒收到文字時為空字串。</summary>
        private string streamingHtml = string.Empty;

        /// <summary>元件已釋放（使用者導航離開）。之後不再更新畫面。</summary>
        private bool isDisposed;

        private string FontScaleCss
            => (fontScalePercent / 100.0).ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>已成功送出的追問輪數（失敗的追問已從對話移除，不算）。</summary>
        private int FollowUpCount => conversation.Count(message => message.Role == UserRole);

        private bool IsFollowUpLimitReached => FollowUpCount >= MaxFollowUpRounds;

        private List<KeyValuePair<string, string>> MetaItems
        {
            get
            {
                var items = new List<KeyValuePair<string, string>>();
                if (item is not null)
                {
                    items.Add(new KeyValuePair<string, string>("例外類型", item.ExceptionType));
                }

                if (string.IsNullOrWhiteSpace(modelName) == false)
                {
                    items.Add(new KeyValuePair<string, string>("使用模型", modelName));
                }

                var usage = AiTokenUsage.ToDisplayText(AiTokenUsage.Sum(usages));
                if (string.IsNullOrEmpty(usage) == false)
                {
                    items.Add(new KeyValuePair<string, string>("累計用量", usage));
                }

                items.Add(new KeyValuePair<string, string>("累計耗時", $"{totalElapsed.TotalSeconds:F1} 秒"));
                return items;
            }
        }

        /// <summary>
        /// 開窗並立即送出第一次分析。
        ///
        /// ⚠️ 對話窗在<b>送出之前</b>就開（同日誌檢視頁 0.9.8 的教訓）：
        /// 推論模型可能要等好幾分鐘，使用者盯著沒變化的畫面會以為頁面沒在做事。
        /// </summary>
        /// <param name="maxFollowUpRounds">追問輪數上限（伺服器設定）。</param>
        public async Task OpenAsync(ExceptionLogAdapterModel exceptionItem, int maxFollowUpRounds)
        {
            if (isRunning)
            {
                return;
            }

            Reset();
            item = exceptionItem;
            conversationId = Guid.NewGuid();
            MaxFollowUpRounds = Math.Max(0, maxFollowUpRounds);

            modalState = ModalState.Running;
            visible = true;

            ViewNotification.Info(notificationService, "正在將這筆例外送給 AI 分析，可能需要數十秒…");
            await RunAsync(round: 0);
        }

        private async Task OnSendFollowUpAsync()
        {
            var question = followUpText.Trim();
            if (isRunning || question.Length == 0 || IsFollowUpLimitReached)
            {
                return;
            }

            var round = FollowUpCount + 1;
            conversation.Add(new AiChatMessageDto { Role = UserRole, Content = question });
            entries.Add(new ChatEntry(EntryKind.User, round, question, string.Empty));
            followUpText = string.Empty;

            await RunAsync(round);
        }

        /// <summary>送出目前的對話並處理結果。第一次分析與追問共用。</summary>
        private async Task RunAsync(int round)
        {
            isRunning = true;
            stopRequested = false;
            streamingHtml = string.Empty;
            runningRound = round;
            elapsedSeconds = 0;
            scrollToLatestPending = round > 0;
            StateHasChanged();

            var clock = Stopwatch.StartNew();
            try
            {
                cts = new CancellationTokenSource();
                var token = cts.Token;
                _ = RunElapsedTickerAsync(token);

                // 串流（0.9.141 起）：第一段文字一到就顯示在對話裡，之後逐段長出來（節流重轉 HTML）。
                AiResultDto? result = null;
                var streamed = new StringBuilder();
                var renderClock = Stopwatch.StartNew();
                var request = new ExceptionAiRequestDto
                {
                    ConversationId = conversationId,
                    Messages = conversation.Select(message => new AiChatMessageDto { Role = message.Role, Content = message.Content }).ToList(),
                };

                var response = await Api.AnalyzeAsync(item!.Id, request, frame =>
                {
                    if (isDisposed)
                    {
                        return Task.CompletedTask;
                    }

                    if (frame.Kind == AiStreamFrameKinds.Result)
                    {
                        result = frame.Result;
                    }
                    else if (frame.Kind == AiStreamFrameKinds.Delta && !string.IsNullOrEmpty(frame.Text))
                    {
                        streamed.Append(frame.Text);
                        if (streamingHtml.Length == 0 || renderClock.Elapsed >= StreamRenderInterval)
                        {
                            ShowStreaming(streamed.ToString());
                            renderClock.Restart();
                            StateHasChanged();
                        }
                    }

                    return Task.CompletedTask;
                }, token);

                streamingHtml = string.Empty;

                // 使用者導航離開：什麼都不要再碰。
                if (isDisposed)
                {
                    return;
                }

                if (result is null)
                {
                    if (stopRequested)
                    {
                        // 「停止產生」會中斷請求，收不到伺服器最後那一筆結果（伺服器照樣記稽核與用量）：
                        // 依已收到的文字自己組出「已取消」的結果，與 Server 版服務回的相同。
                        result = new AiResultDto
                        {
                            Reason = CanceledReason,
                            Markdown = streamed.ToString(),
                            IsInterrupted = streamed.Length > 0,
                            ElapsedMilliseconds = clock.ElapsedMilliseconds,
                        };
                    }
                    else if (token.IsCancellationRequested)
                    {
                        // 使用者關窗放棄。窗已經關了、對話也清掉了，這裡不要再改任何畫面狀態。
                        return;
                    }
                    else
                    {
                        // 伺服器拒絕（400／403／404…）、連不上，或串流中途斷掉。
                        ShowFailure(round, response.Success ? "AI 分析發生未預期的錯誤，請稍後再試或聯絡系統管理員。" : response.UserMessage());
                        return;
                    }
                }

                if (stopRequested)
                {
                    // 停止產生（0.9.141 起）：窗留著。剛好在按下的同時完成的，就當作正常完成往下走。
                    if (result.Reason == CanceledReason)
                    {
                        ShowStopped(round, result);
                        return;
                    }
                }
                else if (result.Reason == CanceledReason || token.IsCancellationRequested)
                {
                    // 使用者關窗放棄。即使回應恰好在取消的同時成功回來，也不能把它塞進已經清空的對話。
                    return;
                }

                if (result.Success == false)
                {
                    logger.LogWarning(
                        "AI exception analysis was not successful. Reason={Reason}, Round={Round}, ExceptionLogId={ExceptionLogId}",
                        result.Reason,
                        round,
                        item?.Id);
                    ShowFailure(round, result.ErrorMessage, result.ErrorDetail);
                    return;
                }

                var html = AiMarkdownRenderer.ToSafeHtml(result.Markdown);
                if (html.Length > MaxRenderedHtmlLength)
                {
                    logger.LogWarning(
                        "AI exception analysis response was too large to render. Characters={Characters}", html.Length);
                    ShowFailure(round, "AI 回傳內容異常龐大，已中止顯示。");
                    return;
                }

                conversation.Add(new AiChatMessageDto { Role = AssistantRole, Content = result.Markdown });
                entries.Add(new ChatEntry(EntryKind.Assistant, round, result.Markdown, html));
                usages.Add(AiTokenUsage.FromDto(result.Usage));
                modelName = result.ModelName;
                totalElapsed += TimeSpan.FromMilliseconds(result.ElapsedMilliseconds);
                modalState = ModalState.Chatting;
                scrollToLatestPending = round > 0;

                // 一份看起來完整、實際被切掉結論的報告，比明講「可能不完整」更危險。
                if (result.IsTruncatedByLength)
                {
                    ViewNotification.Caution(notificationService,
                        "回應已達長度上限，結尾可能不完整。請調高 AiSettings:MaxOutputTokens 或將它設為 null。");
                }

                // 追問不發完成通知：回覆已直接出現在對話裡，而右下角的通知卡片正好蓋住「送出」鍵，
                // 使用者想接著再問時會點不到。
                if (round == 0)
                {
                    ViewNotification.Info(notificationService, "AI 例外分析完成。");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while running AI exception analysis.");

                // 不收拾的話，對話窗會永遠停在「分析中」。
                ShowFailure(round, "AI 分析發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex.ToString());
            }
            finally
            {
                // 取消 token 會順便讓等待計時收工。
                cts?.Cancel();
                cts?.Dispose();
                cts = null;

                streamingHtml = string.Empty;
                isRunning = false;
                if (isDisposed == false)
                {
                    StateHasChanged();
                }
            }
        }

        /// <summary>產生中逐段顯示（0.9.141 起）。超過顯示上限時先停止更新，最後的完整結果仍會照常檢查。</summary>
        private void ShowStreaming(string markdown)
        {
            var html = AiMarkdownRenderer.ToSafeHtml(markdown);
            if (html.Length > MaxRenderedHtmlLength)
            {
                return;
            }

            streamingHtml = html;
            modalState = ModalState.Chatting;
        }

        /// <summary>
        /// 「停止產生」（0.9.141 起）：取消這次呼叫（中斷請求），但窗與對話都留著。
        /// 已收到的文字上游照樣計費，Token 用量會記一筆「已取消」（沒有金額）。
        /// </summary>
        private void OnStop()
        {
            if (isRunning == false)
            {
                return;
            }

            stopRequested = true;
            cts?.Cancel();
        }

        /// <summary>
        /// 停止產生之後：已收到文字 → 保留為一則回覆並標示已中止（也放進之後追問的脈絡）；
        /// 還沒收到 → 第一次分析顯示「已停止」，追問則把問題放回輸入框。
        /// </summary>
        private void ShowStopped(int round, AiResultDto result)
        {
            if (result.IsInterrupted)
            {
                var annotated = AiInterruption.Annotate(result.Markdown);
                var html = AiMarkdownRenderer.ToSafeHtml(annotated);
                if (html.Length <= MaxRenderedHtmlLength)
                {
                    // 送回給 AI 的脈絡保留模型原本的輸出；畫面、複製與 PDF 才加註記。
                    conversation.Add(new AiChatMessageDto { Role = AssistantRole, Content = result.Markdown, Interrupted = true });
                    entries.Add(new ChatEntry(EntryKind.Assistant, round, annotated, html));
                    totalElapsed += TimeSpan.FromMilliseconds(result.ElapsedMilliseconds);
                    modalState = ModalState.Chatting;
                    scrollToLatestPending = round > 0;
                    ViewNotification.Info(notificationService, "已停止產生，保留已產生的內容（不完整）。");
                    return;
                }
            }

            if (round == 0)
            {
                modalState = ModalState.Stopped;
            }
            else
            {
                PutQuestionBack();
                entries.Add(new ChatEntry(EntryKind.Error, round, "已停止產生，尚未收到任何內容。問題已放回輸入框。", string.Empty));
                scrollToLatestPending = true;
            }

            ViewNotification.Info(notificationService, "已停止產生，尚未收到任何內容。");
        }

        /// <summary>
        /// 失敗時把原因留在窗內，同時照舊發一則通知。
        ///
        /// 追問失敗不毀掉整段對話：那一則提問從對話移除並放回輸入框，使用者可以直接重送，
        /// 失敗原因則以一則錯誤泡泡留在畫面上（不送給 AI）。
        /// </summary>
        private void ShowFailure(int round, string message, string? detail = null)
        {
            // ⚠️ 使用者已經放棄（關窗）的話，什麼都不要做 —— 少了這道閘門，關掉的視窗會自己跳回來。
            // 「停止產生」也會取消，但窗還在：這時剛好收到失敗結果要照常收尾（問題放回輸入框），否則對話卡住、之後追問一律被拒（0.10.5 獨立審查）。
            if (cts?.IsCancellationRequested == true && !stopRequested)
            {
                return;
            }

            if (round == 0)
            {
                errorMessage = message;
                modalState = ModalState.Failed;
            }
            else
            {
                PutQuestionBack();
                entries.Add(new ChatEntry(EntryKind.Error, round, message, string.Empty));
                scrollToLatestPending = true;
            }

            ViewNotification.Error(notificationService, message, detail);
        }

        /// <summary>沒有得到回覆的追問：從對話與畫面移除，問題放回輸入框。</summary>
        private void PutQuestionBack()
        {
            if (conversation.Count > 0 && conversation[^1].Role == UserRole)
            {
                var last = conversation[^1];
                conversation.RemoveAt(conversation.Count - 1);
                entries.RemoveAt(entries.Count - 1);
                followUpText = last.Content;
            }
        }

        private async Task OnCopyAsync()
        {
            try
            {
                // 複製 Markdown 而非 HTML：使用者會貼進工單或通訊軟體，Markdown 才是有用的格式。
                var copied = await JSRuntime.InvokeAsync<bool>("appClipboard.copyText", BuildConversationMarkdown());
                if (copied)
                {
                    ViewNotification.Info(notificationService, "已複製整段對話。");
                }
                else
                {
                    ViewNotification.Warning(notificationService, "瀏覽器拒絕存取剪貼簿，請手動選取複製。");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to copy AI exception analysis to clipboard.");
                ViewNotification.UnexpectedError(notificationService, "複製失敗，請手動選取複製。", ex);
            }
        }

        private string BuildConversationMarkdown()
        {
            var builder = new StringBuilder();
            builder.Append("# 例外 AI 分析：").Append(item?.ExceptionType).Append("\n\n");

            foreach (var entry in entries.Where(entry => entry.Kind != EntryKind.Error))
            {
                if (entry.Kind == EntryKind.User)
                {
                    builder.Append("---\n\n## 追問 ").Append(entry.Round).Append("\n\n> ")
                        .Append(entry.Text.Replace("\n", "\n> ")).Append("\n\n");
                }
                else
                {
                    builder.Append(entry.Text.TrimEnd()).Append("\n\n");
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// 匯出 PDF：送回對話（含哪些回覆被中止）、模型與累計用量，伺服器補上例外明細與堆疊、產生檔案並寫稽核
        /// （產生時間、操作者與系統名稱都由伺服器填）。
        /// </summary>
        private async Task OnExportPdfAsync()
        {
            if (item is null || entries.Count == 0)
            {
                return;
            }

            isExporting = true;
            StateHasChanged();

            try
            {
                ViewNotification.Info(notificationService, "正在產生 PDF…");

                var outcome = await Api.ExportAiPdfAsync(item.Id, new ExceptionAiPdfRequestDto
                {
                    Messages = conversation
                        .Select(message => new AiChatMessageDto { Role = message.Role, Content = message.Content, Interrupted = message.Interrupted })
                        .ToList(),
                    ModelName = modelName,
                    Usage = AiTokenUsage.ToDto(AiTokenUsage.Sum(usages)),
                });
                if (!outcome.Success)
                {
                    ViewNotification.Error(notificationService, $"PDF 匯出失敗：{outcome.Message}");
                    return;
                }

                await JSRuntime.DownloadFileAsync(outcome.FileName!, outcome.Content!, outcome.ContentType!);
                logger.LogInformation(
                    "AI exception analysis PDF exported. Bytes={Bytes}, FollowUps={FollowUps}", outcome.Content!.Length, FollowUpCount);
                ViewNotification.Info(notificationService, "PDF 已產生並開始下載。");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AI exception analysis PDF export failed.");
                ViewNotification.UnexpectedError(notificationService, $"PDF 匯出失敗：{ex.GetType().Name}。", ex);
            }
            finally
            {
                isExporting = false;
                StateHasChanged();
            }
        }

        /// <summary>
        /// 使用者關閉對話窗。
        ///
        /// ⚠️ 等待中關窗<b>等於放棄這次呼叫</b>：真的中斷請求，伺服器同步取消 AI 呼叫，不留在背景。
        /// 不論是否等待中，關窗都會丟棄窗內的整段對話 —— 窗內不保存報告；
        /// 每一次送出的呼叫另記於「AI 對話紀錄」（0.9.72 起，管理員專屬、依保留天數自動過期）。
        /// </summary>
        private void OnModalCancel()
        {
            if (isRunning)
            {
                cts?.Cancel();
                ViewNotification.Info(notificationService, "已取消本次 AI 分析。");
            }

            visible = false;
            Reset();
        }

        private void Reset()
        {
            item = null;
            conversationId = Guid.Empty;
            conversation.Clear();
            entries.Clear();
            usages.Clear();
            modelName = string.Empty;
            totalElapsed = TimeSpan.Zero;
            errorMessage = string.Empty;
            followUpText = string.Empty;
            fontScalePercent = DefaultFontScalePercent;
            elapsedSeconds = 0;
            scrollToLatestPending = false;
            stopRequested = false;
            streamingHtml = string.Empty;
            modalState = ModalState.Chatting;
        }

        /// <summary>
        /// 新的一則回覆（或等待泡泡）出現時，把畫面帶到它的開頭。
        /// 用 focus 一個 tabindex=-1 的錨點達成，瀏覽器會自動捲動到可見範圍，不必另寫 JS。
        /// </summary>
        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (scrollToLatestPending == false || latestAnchor.Context is null)
            {
                return;
            }

            scrollToLatestPending = false;
            try
            {
                await latestAnchor.FocusAsync();
            }
            catch (JSException)
            {
                // 錨點在這次 render 之後就消失了（例如回覆剛好到達），不影響任何功能。
            }
        }

        /// <summary>
        /// 等待中每秒更新一次「已等待 N 秒」。跳動的秒數才證明這次呼叫還在進行中。
        /// ⚠️ 一定要用 <c>InvokeAsync(StateHasChanged)</c>：計時器回呼不在 renderer 的同步內容上。
        /// </summary>
        private async Task RunElapsedTickerAsync(CancellationToken token)
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                while (await timer.WaitForNextTickAsync(token))
                {
                    elapsedSeconds++;
                    await InvokeAsync(StateHasChanged);
                }
            }
            catch (OperationCanceledException)
            {
                // 呼叫結束或使用者放棄，正常收工。
            }
            catch (ObjectDisposedException)
            {
                // 元件已經釋放，沒有畫面可以更新了。
            }
        }

        /// <summary>
        /// 使用者在等待中直接離開頁面時，取消還在飛的請求（伺服器同步取消 AI 呼叫）。
        /// 沒有這一段，一條沒人要的呼叫會繼續佔著連線直到 TimeoutSeconds（預設 600 秒）。
        /// </summary>
        public void Dispose()
        {
            isDisposed = true;
            cts?.Cancel();
        }
    }
}
