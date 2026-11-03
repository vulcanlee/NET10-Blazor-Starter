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
using MyProject.Web.Ai.Agents;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Components.Views.AiAssistants;

/// <summary>
/// Agent 測試台（0.9.142 起；0.9.143 起對話會保存，左側「我的對話」可以接續、重新命名與刪除）。
/// 畫面只負責呈現：記帳、權限、核准前的權限檢查、稽核與存檔都在 <see cref="IAiAgentRunner"/>、對話物件與工具外殼裡；
/// 對話紀錄直接呈現 <see cref="AiAgentChat.Transcript"/>，畫面自己只多了「串流中、還沒成為一則」的那段文字。
/// </summary>
public partial class AgentPlaygroundView : IDisposable
{
    /// <summary>單則訊息的字數上限。</summary>
    private const int MaxInputLength = 2000;

    /// <summary>AI 回覆轉成 HTML 之後的顯示上限（同其他 AI 畫面）。</summary>
    private const int MaxRenderedHtmlLength = 512 * 1024;

    /// <summary>串流中重新轉 HTML 的節流（同其他 AI 畫面）。</summary>
    private static readonly TimeSpan StreamRenderInterval = TimeSpan.FromMilliseconds(150);

    private readonly ILogger<AgentPlaygroundView> logger;
    private readonly IAiAgentRunner runner;
    private readonly AiAgentConversationService conversationService;
    private readonly NotificationService notificationService;
    private readonly ModalService modalService;

    /// <summary>AI 回覆的 HTML（以紀錄物件為鍵，每則只轉一次）；null 表示內容異常龐大、不顯示。</summary>
    private readonly Dictionary<AiAgentTranscriptEntry, string?> htmlCache = [];

    private bool isAccessChecked;
    private string? unavailableReason;
    private AiAgentChat? chat;
    private string modelName = string.Empty;
    private IReadOnlyList<AiAgentConversationSummary> conversations = [];
    private string inputText = string.Empty;
    private bool isRunning;
    private bool isOpening;
    private int elapsedSeconds;
    private string streamingHtml = string.Empty;
    private bool approvalVisible;
    private IReadOnlyList<AiToolApprovalRequest> pendingApprovals = [];
    private bool renameVisible;
    private Guid renameTarget;
    private string renameTitle = string.Empty;
    private CancellationTokenSource? cts;
    private bool isDisposed;

    public AgentPlaygroundView(
        ILogger<AgentPlaygroundView> logger,
        IAiAgentRunner runner,
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

    private bool IsAwaitingApproval => chat?.IsAwaitingApproval == true;

    private bool IsBusy => isRunning || isOpening;

    private IReadOnlyList<AiAgentTranscriptEntry> Entries => chat?.Transcript ?? [];

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
        await LoadConversationsAsync();
    }

    private void OnNewConversation()
    {
        if (IsBusy)
        {
            return;
        }

        ResetConversation();
        unavailableReason = runner.UnavailableReason;
    }

