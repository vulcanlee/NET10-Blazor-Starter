using System.Diagnostics;
using System.Text;
using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Ai;
using MyProject.Web.Ai.Workflows;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Components.Views.AiAssistants;

/// <summary>
/// Agent 工作流程（0.9.144 起）：選一條工作流程、輸入後執行，途中在畫面上回覆工具核准、Agent 提問與審閱關卡。
/// 畫面只負責呈現：記帳、權限、稽核、存檔都在 <see cref="IAiWorkflowRunner"/> 與 <see cref="AiWorkflowRun"/> 裡；
/// 紀錄直接呈現 <see cref="AiWorkflowRun.Transcript"/>，畫面自己只多了「各 agent 串流中、還沒成為一則」的文字。
/// </summary>
public partial class AgentWorkflowsView : IAsyncDisposable
{
    /// <summary>回答與審閱意見的字數上限。</summary>
    private const int MaxInputLength = 2000;

    /// <summary>輸入（多觀點審查要貼整份文件）的字數上限。</summary>
    private const int MaxDocumentLength = 20000;

    /// <summary>AI 回覆轉成 HTML 之後的顯示上限（同其他 AI 畫面）。</summary>
    private const int MaxRenderedHtmlLength = 512 * 1024;

    /// <summary>串流中重新轉 HTML 的節流（同其他 AI 畫面）。</summary>
    private static readonly TimeSpan StreamRenderInterval = TimeSpan.FromMilliseconds(150);

    private readonly ILogger<AgentWorkflowsView> logger;
    private readonly IAiWorkflowRunner runner;
    private readonly AiAgentConversationService conversationService;
    private readonly NotificationService notificationService;
    private readonly ModalService modalService;

    /// <summary>AI 回覆的 HTML（以紀錄物件為鍵，每則只轉一次）；null 表示內容異常龐大、不顯示。</summary>
    private readonly Dictionary<AiAgentTranscriptEntry, string?> htmlCache = [];

    /// <summary>各 agent 串流中的文字（並行的 agent 各一段）。</summary>
    private readonly Dictionary<string, StringBuilder> streamed = new(StringComparer.Ordinal);
    private readonly List<string> activeAgents = [];

    private bool isAccessChecked;
    private string? unavailableReason;
    private AiWorkflowDefinition selected = AiWorkflowCatalog.All[0];
    private AiWorkflowRun? run;
    private Guid? viewingId;
    private IReadOnlyList<AiAgentTranscriptEntry> viewedTranscript = [];
    private string modelName = string.Empty;
    private IReadOnlyList<AiAgentConversationSummary> records = [];
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

    public AgentWorkflowsView(
        ILogger<AgentWorkflowsView> logger,
        IAiWorkflowRunner runner,
        AiAgentConversationService conversationService,
        NotificationService notificationService,
        ModalService modalService)
    {
        this.logger = logger;
        this.runner = runner;
        this.conversationService = conversationService;
        this.notificationService = notificationService;
        this.modalService = modalService;
    }

    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    public AuthenticationStateProvider AuthStateProvider { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    public string RoleMessage { get; set; } = string.Empty;

    private bool IsBusy => isRunning || isOpening;

    /// <summary>執行中（含停下來等使用者）但還沒結束。</summary>
    private bool HasActiveRun => run is not null && run.Result is null;

    private IReadOnlyList<AiAgentTranscriptEntry> Entries => run?.Transcript ?? viewedTranscript;

    private IReadOnlyList<AiWorkflowPendingRequest> PendingRequests => run?.PendingRequests ?? [];

    protected override async Task OnInitializedAsync()
    {
        var checkResult = await AuthenticationStateHelper.Check(AuthStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            return;
        }

        isAccessChecked = true;

        if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_Agent工作流程) == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/agent-workflows");
            logger.LogWarning("Agent workflows denied because current user has not this role permission.");
            return;
        }

