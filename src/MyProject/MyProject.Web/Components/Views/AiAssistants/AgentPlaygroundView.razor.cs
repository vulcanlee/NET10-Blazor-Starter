using System.Diagnostics;
using System.Text;
using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using MyProject.Business.Services.Other;
using MyProject.Share.Helpers;
using MyProject.Web.Ai;
using MyProject.Web.Ai.Agents;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Components.Views.AiAssistants;

/// <summary>
/// Agent 測試台（0.9.142 起）。畫面只負責呈現：記帳、權限、核准前的權限檢查與稽核都在 <see cref="IAiAgentRunner"/> 與工具外殼裡。
/// </summary>
public partial class AgentPlaygroundView : IDisposable
{
    private enum EntryKind
    {
        User,
        Assistant,

        /// <summary>Agent 呼叫了哪個工具（不含參數與結果，那些在 AI 對話紀錄）。</summary>
        Tool,

        /// <summary>核准／拒絕、停止等狀態說明。</summary>
        Notice,
        Error,
    }

    private sealed record ChatEntry(EntryKind Kind, string Text, string Html = "");

    /// <summary>單則訊息的字數上限。</summary>
    private const int MaxInputLength = 2000;

    /// <summary>AI 回覆轉成 HTML 之後的顯示上限（同其他 AI 畫面）。</summary>
    private const int MaxRenderedHtmlLength = 512 * 1024;

    /// <summary>串流中重新轉 HTML 的節流（同其他 AI 畫面）。</summary>
    private static readonly TimeSpan StreamRenderInterval = TimeSpan.FromMilliseconds(150);

    private readonly ILogger<AgentPlaygroundView> logger;
    private readonly IAiAgentRunner runner;
    private readonly NotificationService notificationService;
    private readonly List<ChatEntry> entries = [];

    private bool isAccessChecked;
    private string? unavailableReason;
    private AiAgentChat? chat;
    private string modelName = string.Empty;
    private string inputText = string.Empty;
    private bool isRunning;
    private bool stopRequested;
    private int elapsedSeconds;
    private string streamingHtml = string.Empty;
    private bool approvalVisible;
    private IReadOnlyList<AiToolApprovalRequest> pendingApprovals = [];
    private CancellationTokenSource? cts;
    private bool isDisposed;

    public AgentPlaygroundView(
        ILogger<AgentPlaygroundView> logger,
        IAiAgentRunner runner,
        NotificationService notificationService)
    {
        this.logger = logger;
        this.runner = runner;
        this.notificationService = notificationService;
    }

    [Inject]
    public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;