    /// <summary>點左側清單的一段對話：接續它（停在核准的重新跳出核准窗）。</summary>
    private async Task OnOpenConversationAsync(AiAgentConversationSummary summary)
    {
        if (IsBusy || chat?.ConversationId == summary.ConversationId || unavailableReason is not null)
        {
            return;
        }

        isOpening = true;
        try
        {
            var resumed = await runner.ResumeAsync(AgentPlaygroundAgent.Definition, summary.ConversationId);
            if (resumed is null)
            {
                ViewNotification.Error(notificationService, "這段對話無法接續：內容可能已被清除，或格式已不相容。可以刪除它後開新對話。");
                return;
            }

            ResetConversation();
            chat = resumed;
            modelName = resumed.ModelName;
            if (resumed.IsAwaitingApproval)
            {
                pendingApprovals = resumed.PendingApprovals;
                approvalVisible = true;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent playground failed to resume a conversation. ConversationId={ConversationId}", summary.ConversationId);
            ViewNotification.UnexpectedError(notificationService, "無法開啟這段對話，請稍後再試。", ex);
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
        await LoadConversationsAsync();
    }

    private async Task OnDeleteAsync(AiAgentConversationSummary summary)
    {
        if (IsBusy)
        {
            return;
        }

        var confirmed = await ConfirmDialog.AskDestructiveAsync(
            modalService,
            "刪除對話",
            $"確定要刪除「{summary.Title}」嗎？對話內容會一併刪除，無法復原（AI 對話紀錄與 Token 用量不受影響）。",
            "刪除");
        if (confirmed == false)
        {
            return;
        }

        var result = await conversationService.DeleteAsync(summary.ConversationId);
        if (result.Success == false)
        {
            ViewNotification.Error(notificationService, result.Message);
            return;
        }

        if (chat?.ConversationId == summary.ConversationId)
        {
            ResetConversation();
        }

        ViewNotification.Warning(notificationService, "已刪除對話。");
        await LoadConversationsAsync();
    }

    private async Task OnSendAsync()
    {
        var text = inputText.Trim();
        if (IsBusy || IsAwaitingApproval || text.Length == 0 || unavailableReason is not null)
        {
            return;
        }

        try
        {
            chat ??= await runner.StartAsync(AgentPlaygroundAgent.Definition);
            modelName = chat.ModelName;
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

    /// <summary>核准或拒絕所有待決定的工具，然後讓 Agent 繼續（核准／拒絕的說明由對話物件記進紀錄）。</summary>
    private async Task OnDecideAsync(bool approve)
    {
        if (chat is null || isRunning)
        {
            return;
        }

        approvalVisible = false;
        var ids = approve ? pendingApprovals.Select(item => item.RequestId).ToList() : [];
        pendingApprovals = [];

        var current = chat;
        await RunAsync(token => current.RespondAsync(ids, token));
    }

    private void OnStop()
    {
        if (isRunning)
        {
            cts?.Cancel();
        }
    }

    /// <summary>跑一次執行：文字逐段長；工具活動、核准、結果出現時，對話物件已把完整的一則記進紀錄，串流區就清掉。</summary>
    private async Task RunAsync(Func<CancellationToken, IAsyncEnumerable<AiAgentUpdate>> start)
    {
        isRunning = true;
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
                    // 離開頁面：不再讀取，對話物件會把已收到的內容收進紀錄並存檔。
                    break;
                }

                if (update.ToolActivity is { } activity)
                {
                    if (activity.Completed == false)
                    {
                        ClearStreaming(streamed);
                        StateHasChanged();
                    }

                    continue;
                }

                if (update.Approvals is { } approvals)
                {
                    ClearStreaming(streamed);
                    pendingApprovals = approvals;
                    approvalVisible = true;
                    continue;
                }

                if (update.Result is not null)
                {
                    ClearStreaming(streamed);
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

            if (isDisposed || result is null)
            {
                // 停在核准：等使用者決定。
                return;
            }

            if (result.Success)
            {
                if (result.IsTruncatedByLength)
                {
                    ViewNotification.Caution(notificationService, "回應已達長度上限，結尾可能不完整。");
                }

                return;
            }

            if (result.Reason != AiAnalysisFailureReason.Canceled)
            {
                ViewNotification.Error(notificationService, result.ErrorMessage, result.ErrorDetail);
            }
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
                await LoadConversationsAsync();
                StateHasChanged();
            }
        }
    }

    private async Task LoadConversationsAsync()
    {
        try
        {
            conversations = await conversationService.ListMineAsync(AgentPlaygroundAgent.Definition.Name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent playground failed to load conversations.");
            ViewNotification.UnexpectedError(notificationService, "無法讀取「我的對話」，請稍後再試。", ex);
        }
    }

    private void ResetConversation()
    {
        chat = null;
        modelName = string.Empty;
        inputText = string.Empty;
        pendingApprovals = [];
        approvalVisible = false;
        htmlCache.Clear();
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

    /// <summary>離開頁面時取消還在跑的執行（已收到文字的會記一筆「已取消」，同其他 AI 畫面；對話照樣存檔）。</summary>
    public void Dispose()
    {
        isDisposed = true;
        cts?.Cancel();
    }
}
