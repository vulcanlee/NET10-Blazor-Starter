using System.Diagnostics;
using System.Text;
using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MyProject.Dtos.Ai;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Ai;
using MyProject.Web.Ai.Agents;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Client.Views.AiAssistants;

/// <summary>
/// Agent 測試台（0.9.142 起；0.9.143 起對話會保存，左側「我的對話」可以接續、重新命名與刪除）。
/// 0.10.7 起在 WASM 執行（WASM 遷移 M8）：畫面、對話窗與提示訊息照 Server 版；對話不在瀏覽器裡執行，
/// 每次送出或核准都是一個請求（<see cref="AgentPlaygroundApi"/>，NDJSON 串流），伺服器接續存下的對話、執行、存檔，
/// 新增的紀錄、文字片段、工具活動、待核准與結果一行行送來。2,000 字、等待核准不能送、核准冪等等規則都在伺服器再擋一次。
/// 網址帶 <c>?conversation={id}</c>，重新整理後回到同一段對話（停在核准的照樣跳出核准窗）。
/// </summary>
public partial class AgentPlaygroundView : IDisposable
{
    /// <summary>單則訊息的字數上限（伺服器也擋）。</summary>
    private const int MaxInputLength = AgentConversationLimits.MaxMessageLength;

    /// <summary>AI 回覆轉成 HTML 之後的顯示上限（同其他 AI 畫面）。</summary>
    private const int MaxRenderedHtmlLength = 512 * 1024;

    /// <summary>伺服器 <c>AiAnalysisFailureReason.Canceled</c> 的名稱（<see cref="AiResultDto.Reason"/>）。</summary>
    private const string CanceledReason = "Canceled";

    private const string UnexpectedMessage = "Agent 執行發生未預期的錯誤，請稍後再試。";

    /// <summary>串流中重新轉 HTML 的節流（同其他 AI 畫面）。</summary>
    private static readonly TimeSpan StreamRenderInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>AI 回覆的 HTML（以紀錄物件為鍵，每則只轉一次）；null 表示內容異常龐大、不顯示。</summary>
    private readonly Dictionary<AiAgentTranscriptEntry, string?> htmlCache = [];

    private readonly List<AiAgentTranscriptEntry> entries = [];

    private bool isAccessChecked;
    private string? unavailableReason;
    private string displayName = string.Empty;
    private int retentionDays;
    private IReadOnlyList<string> reasoningEffortValues = [];
    private string systemDefaultReasoningLabel = string.Empty;
    private string sampleTitlePrefix = string.Empty;

    /// <summary>目前這段對話（還沒送出第一則前為 null，由伺服器在 start 那一行給）。</summary>
    private Guid? conversationId;

    /// <summary>這段對話存在伺服器的推論等級（改值失敗時退回它）。</summary>
    private string? savedReasoning;
    private string modelName = string.Empty;
    private IReadOnlyList<AiAgentConversationSummary> conversations = [];
    private string inputText = string.Empty;
    private bool isRunning;
    private bool isOpening;
    private bool stopRequested;
    private int elapsedSeconds;
    private string streamingHtml = string.Empty;
    private bool approvalVisible;
    private IReadOnlyList<AiToolApprovalRequest> pendingApprovals = [];
    private bool renameVisible;
    private Guid renameTarget;
    private string renameTitle = string.Empty;
    private CancellationTokenSource? cts;
    private bool isDisposed;
    private bool canCreateSamples;
    private bool canClearSamples;
    private bool samplesVisible;
    private IReadOnlyList<string> sampleTitles = [];

    /// <summary>畫面上選的推論等級（0.9.170 起）；空字串＝系統預設。</summary>
    private string reasoningChoice = string.Empty;

    /// <summary>改值失敗退回舊值時換一個 key，強制 Select 重新渲染（參數沒變時 AntDesign 不會自己同步回去）。</summary>
    private int reasoningSelectKey;

    [Inject]
    public AgentPlaygroundApi Api { get; set; } = default!;

    [Inject]
    public PagePermissions Permissions { get; set; } = default!;

    [Inject]
    public NotificationService NotificationService { get; set; } = default!;

