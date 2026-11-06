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
using MyProject.Web.Ai.Agents.AdminAssistant;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Components.Views.Admins;

/// <summary>
/// 系統管理助理（0.9.155 起）：管理員專屬，權限鍵刻意不進角色矩陣，以 CheckIsAdmin 判斷。
/// 互動與 Agent 測試台相同（對話保存、接續、核准窗、停止產生）；開對話與接續改用 <see cref="AdminAssistantTurnPlanner"/>，
/// 每則訊息由規劃器先分派領域，AI 回覆上方顯示「由誰回答」（<see cref="AiAgentTranscriptEntry.Agent"/>）。
/// </summary>
public partial class AdminAssistantView : IDisposable
{
    /// <summary>單則訊息的字數上限。</summary>
    private const int MaxInputLength = 2000;

    /// <summary>AI 回覆轉成 HTML 之後的顯示上限（同其他 AI 畫面）。</summary>
    private const int MaxRenderedHtmlLength = 512 * 1024;

    /// <summary>串流中重新轉 HTML 的節流（同其他 AI 畫面）。</summary>
    private static readonly TimeSpan StreamRenderInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>空白對話時的範例問句。</summary>
    private static readonly IReadOnlyList<string> SamplePrompts =
        ["幫我巡一下系統", "最近排程的執行狀況", "昨晚備份為什麼失敗", "哪些帳號超過 180 天沒登入", "把日誌等級調成 Debug"];

    private readonly ILogger<AdminAssistantView> logger;
    private readonly IAiAgentRunner runner;
    private readonly AdminAssistantTurnPlanner planner;
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
    private bool hasUpdate;
    private int elapsedSeconds;
    private string streamingHtml = string.Empty;
    private bool approvalVisible;
    private IReadOnlyList<AiToolApprovalRequest> pendingApprovals = [];

    /// <summary>核准窗的確認輸入（0.9.159 起，有不可復原的動作時才出現）；每次決定後清空。</summary>
    private string confirmInput = string.Empty;
    private bool renameVisible;
    private Guid renameTarget;
    private string renameTitle = string.Empty;
    private CancellationTokenSource? cts;
    private bool isDisposed;

    public AdminAssistantView(
        ILogger<AdminAssistantView> logger,
        IAiAgentRunner runner,
        AdminAssistantTurnPlanner planner,
        AiAgentConversationService conversationService,
        NotificationService notificationService,
        ModalService modalService)
    {
        this.logger = logger;
        this.runner = runner;
        this.planner = planner;
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

    /// <summary>執行中的狀態文字：還沒收到任何更新時是在分派，之後依有沒有文字分思考中／產生中。</summary>
    private string RunningText => hasUpdate == false ? "分派中" : streamingHtml.Length > 0 ? "產生中" : "思考中";

    protected override async Task OnInitializedAsync()
    {
        var checkResult = await AuthenticationStateHelper.Check(AuthStateProvider, NavigationManager);
        if (checkResult != AuthenticationCheckResult.Succeeded)
        {
            return;
        }

        isAccessChecked = true;

        if (AuthenticationStateHelper.CheckIsAdmin() == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            await AuthenticationStateHelper.RecordPageAccessDeniedAsync("/admin-assistant");
            logger.LogWarning("Admin assistant denied because the current user is not an administrator.");
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
            var resumed = await runner.ResumeAsync(planner, summary.ConversationId);
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
                confirmInput = string.Empty;
                approvalVisible = true;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Admin assistant failed to resume a conversation. ConversationId={ConversationId}", summary.ConversationId);
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
            chat ??= await runner.StartAsync(planner);
            modelName = chat.ModelName;
            inputText = string.Empty;
            var current = chat;
            await RunAsync(token => current.SendAsync(text, token));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Admin assistant failed to start a conversation.");
            ViewNotification.UnexpectedError(notificationService, "無法開始對話，請稍後再試。", ex);
        }
    }

    /// <summary>核准或拒絕所有待決定的工具，然後讓助理繼續（核准／拒絕的說明由對話物件記進紀錄）。</summary>
    private async Task OnDecideAsync(bool approve)
    {
        if (chat is null || isRunning)
        {
            return;
        }

        // 按鈕停用之外再擋一次：不可復原的動作沒有輸入「確認」就不核准。
        if (approve && AdminAssistantApproval.CanApprove(pendingApprovals, confirmInput) == false)
        {
            return;
        }

        approvalVisible = false;
        var ids = approve ? pendingApprovals.Select(item => item.RequestId).ToList() : [];
        pendingApprovals = [];
        confirmInput = string.Empty;

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
        hasUpdate = false;
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

                hasUpdate = true;
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
                    confirmInput = string.Empty;
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
            logger.LogError(ex, "Admin assistant run failed unexpectedly.");
            ViewNotification.UnexpectedError(notificationService, "系統管理助理發生未預期的錯誤，請稍後再試。", ex);
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
            conversations = await conversationService.ListMineAsync(AdminAssistantAgent.Name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Admin assistant failed to load conversations.");
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