    [Inject]
    public AuthenticationStateProvider AuthStateProvider { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    public string RoleMessage { get; set; } = string.Empty;

    private bool IsAwaitingApproval => chat?.IsAwaitingApproval == true;

    protected override async Task OnInitializedAsync()
    {
        var checkResult = await AuthenticationStateHelper.Check(AuthStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            return;
        }

        isAccessChecked = true;

        if (AuthenticationStateHelper.CheckAccessPage(MagicObjectHelper.角色_Agent測試台) == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/agent-playground");
            logger.LogWarning("Agent playground denied because current user has not this role permission.");
            return;
        }

        unavailableReason = runner.UnavailableReason;
    }

    private void OnNewConversation()
    {
        cts?.Cancel();
        chat = null;
        modelName = string.Empty;
        entries.Clear();
        inputText = string.Empty;
        pendingApprovals = [];
        approvalVisible = false;
        unavailableReason = runner.UnavailableReason;
    }

    private async Task OnSendAsync()
    {
        var text = inputText.Trim();
        if (isRunning || IsAwaitingApproval || text.Length == 0 || unavailableReason is not null)
        {
            return;
        }

        try
        {
            if (chat is null)
            {
                chat = await runner.StartAsync(AgentPlaygroundAgent.Definition);
                modelName = chat.ModelName;
            }

            entries.Add(new ChatEntry(EntryKind.User, text));
            inputText = string.Empty;
            var current = chat;
            await RunAsync(token => current.SendAsync(text, token));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent playground failed to start a conversation.");
            ViewNotification.UnexpectedError(notificationService, "無法開始對話，請稍後再試。", ex);
        }
    }

    /// <summary>核准或拒絕所有待決定的工具，然後讓 Agent 繼續。</summary>
    private async Task OnDecideAsync(bool approve)
    {
        if (chat is null || isRunning)
        {
            return;
        }

        approvalVisible = false;
        var names = string.Join("、", pendingApprovals.Select(item => item.DisplayName));
        entries.Add(new ChatEntry(EntryKind.Notice, approve ? $"你核准了：{names}" : $"你拒絕了：{names}"));
        var ids = approve ? pendingApprovals.Select(item => item.RequestId).ToList() : [];
        pendingApprovals = [];

        var current = chat;
        await RunAsync(token => current.RespondAsync(ids, token));
    }

    private void OnStop()
    {
        if (isRunning)
        {
            stopRequested = true;
            cts?.Cancel();
        }
    }

    /// <summary>跑一次執行並把更新畫到對話上（文字逐段長、工具活動另起一行、需要核准就開核准窗）。</summary>
    private async Task RunAsync(Func<CancellationToken, IAsyncEnumerable<AiAgentUpdate>> start)
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
        AiAnalysisResult? result = null;

        try
        {
            await foreach (var update in start(token))
            {
                if (isDisposed)
                {
                    break;
                }

                if (update.ToolActivity is { Completed: false } activity)
                {
                    // 工具前面若有文字（例如「我來查一下」），先收成一則回覆，順序才對。
                    FlushAssistant(streamed, interrupted: false);
                    entries.Add(new ChatEntry(EntryKind.Tool, $"呼叫工具：{activity.DisplayName}"));
                    StateHasChanged();
                    continue;
                }

                if (update.Approvals is { } approvals)
                {
                    FlushAssistant(streamed, interrupted: false);
                    pendingApprovals = approvals;
                    approvalVisible = true;
                    continue;
                }

                if (update.Result is not null)
                {
                    result = update.Result;
                    continue;
                }

                if (update.TextDelta.Length > 0)
                {
                    streamed.Append(update.TextDelta);
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
                }
            }

            if (isDisposed)
            {
                return;
            }

            if (result is null)
            {
                // 停在核准：文字已收好，等使用者決定。
                return;
            }

            if (result.Success)
            {
                FlushAssistant(streamed, interrupted: false);
                if (result.IsTruncatedByLength)
                {
                    ViewNotification.Caution(notificationService, "回應已達長度上限，結尾可能不完整。");
                }

                return;
            }

            if (result.Reason == AiAnalysisFailureReason.Canceled)
            {
                FlushAssistant(streamed, interrupted: streamed.Length > 0);
                if (stopRequested)
                {
                    entries.Add(new ChatEntry(EntryKind.Notice, streamed.Length > 0 ? "已停止產生（上面的回覆不完整）。" : "已停止產生，尚未收到任何內容。"));
                }

                return;
            }

            FlushAssistant(streamed, interrupted: false);
            entries.Add(new ChatEntry(EntryKind.Error, result.ErrorMessage));
            ViewNotification.Error(notificationService, result.ErrorMessage, result.ErrorDetail);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent playground run failed unexpectedly.");
            ViewNotification.UnexpectedError(notificationService, "Agent 執行發生未預期的錯誤，請稍後再試。", ex);
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
                StateHasChanged();
            }
        }
    }

    /// <summary>把累積的文字收成一則 AI 回覆（中途停止的加註已中止）。</summary>
    private void FlushAssistant(StringBuilder streamed, bool interrupted)
    {
        if (streamed.Length == 0)
        {
            return;
        }

        var markdown = interrupted ? AiInterruption.Annotate(streamed.ToString()) : streamed.ToString();
        var html = AiMarkdownRenderer.ToSafeHtml(markdown);
        entries.Add(html.Length <= MaxRenderedHtmlLength
            ? new ChatEntry(EntryKind.Assistant, markdown, html)
            : new ChatEntry(EntryKind.Error, "AI 回傳內容異常龐大，已中止顯示。"));
        streamed.Clear();
        streamingHtml = string.Empty;
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

    /// <summary>離開頁面時取消還在跑的執行（已收到文字的會記一筆「已取消」，同其他 AI 畫面）。</summary>
    public void Dispose()
    {
        isDisposed = true;
        cts?.Cancel();
    }
}
