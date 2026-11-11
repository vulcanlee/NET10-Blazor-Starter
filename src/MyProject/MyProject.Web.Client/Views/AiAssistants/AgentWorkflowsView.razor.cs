using System.Diagnostics;
using System.Net;
using System.Text;
using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MyProject.Dtos.Ai;
using MyProject.Dtos.Commons;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Ai;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Client.Views.AiAssistants;

/// <summary>
/// Agent 工作流程（0.9.144 起；0.10.7 起在 WASM 執行，WASM 遷移 M8）：選一條工作流程、輸入後執行，途中在畫面上回覆工具核准、Agent 提問與審閱關卡。
///
/// <para>
/// 畫面、按鈕與提示訊息照 Server 版。執行在伺服器（<see cref="AgentWorkflowApi"/>）：開始與每次回覆各是一個 NDJSON 串流，
/// 串流的每一行附上執行紀錄新增的幾則，畫面把它們接起來，自己只多了「各 agent 串流中、還沒成為一則」的文字。
/// 記帳、權限、稽核、存檔都在伺服器。
/// </para>
/// <para>
/// 與 Server 版相同：串流中離開頁面（或關掉、重新整理）會中斷整個流程；停下來等使用者時在站內離開頁面也會中斷（放棄）。
/// 不同的是停下來等使用者時重新整理或關掉分頁，執行會在伺服器留著（閒置 30 分鐘才結束），回到這頁會自動接回來。
/// </para>
/// </summary>
public partial class AgentWorkflowsView : IAsyncDisposable
{
    /// <summary>回答與審閱意見的字數上限（伺服器也擋）。</summary>
    private const int MaxInputLength = AgentWorkflowRespondRequestDto.MaxTextLength;

    /// <summary>輸入（多觀點審查要貼整份文件）的字數上限（伺服器也擋）。</summary>
    private const int MaxDocumentLength = AgentWorkflowStartRequestDto.MaxInputLength;

    /// <summary>AI 回覆轉成 HTML 之後的顯示上限（同其他 AI 畫面）。</summary>
    private const int MaxRenderedHtmlLength = 512 * 1024;

    /// <summary>串流中重新轉 HTML 的節流（同其他 AI 畫面）。</summary>
    private static readonly TimeSpan StreamRenderInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>AI 回覆的 HTML（以紀錄物件為鍵，每則只轉一次）；null 表示內容異常龐大、不顯示。</summary>
    private readonly Dictionary<AiAgentTranscriptEntry, string?> htmlCache = [];

    /// <summary>各 agent 串流中的文字（並行的 agent 各一段）。</summary>
    private readonly Dictionary<string, StringBuilder> streamed = new(StringComparer.Ordinal);
    private readonly List<string> activeAgents = [];

    private bool isAccessChecked;
    private string? unavailableReason;
    private List<AgentWorkflowDefinitionDto> workflows = [];
    private AgentWorkflowDefinitionDto selected = new();
    private int retentionDays;

    /// <summary>目前這次執行（開始後才有；結束後仍留著顯示紀錄，直到按「新的執行」）。</summary>
    private Guid? runId;

    /// <summary>這次執行是不是這個分頁開始或回覆過的（離開頁面時只放棄自己的；0.10.7 起）。</summary>
    private bool ownsRun;

    /// <summary>目前這次執行已結束（伺服器送來了結果，或執行已不在伺服器上）。</summary>
    private bool runFinished;
    private List<AiAgentTranscriptEntry> runEntries = [];
    private List<AgentWorkflowPendingRequestDto> pendingRequests = [];

    private Guid? viewingId;
    private IReadOnlyList<AiAgentTranscriptEntry> viewedTranscript = [];
    private string modelName = string.Empty;
    private IReadOnlyList<AgentWorkflowRecordDto> records = [];
    private string inputText = string.Empty;
    private string answerText = string.Empty;
    private string commentText = string.Empty;
    private bool isRunning;
    private bool isOpening;
    private int elapsedSeconds;
    private List<(string Agent, string Html)> streamingHtml = [];
    private bool renameVisible;
    private Guid renameTarget;
    private string renameTitle = string.Empty;
    private CancellationTokenSource? cts;
    private bool isDisposed;

    [Inject]
    public AgentWorkflowApi Api { get; set; } = default!;

    [Inject]
    public PagePermissions Permissions { get; set; } = default!;

    [Inject]
    public NotificationService notificationService { get; set; } = default!;