        unavailableReason = runner.UnavailableReason;
        await LoadRecordsAsync();
    }

    private async Task OnSelectAsync(AiWorkflowDefinition workflow)
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
        unavailableReason = runner.UnavailableReason;
    }

    /// <summary>點左側清單的一筆：唯讀回顧（工作流程不能從中途接續）。</summary>
    private async Task OnOpenRecordAsync(AiAgentConversationSummary summary)
    {
        if (IsBusy || viewingId == summary.ConversationId || run?.ConversationId == summary.ConversationId
            || await AskAbandonAsync() == false)
        {
            return;
        }

        isOpening = true;
        try
        {
            var loaded = await conversationService.LoadMineAsync(summary.ConversationId);
            if (loaded?.Content is not { } content)
            {
                ViewNotification.Error(notificationService, "這筆紀錄的內容已不存在（可能已被清除）。可以刪除它。");
                return;
            }

            Reset();
            viewingId = summary.ConversationId;
            viewedTranscript = content.Transcript;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent workflows failed to open a record. ConversationId={ConversationId}", summary.ConversationId);
            ViewNotification.UnexpectedError(notificationService, "無法開啟這筆紀錄，請稍後再試。", ex);
        }
        finally
        {
            isOpening = false;
        }
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
        var result = await conversationService.RenameAsync(renameTarget, renameTitle);
        if (result.Success == false)
        {
            ViewNotification.Error(notificationService, result.Message);
            return;
        }

        renameVisible = false;
        ViewNotification.Warning(notificationService, "已重新命名。");
        await LoadRecordsAsync();
    }

    private async Task OnDeleteAsync(AiAgentConversationSummary summary)
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

        if (run?.ConversationId == summary.ConversationId)
        {
            await AbandonAsync();
        }

        var result = await conversationService.DeleteAsync(summary.ConversationId);
        if (result.Success == false)
        {
            ViewNotification.Error(notificationService, result.Message);
            return;
        }

        if (viewingId == summary.ConversationId || run?.ConversationId == summary.ConversationId)
        {
            Reset();
        }

        ViewNotification.Warning(notificationService, "已刪除紀錄。");
        await LoadRecordsAsync();
    }

    private async Task OnStartAsync()
    {
        var text = inputText.Trim();
        if (IsBusy || run is not null || text.Length == 0 || unavailableReason is not null)
        {
            return;
        }

        try
        {
            isOpening = true;
            var started = await runner.StartAsync(selected);
            run = started;
            modelName = started.ModelName;
            inputText = string.Empty;
            isOpening = false;
            await PumpAsync(started.RunAsync(text));
        }
        catch (Exception ex)
        {
            isOpening = false;
            logger.LogError(ex, "Agent workflows failed to start. Workflow={Workflow}", selected.Name);
            ViewNotification.UnexpectedError(notificationService, "無法開始執行，請稍後再試。", ex);
        }
    }

    private Task OnApproveAsync(AiWorkflowPendingRequest pending, bool approve)
        => RespondAsync(pending, new AiWorkflowHumanResponse
        {
            ApprovedRequestIds = approve ? pending.Request.Approvals.Select(item => item.RequestId).ToList() : [],
        });

    private async Task OnAnswerAsync(AiWorkflowPendingRequest pending)
    {
        if (string.IsNullOrWhiteSpace(answerText))
        {
            ViewNotification.ValidationError(notificationService, "請輸入回答。");
            return;
        }

        var answer = answerText.Trim();
        answerText = string.Empty;
        await RespondAsync(pending, new AiWorkflowHumanResponse { Answer = answer });
    }

    private async Task OnReviewAsync(AiWorkflowPendingRequest pending, AiReviewDecision decision)
    {
        if (decision == AiReviewDecision.Reject && string.IsNullOrWhiteSpace(commentText))
        {
            ViewNotification.ValidationError(notificationService, "退回時請寫下要修改的地方。");
            return;
        }

        var comment = commentText.Trim();
        commentText = string.Empty;
        await RespondAsync(pending, new AiWorkflowHumanResponse { Decision = decision, Comment = comment });
    }

    private async Task RespondAsync(AiWorkflowPendingRequest pending, AiWorkflowHumanResponse response)
    {
        if (run is not { } current || isRunning)
        {
            return;
        }

        IAsyncEnumerable<AiWorkflowUpdate> stream;
        try
        {
            stream = current.RespondAsync(pending.RequestId, response);
        }
        catch (InvalidOperationException ex)
        {
            // 執行器的規則檢查（畫面已先擋過，正常不會到這裡）。
            ViewNotification.ValidationError(notificationService, ex.Message);
            return;
        }

        await PumpAsync(stream);
    }

    private void OnStop()
    {
        if (isRunning)
        {
            cts?.Cancel();
        }
    }

    /// <summary>
    /// 讀一輪更新：各 agent 的文字逐段長；某個 agent 完成、呼叫工具或停下來等使用者時，執行物件已把完整的一則記進紀錄，那段串流就清掉。
    /// </summary>
    /// <param name="stream">執行物件回傳的讀取；畫面的取消權杖（「停止」）以 <c>WithCancellation</c> 帶進去，會取消整個工作流程。</param>
    private async Task PumpAsync(IAsyncEnumerable<AiWorkflowUpdate> stream)
    {
        isRunning = true;
        elapsedSeconds = 0;
        cts = new CancellationTokenSource();
        var token = cts.Token;
        _ = RunElapsedTickerAsync(token);
        StateHasChanged();

        var renderClock = Stopwatch.StartNew();
        AiWorkflowResult? result = null;

        try
        {
            await foreach (var update in stream.WithCancellation(token))
            {
                if (isDisposed)
                {
                    // 離開頁面：不再讀取，執行物件會收尾並存成「中斷」。
                    break;
                }

                var agent = update.Agent ?? string.Empty;
                if (update.AgentStarted)
                {
                    if (activeAgents.Contains(agent) == false)
                    {
                        activeAgents.Add(agent);
                    }

                    StateHasChanged();
                }
                else if (update.TextDelta.Length > 0)
                {
                    BufferOf(agent).Append(update.TextDelta);
                    if (renderClock.Elapsed >= StreamRenderInterval || streamingHtml.All(item => item.Agent != agent))
                    {
                        RenderStreaming();
                        renderClock.Restart();
                        StateHasChanged();
                    }
                }
                else if (update.ToolActivity is { Completed: false } || update.AgentCompleted || update.Request is not null)
                {
                    ClearStreaming(agent);
                    if (update.AgentCompleted || update.Request is not null)
                    {
                        activeAgents.Remove(agent);
                    }

                    StateHasChanged();
                }
                else if (update.Output is not null)
                {
                    StateHasChanged();
                }
                else if (update.Result is not null)
                {
                    result = update.Result;
                }
            }

            if (isDisposed || result is null)
            {
                // 停下來等使用者回覆。
                return;
            }

            switch (result.Outcome)
            {
                case AiWorkflowOutcome.Completed:
                    ViewNotification.Warning(notificationService, "工作流程已完成。");
                    break;
                case AiWorkflowOutcome.Ended:
                    ViewNotification.Caution(notificationService, "流程已結束，報告沒有定稿。");
                    break;
                case AiWorkflowOutcome.Failed:
                    ViewNotification.Error(notificationService, result.ErrorMessage ?? "工作流程執行失敗。");
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Agent workflow run failed unexpectedly. Workflow={Workflow}", selected.Name);
            ViewNotification.UnexpectedError(notificationService, "工作流程發生未預期的錯誤，請稍後再試。", ex);
        }
        catch (OperationCanceledException)
        {
            // 按「停止」：執行物件已記下「已停止」。
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
            if (isDisposed == false)
            {
                await LoadRecordsAsync();
                StateHasChanged();
            }
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
        if (run is { } current)
        {
            await current.DisposeAsync();
        }
    }

    private async Task LoadRecordsAsync()
    {
        try
        {
            records = await conversationService.ListMineAsync(selected.Name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent workflows failed to load records.");
            ViewNotification.UnexpectedError(notificationService, "無法讀取「我的執行紀錄」，請稍後再試。", ex);
        }
    }

    private void Reset()
    {
        run = null;
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

    /// <summary>離開頁面：還沒結束的執行取消並存成「中斷」（工作流程不能從中途接續）。</summary>
    public async ValueTask DisposeAsync()
    {
        isDisposed = true;
        cts?.Cancel();
        if (run is { } current)
        {
            await current.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