    [Inject]
    public ModalService ModalService { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    public ILogger<AgentPlaygroundView> Logger { get; set; } = default!;

    /// <summary>網址帶過來的對話（重新整理前開著的那一段）。</summary>
    [Parameter]
    public Guid? InitialConversationId { get; set; }

    public string RoleMessage { get; set; } = string.Empty;

    private bool IsAwaitingApproval => pendingApprovals.Count > 0;

    private bool IsBusy => isRunning || isOpening;

    protected override async Task OnInitializedAsync()
    {
        // 沒有權限不呼叫任何 API；開頁被拒的稽核由伺服器在開頁紀錄時寫（shell/page-view）。
        if (Permissions.CanAccessPage(MagicObjectHelper.角色_Agent測試台) == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            isAccessChecked = true;
            Logger.LogWarning("Agent playground denied because current user has not this role permission.");
            return;
        }

        await LoadInfoAsync();
        await LoadConversationsAsync();
        isAccessChecked = true;

        if (InitialConversationId is { } initial && unavailableReason is null)
        {
            isOpening = true;
            try
            {
                await OpenAsync(initial);
            }
            finally
            {
                isOpening = false;
            }
        }
    }

    private async Task LoadInfoAsync()
    {
        var result = await Api.InfoAsync();
        if (result.Success == false || result.Data is not { } info)
        {
            Logger.LogWarning("Agent playground failed to load the page info. Message={Message}", result.UserMessage());
            unavailableReason = result.UserMessage();
            return;
        }

        displayName = info.DisplayName;
        unavailableReason = info.UnavailableReason;
        retentionDays = info.RetentionDays;
        reasoningEffortValues = info.ReasoningEffortValues;
        systemDefaultReasoningLabel = info.SystemDefaultReasoningLabel;
        canCreateSamples = info.CanCreateSamples;
        canClearSamples = info.CanClearSamples;
        sampleTitlePrefix = info.SampleTitlePrefix;
    }

    /// <summary>產生 10 筆測試專案（0.9.145 起），成功後跳出說明窗；已經有測試專案時提示先清除。</summary>
    private async Task OnGenerateSamplesAsync()
    {
        if (IsBusy || canCreateSamples == false)
        {
            return;
        }

        isOpening = true;
        try
        {
            var result = await Api.GenerateSamplesAsync();
            if (result.Success && result.Data is { } created)
            {
                sampleTitles = created.Titles;
                samplesVisible = true;
            }
            else if (result.StatusCode == 409)
            {
                ViewNotification.Caution(NotificationService, result.UserMessage());
            }
            else
            {
                ViewNotification.Error(NotificationService, result.UserMessage());
            }
        }
        finally
        {
            isOpening = false;
        }
    }

    /// <summary>永久刪除所有測試專案（含已刪除的），刪除前確認。</summary>
    private async Task OnClearSamplesAsync()
    {
        if (IsBusy || canClearSamples == false)
        {
            return;
        }

        var confirmed = await ConfirmDialog.AskDestructiveAsync(
            ModalService,
            "清除測試專案",
            $"確定要永久刪除所有標題以「{sampleTitlePrefix}」開頭的專案（含已刪除的）嗎？刪除後無法復原。",
            "永久刪除");
        if (confirmed == false)
        {
            return;
        }

        isOpening = true;
        try
        {
            var result = await Api.ClearSamplesAsync();
            if (result.Success)
            {
                ViewNotification.Warning(NotificationService, result.Message);
            }
            else
            {
                ViewNotification.Error(NotificationService, result.UserMessage());
            }
        }
        finally
        {
            isOpening = false;
        }
    }

    private async Task OnNewConversation()
    {
        if (IsBusy)
        {
            return;
        }

        ResetConversation();
        SyncUrl();

        // 同 Server 版：開新對話時重新確認 AI 設定（管理員可能剛改好）。
        var result = await Api.InfoAsync();
        if (result.Success && result.Data is { } info)
        {
            unavailableReason = info.UnavailableReason;
        }
    }

    /// <summary>點左側清單的一段對話：接續它（停在核准的重新跳出核准窗）。</summary>
    private async Task OnOpenConversationAsync(AiAgentConversationSummary summary)
    {
        if (IsBusy || conversationId == summary.ConversationId || unavailableReason is not null)
        {
            return;
        }

        isOpening = true;
        try
        {
            await OpenAsync(summary.ConversationId);
        }
        finally
        {
            isOpening = false;
        }
    }

    /// <summary>讀回一段對話（完整紀錄與待核准）；讀不到時提示，畫面維持原狀。</summary>
    private async Task OpenAsync(Guid id)
    {
        var result = await Api.GetAsync(id);
        if (result.Success == false || result.Data is not { } opened)
        {
            Logger.LogWarning("Agent playground failed to open a conversation. ConversationId={ConversationId}, Status={Status}", id, result.StatusCode);
            ViewNotification.Error(NotificationService, result.StatusCode == 404 ? result.UserMessage() : "無法開啟這段對話，請稍後再試。");
            if (conversationId is null)
            {
                SyncUrl();
            }

            return;
        }

        ResetConversation();
        conversationId = opened.ConversationId;
        modelName = opened.ModelName;
        savedReasoning = opened.ReasoningEffort;
        reasoningChoice = opened.ReasoningEffort ?? string.Empty;
        entries.AddRange(opened.Transcript.Select(AgentDtoMapper.ToModel));
        if (opened.PendingApprovals.Count > 0)
        {
            pendingApprovals = opened.PendingApprovals.Select(AgentDtoMapper.ToModel).ToList();
            approvalVisible = true;
        }

        SyncUrl();
    }

    private void OnRename(AiAgentConversationSummary summary)
    {
        if (IsBusy)
        {
            return;
        }

        renameTarget = summary.ConversationId;
        renameTitle = summary.Title;
        renameVisible = true;
    }

    private async Task OnRenameSaveAsync()
    {
        var result = await Api.RenameAsync(renameTarget, renameTitle);
        if (result.Success == false)
        {
            ViewNotification.Error(NotificationService, result.UserMessage());
            return;
        }

        renameVisible = false;
        ViewNotification.Warning(NotificationService, "已重新命名。");
        await LoadConversationsAsync();
    }

    private async Task OnDeleteAsync(AiAgentConversationSummary summary)
    {
        if (IsBusy)
        {
            return;
        }

        var confirmed = await ConfirmDialog.AskDestructiveAsync(
            ModalService,
            "刪除對話",
            $"確定要刪除「{summary.Title}」嗎？對話內容會一併刪除，無法復原（AI 對話紀錄與 Token 用量不受影響）。",
            "刪除");
        if (confirmed == false)
        {
            return;
        }

        var result = await Api.DeleteAsync(summary.ConversationId);
        if (result.Success == false)
        {
            ViewNotification.Error(NotificationService, result.UserMessage());
            return;
        }

        if (conversationId == summary.ConversationId)
        {
            ResetConversation();
            SyncUrl();
        }

        ViewNotification.Warning(NotificationService, "已刪除對話。");
        await LoadConversationsAsync();
    }

    private async Task OnSendAsync()
    {
        var text = inputText.Trim();
        if (IsBusy || IsAwaitingApproval || text.Length == 0 || unavailableReason is not null)
        {
            return;
        }

        inputText = string.Empty;
        var request = new AgentMessageRequestDto
        {
            ConversationId = conversationId,
            Message = text,
            ReasoningEffort = string.IsNullOrEmpty(reasoningChoice) ? null : reasoningChoice,
        };
        await RunAsync(text, (onFrame, token) => Api.SendAsync(request, onFrame, token));
    }

    /// <summary>核准或拒絕所有待決定的工具，然後讓 Agent 繼續（核准／拒絕的說明由伺服器記進紀錄，一起送回來）。</summary>
    private async Task OnDecideAsync(bool approve)
    {
        if (conversationId is not { } id || isRunning)
        {
            return;
        }

        approvalVisible = false;
        var ids = pendingApprovals.Select(item => item.RequestId).ToList();
        pendingApprovals = [];

        var request = approve
            ? new AgentApprovalResponseRequestDto { ApprovedRequestIds = ids }
            : new AgentApprovalResponseRequestDto { RejectedRequestIds = ids };
        await RunAsync(null, (onFrame, token) => Api.RespondAsync(id, request, onFrame, token));
    }

    private void OnStop()
    {
        if (isRunning)
        {
            stopRequested = true;
            cts?.Cancel();
        }
    }

    /// <summary>
    /// 跑一次執行：文字逐段長；工具活動、核准、結果出現時，伺服器已把完整的一則記進紀錄（entry 那一行），串流區就清掉。
    /// <paramref name="sentMessage"/>：這次送出的訊息（回覆核准時為 null），停止產生時用來自己補上紀錄。
    /// </summary>
    private async Task RunAsync(string? sentMessage, Func<Func<AgentStreamFrameDto, Task>, CancellationToken, Task<Dtos.Commons.ApiResult<object>>> start)
    {
        isRunning = true;
        stopRequested = false;
        elapsedSeconds = 0;
        streamingHtml = string.Empty;
        cts = new CancellationTokenSource();
        var token = cts.Token;
        _ = RunElapsedTickerAsync(token);
        StateHasChanged();

        var streamed = new StringBuilder();
        var renderClock = Stopwatch.StartNew();
        AiResultDto? result = null;
        var startedNow = false;
        var awaitingApproval = false;
        var userEntryReceived = false;

        try
        {
            var response = await start(frame =>
            {
                if (isDisposed)
                {
                    // 離開頁面：不再處理，伺服器會把已收到的內容收進紀錄並存檔。
                    return Task.CompletedTask;
                }

                switch (frame.Kind)
                {
                    case AgentStreamFrameKinds.Start:
                        startedNow = true;
                        var isNew = conversationId != frame.ConversationId;
                        conversationId = frame.ConversationId;
                        modelName = frame.ModelName ?? string.Empty;
                        savedReasoning = frame.ReasoningEffort;
                        if (isNew)
                        {
                            SyncUrl();
                        }

                        StateHasChanged();
                        break;
                    case AgentStreamFrameKinds.Entry when frame.Entry is { } dto:
                        var entry = AgentDtoMapper.ToModel(dto);
                        entries.Add(entry);
                        userEntryReceived |= entry.Kind == AiAgentTranscriptKinds.User;
                        if (entry.Kind == AiAgentTranscriptKinds.Assistant)
                        {
                            // 串流中的文字已成為完整的一則。
                            ClearStreaming(streamed);
                        }

                        break;
                    case AgentStreamFrameKinds.Tool when frame.Tool is { Completed: false }:
                        ClearStreaming(streamed);
                        StateHasChanged();
                        break;
                    case AgentStreamFrameKinds.Approvals when frame.Approvals is { } approvals:
                        ClearStreaming(streamed);
                        pendingApprovals = approvals.Select(AgentDtoMapper.ToModel).ToList();
                        approvalVisible = true;
                        awaitingApproval = true;
                        break;
                    case AgentStreamFrameKinds.Result:
                        ClearStreaming(streamed);
                        result = frame.Result;
                        break;
                    case AgentStreamFrameKinds.Delta when string.IsNullOrEmpty(frame.Text) == false:
                        streamed.Append(frame.Text);
                        if (streamingHtml.Length == 0 || renderClock.Elapsed >= StreamRenderInterval)
                        {
                            var html = AiMarkdownRenderer.ToSafeHtml(streamed.ToString());
                            if (html.Length <= MaxRenderedHtmlLength)
                            {
                                streamingHtml = html;
                            }

                            renderClock.Restart();
                            StateHasChanged();
                        }

                        break;
                }

                return Task.CompletedTask;
            }, token);

            if (isDisposed || awaitingApproval)
            {
                // 停在核准：等使用者決定。
                return;
            }

            if (result is null)
            {
                if (stopRequested)
                {
                    if (startedNow == false && conversationId is not null)
                    {
                        // 還在排隊（另一個分頁正在用這段對話）就按停止：伺服器什麼都沒做、沒有存檔（0.10.7 獨立審查）。
                        // 不自己補紀錄，改重讀伺服器上的對話（核准中的話核准窗也會回來）。
                        await ReloadCurrentAsync();
                        return;
                    }

                    // 「停止產生」會中斷請求，收不到伺服器最後幾行（伺服器照樣取消、存檔、記帳）：照伺服器的寫法自己補上紀錄。
                    AppendStopped(sentMessage, userEntryReceived, streamed.ToString());
                    return;
                }

                // 伺服器拒絕（400／404／409…）、連不上，或串流中途斷掉。
                ViewNotification.Error(NotificationService, response.Success ? UnexpectedMessage : response.UserMessage());
                if (startedNow == false && conversationId is not null)
                {
                    // 沒有開始執行（例如推論等級換不過去）：選單退回這段對話原本的值。
                    RevertReasoningChoice();
                }

                if (response.StatusCode == 409)
                {
                    // 另一個分頁已經改變了這段對話（例如已經停在核准）：重新讀一次，畫面才跟得上。
                    await ReloadCurrentAsync();
                }

                return;
            }

            if (result.Success)
            {
                if (result.IsTruncatedByLength)
                {
                    ViewNotification.Caution(NotificationService, "回應已達長度上限，結尾可能不完整。");
                }

                return;
            }

            if (result.Reason != CanceledReason)
            {
                ViewNotification.Error(NotificationService, result.ErrorMessage, result.ErrorDetail);
                if (result.Reason is "Conflict" or "NotFound")
                {
                    // 排隊等到之後才發現對話已經變了（另一個分頁先處理了）。
                    await ReloadCurrentAsync();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Agent playground run failed unexpectedly.");
            ViewNotification.UnexpectedError(NotificationService, UnexpectedMessage, ex);
        }
        finally
        {
            cts?.Cancel();
            cts?.Dispose();
            cts = null;
            streamingHtml = string.Empty;
            isRunning = false;
            if (isDisposed == false)
            {
                await LoadConversationsAsync();
                StateHasChanged();
            }
        }
    }

    /// <summary>停止產生後補上的紀錄，與伺服器存下的相同（下次打開這段對話看到的也是這樣）。</summary>
    private void AppendStopped(string? sentMessage, bool userEntryReceived, string streamedText)
    {
        if (sentMessage is not null && userEntryReceived == false)
        {
            entries.Add(new AiAgentTranscriptEntry { Kind = AiAgentTranscriptKinds.User, Text = sentMessage });
        }

        if (streamedText.Length > 0)
        {
            entries.Add(new AiAgentTranscriptEntry { Kind = AiAgentTranscriptKinds.Assistant, Text = AiInterruption.Annotate(streamedText) });
        }

        entries.Add(new AiAgentTranscriptEntry
        {
            Kind = AiAgentTranscriptKinds.Notice,
            Text = streamedText.Length > 0 ? "已停止產生（上面的回覆不完整）。" : "已停止產生，尚未收到任何內容。",
        });
    }

    /// <summary>重新讀目前這段對話（讀不到時維持原狀）。</summary>
    private async Task ReloadCurrentAsync()
    {
        if (conversationId is { } id && isDisposed == false)
        {
            await OpenAsync(id);
        }
    }

    private async Task LoadConversationsAsync()
    {
        var result = await Api.ListAsync();
        if (result.Success == false || result.Data is not { } items)
        {
            Logger.LogWarning("Agent playground failed to load conversations. Message={Message}", result.UserMessage());
            ViewNotification.Error(NotificationService, "無法讀取「我的對話」，請稍後再試。");
            return;
        }

        conversations = items.Select(item => AgentDtoMapper.ToModel(item, "AgentPlayground")).ToList();
    }

    /// <summary>
    /// 改推論等級（0.9.170 起）：只記在畫面上，下一則訊息送出時帶給伺服器，伺服器從那則起生效並跟著對話存起來
    /// （Server 版改下拉就立即重建 agent，效果相同）。
    /// </summary>
    private void OnReasoningChanged(string? value)
    {
        reasoningChoice = value ?? string.Empty;
    }

    private void RevertReasoningChoice()
    {
        reasoningChoice = savedReasoning ?? string.Empty;
        reasoningSelectKey++;
    }

    private void ResetConversation()
    {
        conversationId = null;
        savedReasoning = null;
        entries.Clear();
        modelName = string.Empty;
        reasoningChoice = string.Empty;
        inputText = string.Empty;
        pendingApprovals = [];
        approvalVisible = false;
        htmlCache.Clear();
    }

    /// <summary>網址帶目前的對話（重新整理後回到同一段）；沒有對話時拿掉。</summary>
    private void SyncUrl()
    {
        var uri = NavigationManager.GetUriWithQueryParameter("conversation", conversationId?.ToString());
        if (string.Equals(uri, NavigationManager.Uri, StringComparison.Ordinal) == false)
        {
            NavigationManager.NavigateTo(uri, replace: true);
        }
    }

    private void ClearStreaming(StringBuilder streamed)
    {
        streamed.Clear();
        streamingHtml = string.Empty;
    }

    /// <summary>AI 回覆的安全 HTML（只轉一次）；超過顯示上限回 null。</summary>
    private string? HtmlOf(AiAgentTranscriptEntry entry)
    {
        if (htmlCache.TryGetValue(entry, out var cached))
        {
            return cached;
        }

        var html = AiMarkdownRenderer.ToSafeHtml(entry.Text);
        var value = html.Length <= MaxRenderedHtmlLength ? html : null;
        htmlCache[entry] = value;
        return value;
    }

    /// <summary>每秒更新「已 N 秒」。⚠️ 計時器回呼不在 renderer 的同步內容上，一定要 InvokeAsync。</summary>
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
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>離開頁面時中斷還在跑的請求：伺服器取消這次執行（已收到文字的會記一筆「已停止」），對話照樣存檔。</summary>
    public void Dispose()
    {
        isDisposed = true;
        cts?.Cancel();
    }
}