    [Inject]
    public ModalService modalService { get; set; } = default!;

    [Inject]
    public ILogger<AgentWorkflowsView> logger { get; set; } = default!;

    public string RoleMessage { get; set; } = string.Empty;

    private bool IsBusy => isRunning || isOpening;

    /// <summary>執行中（含停下來等使用者）但還沒結束。</summary>
    private bool HasActiveRun => runId is not null && runFinished == false;

    private IReadOnlyList<AiAgentTranscriptEntry> Entries => runId is not null ? runEntries : viewedTranscript;

    private IReadOnlyList<AgentWorkflowPendingRequestDto> PendingRequests => pendingRequests;

    protected override async Task OnInitializedAsync()
    {
        // 登入檢查已由版面（ClientMainLayout）完成；沒有權限的開頁稽核由伺服器在開頁紀錄時寫（shell/page-view）。
        if (Permissions.CanAccessPage(MagicObjectHelper.角色_Agent工作流程) == false)
        {
            isAccessChecked = true;
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            logger.LogWarning("Agent workflows denied because current user has not this role permission.");
            return;
        }

        await LoadInfoAsync();
        await ReconnectAsync();
        await LoadRecordsAsync();
        isAccessChecked = true;
    }

    private async Task OnSelectAsync(AgentWorkflowDefinitionDto workflow)
    {
        if (IsBusy || workflow == selected || await AskAbandonAsync() == false)
        {
            return;
        }

        selected = workflow;
        Reset();
        await LoadRecordsAsync();
    }

    private async Task OnNewRun()
    {
        if (IsBusy || await AskAbandonAsync() == false)
        {
            return;
        }

        Reset();
        await LoadInfoAsync();
    }

    /// <summary>點左側清單的一筆：唯讀回顧（工作流程不能從中途接續）。</summary>
    private async Task OnOpenRecordAsync(AgentWorkflowRecordDto summary)
    {
        if (IsBusy || viewingId == summary.ConversationId || runId == summary.ConversationId
            || await AskAbandonAsync() == false)
        {
            return;
        }

        isOpening = true;
        try
        {
            var result = await Api.RecordAsync(summary.ConversationId);
            if (result.Success == false || result.Data is not { } record)
            {
                // 找不到時伺服器的訊息就是 Server 版那一句（「這筆紀錄的內容已不存在（可能已被清除）。可以刪除它。」）。
                ViewNotification.Error(notificationService, result.StatusCode == (int)HttpStatusCode.NotFound
                    ? result.UserMessage()
                    : "無法開啟這筆紀錄，請稍後再試。");
                return;
            }

            Reset();
            viewingId = summary.ConversationId;
            viewedTranscript = record.Transcript.Select(AgentWorkflowDtoMapper.ToModel).ToList();
        }
        finally
        {
            isOpening = false;
        }
    }

    private void OnRename(AgentWorkflowRecordDto summary)
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
            ViewNotification.Error(notificationService, result.UserMessage());
            return;
        }

        renameVisible = false;
        ViewNotification.Warning(notificationService, "已重新命名。");
        await LoadRecordsAsync();
    }

    private async Task OnDeleteAsync(AgentWorkflowRecordDto summary)
    {
        if (IsBusy)
        {
            return;
        }

        var confirmed = await ConfirmDialog.AskDestructiveAsync(
            modalService,
            "刪除執行紀錄",
            $"確定要刪除「{summary.Title}」嗎？紀錄內容會一併刪除，無法復原（AI 對話紀錄與 Token 用量不受影響）。",
            "刪除");
        if (confirmed == false)
        {
            return;
        }

        // 這次執行還沒結束的話，伺服器會先放棄它（存成「中斷」）再刪除，同 Server 版。
        var result = await Api.DeleteAsync(summary.ConversationId);
        if (result.Success == false)
        {
            ViewNotification.Error(notificationService, result.UserMessage());
            return;
        }

        if (viewingId == summary.ConversationId || runId == summary.ConversationId)
        {
            Reset();
        }

        ViewNotification.Warning(notificationService, "已刪除紀錄。");
        await LoadRecordsAsync();
    }

    private async Task OnStartAsync()
    {
        var text = inputText.Trim();
        if (IsBusy || runId is not null || text.Length == 0 || unavailableReason is not null)
        {
            return;
        }

        var request = new AgentWorkflowStartRequestDto { Workflow = selected.Name, Input = text };
        ownsRun = true;
        await PumpAsync((onFrame, token) => Api.StartAsync(request, onFrame, token), answering: null);
    }

    private Task OnApproveAsync(AgentWorkflowPendingRequestDto pending, bool approve)
        => RespondAsync(pending, new AgentWorkflowRespondRequestDto
        {
            ApprovedRequestIds = approve ? pending.Approvals.Select(item => item.RequestId).ToList() : [],
        });

    private async Task OnAnswerAsync(AgentWorkflowPendingRequestDto pending)
    {
        if (string.IsNullOrWhiteSpace(answerText))
        {
            ViewNotification.ValidationError(notificationService, "請輸入回答。");
            return;
        }

        var answer = answerText.Trim();
        answerText = string.Empty;
        await RespondAsync(pending, new AgentWorkflowRespondRequestDto { Answer = answer });
    }

    private async Task OnReviewAsync(AgentWorkflowPendingRequestDto pending, string decision)
    {
        if (decision == AgentWorkflowReviewDecisions.Reject && string.IsNullOrWhiteSpace(commentText))
        {
            ViewNotification.ValidationError(notificationService, "退回時請寫下要修改的地方。");
            return;
        }

        var comment = commentText.Trim();
        commentText = string.Empty;
        await RespondAsync(pending, new AgentWorkflowRespondRequestDto { Decision = decision, Comment = comment });
    }

    private async Task RespondAsync(AgentWorkflowPendingRequestDto pending, AgentWorkflowRespondRequestDto response)
    {
        if (runId is not { } id || isRunning)
        {
            return;
        }

        response.RequestId = pending.RequestId;
        ownsRun = true;
        await PumpAsync((onFrame, token) => Api.RespondAsync(id, response, onFrame, token), answering: pending);
    }

    private void OnStop()
    {
        if (isRunning && runId is { } id)
        {
            // 伺服器取消整個流程，串流最後送來「已停止」的結果（同 Server 版，不另外提示）。
            _ = Api.StopAsync(id);
        }
    }

    /// <summary>
    /// 讀一輪串流：各 agent 的文字逐段長；某個 agent 完成、呼叫工具或停下來等使用者時，伺服器已把完整的一則記進紀錄（隨那一行送來），那段串流就清掉。
    /// </summary>
    /// <param name="send">送出開始或回覆的請求；畫面的取消權杖（離開頁面）會中斷請求，伺服器把這次執行存成「中斷」。</param>
    /// <param name="answering">回覆的是哪一筆請求（伺服器接受後從畫面拿掉）；開始執行時為 null。</param>
    private async Task PumpAsync(
        Func<Func<AgentWorkflowFrameDto, Task>, CancellationToken, Task<ApiResult<object>>> send, AgentWorkflowPendingRequestDto? answering)
    {
        var starting = answering is null;
        if (starting)
        {
            isOpening = true;
        }
        else
        {
            isRunning = true;
        }

        elapsedSeconds = 0;
        cts = new CancellationTokenSource();
        var token = cts.Token;
        _ = RunElapsedTickerAsync(token);
        StateHasChanged();

        var renderClock = Stopwatch.StartNew();
        var started = false;
        AgentWorkflowResultDto? result = null;

        Task OnFrame(AgentWorkflowFrameDto frame)
        {
            if (isDisposed)
            {
                // 離開頁面：不再更新畫面，伺服器會收尾並存成「中斷」。
                return Task.CompletedTask;
            }

            var changed = false;
            if (frame.Entries is { Count: > 0 } added)
            {
                runEntries.AddRange(added.Select(AgentWorkflowDtoMapper.ToModel));
                changed = true;
            }

            var agent = frame.Agent ?? string.Empty;
            switch (frame.Kind)
            {
                case AgentWorkflowFrameKinds.Start:
                    started = true;
                    runId = frame.RunId;
                    runFinished = false;
                    if (string.IsNullOrEmpty(frame.ModelName) == false)
                    {
                        modelName = frame.ModelName;
                    }

                    if (starting)
                    {
                        inputText = string.Empty;
                        isOpening = false;
                        isRunning = true;
                        elapsedSeconds = 0;
                    }
                    else
                    {
                        pendingRequests.Remove(answering!);
                    }

                    changed = true;
                    break;

                case AgentWorkflowFrameKinds.AgentStarted:
                    if (activeAgents.Contains(agent) == false)
                    {
                        activeAgents.Add(agent);
                    }

                    changed = true;
                    break;

                case AgentWorkflowFrameKinds.Delta when string.IsNullOrEmpty(frame.Text) == false:
                    BufferOf(agent).Append(frame.Text);
                    if (renderClock.Elapsed >= StreamRenderInterval || streamingHtml.All(item => item.Agent != agent))
                    {
                        RenderStreaming();
                        renderClock.Restart();
                        changed = true;
                    }

                    break;

                case AgentWorkflowFrameKinds.Tool when frame.Tool is { Completed: false }:
                case AgentWorkflowFrameKinds.AgentCompleted:
                case AgentWorkflowFrameKinds.Request:
                    ClearStreaming(agent);
                    if (frame.Kind != AgentWorkflowFrameKinds.Tool)
                    {
                        activeAgents.Remove(agent);
                    }

                    if (frame.Request is { } request)
                    {
                        pendingRequests.Add(request);
                    }

                    changed = true;
                    break;

                case AgentWorkflowFrameKinds.Output:
                    changed = true;
                    break;

                case AgentWorkflowFrameKinds.Result:
                    result = frame.Result;
                    break;
            }

            if (changed)
            {
                StateHasChanged();
            }

            return Task.CompletedTask;
        }

        try
        {
            var response = await send(OnFrame, token);
            if (isDisposed)
            {
                return;
            }

            if (result is null)
            {
                if (response.Success)
                {
                    // 停下來等使用者回覆。
                    return;
                }

                if (started == false)
                {
                    await ShowRejectedAsync(response, starting);
                    return;
                }

                // 串流途中斷線：伺服器把這次執行存成「中斷」（同離開頁面），不能再接續。
                logger.LogWarning("Agent workflow stream was cut off. Workflow={Workflow}, Message={Message}", selected.Name, response.UserMessage());
                runFinished = true;
                pendingRequests.Clear();
                ViewNotification.Error(notificationService, "工作流程發生未預期的錯誤，請稍後再試。");
                return;
            }

            runFinished = true;
            pendingRequests.Clear();
            switch (result.Outcome)
            {
                case AgentWorkflowOutcomes.Completed:
                    ViewNotification.Warning(notificationService, "工作流程已完成。");
                    break;
                case AgentWorkflowOutcomes.Ended:
                    ViewNotification.Caution(notificationService, "流程已結束，報告沒有定稿。");
                    break;
                case AgentWorkflowOutcomes.Failed:
                    ViewNotification.Error(notificationService, result.ErrorMessage ?? "工作流程執行失敗。");
                    break;
            }
        }
        finally
        {
            cts?.Cancel();
            cts?.Dispose();
            cts = null;
            streamed.Clear();
            streamingHtml = [];
            activeAgents.Clear();
            isRunning = false;
            isOpening = false;
            if (isDisposed == false)
            {
                await LoadRecordsAsync();
                StateHasChanged();
            }
        }
    }

    /// <summary>伺服器一開始就拒絕（規則不符、執行已不在、別的分頁正在用…）：請求原樣還在畫面上，提示原因。</summary>
    private async Task ShowRejectedAsync(ApiResult<object> response, bool starting)
    {
        switch (response.StatusCode)
        {
            case (int)HttpStatusCode.BadRequest:
                // 伺服器的規則檢查（畫面已先擋過，正常不會到這裡）。
                ViewNotification.ValidationError(notificationService, response.UserMessage());
                break;
            case (int)HttpStatusCode.NotFound when starting == false:
                // 執行已不在伺服器上（閒置太久、網站重新啟動）：只能回顧。
                runFinished = true;
                pendingRequests.Clear();
                ViewNotification.Error(notificationService, response.UserMessage());
                break;
            case (int)HttpStatusCode.Conflict when starting == false:
                // 這個請求已被另一個分頁回覆過或正在處理：重讀伺服器上的狀態，畫面才不會停在舊的待回覆（0.10.7 獨立審查）。
                ViewNotification.Error(notificationService, response.UserMessage());
                await ReconnectAsync();
                break;
            case (int)HttpStatusCode.Conflict:
            case (int)HttpStatusCode.Forbidden:
            case (int)HttpStatusCode.TooManyRequests:
                ViewNotification.Error(notificationService, response.UserMessage());
                break;
            default:
                ViewNotification.Error(notificationService, starting ? "無法開始執行，請稍後再試。" : "工作流程發生未預期的錯誤，請稍後再試。");
                break;
        }
    }

    /// <summary>還有沒結束的執行時，先問使用者要不要放棄它（放棄就存成「中斷」，不能接續）。</summary>
    private async Task<bool> AskAbandonAsync()
    {
        if (HasActiveRun == false)
        {
            return true;
        }

        var confirmed = await ConfirmDialog.AskDestructiveAsync(
            modalService,
            "放棄目前的執行",
            "目前的執行還在等你回覆。離開它就會中斷，之後只能回顧、不能接續。確定要離開嗎？",
            "放棄執行");
        if (confirmed)
        {
            await AbandonAsync();
        }

        return confirmed;
    }

    private async Task AbandonAsync()
    {
        if (runId is { } id)
        {
            await Api.AbandonAsync(id);
        }
    }

    /// <summary>重新整理頁面後接回自己停下來等回覆的那一次執行（伺服器上還在的話）。</summary>
    private async Task ReconnectAsync()
    {
        var result = await Api.ActiveAsync();
        if (result.Success == false || result.Data is not { } active)
        {
            return;
        }

        selected = workflows.FirstOrDefault(item => item.Name == active.Workflow) ?? selected;
        runId = active.RunId;
        // 接回的執行不算這個分頁的（可能是另一個分頁開的）：只看一下就離開時不要放棄它（0.10.7 獨立審查）。
        ownsRun = false;
        runFinished = false;
        modelName = active.ModelName;
        runEntries = active.Transcript.Select(AgentWorkflowDtoMapper.ToModel).ToList();
        pendingRequests = active.PendingRequests;
    }

    /// <summary>可選的工作流程、AI 是否可用與保留天數（設定可能改了，「新的執行」時重讀）。</summary>
    private async Task LoadInfoAsync()
    {
        var result = await Api.InfoAsync();
        if (result.Success == false || result.Data is not { } info)
        {
            unavailableReason ??= result.UserMessage();
            return;
        }

        unavailableReason = info.UnavailableReason;
        retentionDays = info.RetentionDays;
        if (workflows.Count == 0)
        {
            workflows = info.Workflows;
            selected = workflows.FirstOrDefault() ?? selected;
        }
    }

    private async Task LoadRecordsAsync()
    {
        if (string.IsNullOrEmpty(selected.Name))
        {
            return;
        }

        var result = await Api.RecordsAsync(selected.Name);
        if (result.Success == false || result.Data is not { } data)
        {
            logger.LogWarning("Agent workflows failed to load records. Message={Message}", result.UserMessage());
            ViewNotification.Error(notificationService, "無法讀取「我的執行紀錄」，請稍後再試。");
            return;
        }

        records = data;
    }

    private void Reset()
    {
        runId = null;
        runFinished = false;
        runEntries = [];
        pendingRequests = [];
        viewingId = null;
        viewedTranscript = [];
        modelName = string.Empty;
        inputText = string.Empty;
        answerText = string.Empty;
        commentText = string.Empty;
        streamed.Clear();
        streamingHtml = [];
        activeAgents.Clear();
        htmlCache.Clear();
    }

    private StringBuilder BufferOf(string agent)
    {
        if (streamed.TryGetValue(agent, out var buffer) == false)
        {
            buffer = new StringBuilder();
            streamed[agent] = buffer;
        }

        return buffer;
    }

    private void ClearStreaming(string agent)
    {
        streamed.Remove(agent);
        RenderStreaming();
    }

    private void RenderStreaming()
    {
        var list = new List<(string Agent, string Html)>();
        foreach (var (agent, buffer) in streamed)
        {
            var html = AiMarkdownRenderer.ToSafeHtml(buffer.ToString());
            if (html.Length <= MaxRenderedHtmlLength)
            {
                list.Add((agent, html));
            }
        }

        streamingHtml = list;
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

    /// <summary>
    /// 離開頁面：串流中就中斷請求（伺服器把流程存成「中斷」）；停下來等回覆的執行也放棄（同 Server 版，工作流程不能從中途接續）。
    /// 重新整理或關掉分頁不會走到這裡：等回覆的執行留在伺服器，回來時自動接回。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        isDisposed = true;
        var streaming = cts is not null;
        cts?.Cancel();
        // 只放棄這個分頁自己開始或回覆過的執行，而且伺服器只在沒有別的請求正在用它時才放棄（0.10.7 獨立審查：
        // 另一個分頁接回來看一下就離開，不可以中斷原本分頁正在跑的流程）。
        if (streaming == false && ownsRun && HasActiveRun && runId is { } id)
        {
            _ = Api.AbandonAsync(id, onlyIfIdle: true);
        }

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
