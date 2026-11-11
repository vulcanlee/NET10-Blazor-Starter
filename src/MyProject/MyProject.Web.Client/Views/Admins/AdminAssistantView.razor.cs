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
using MyProject.Web.Ai.Agents.AdminAssistant;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Client.Views.Admins;

/// <summary>
/// 系統管理助理（0.9.155 起）：管理員專屬，權限鍵刻意不進角色矩陣，以 IsAdmin 判斷。
/// 互動與 Agent 測試台相同（對話保存、接續、核准窗、停止產生）；每則訊息由伺服器的規劃器先分派領域，
/// AI 回覆上方顯示「由誰回答」（<see cref="AiAgentTranscriptEntry.Agent"/>）。
/// 0.10.7 起在 WASM 執行（WASM 遷移 M8）：畫面、對話窗與提示訊息照 Server 版；每次送出或核准都是一個請求
/// （<see cref="AdminAssistantApi"/>，NDJSON 串流），核准後沿用分派到的領域（伺服器存在對話裡）。
/// 不可復原的動作要輸入「確認」：畫面停用核准鈕，伺服器也再擋一次。網址帶 <c>?conversation={id}</c>，重新整理後回到同一段對話。
/// </summary>
public partial class AdminAssistantView : IDisposable
{
    /// <summary>單則訊息的字數上限（伺服器也擋）。</summary>
    private const int MaxInputLength = AgentConversationLimits.MaxMessageLength;

    /// <summary>AI 回覆轉成 HTML 之後的顯示上限（同其他 AI 畫面）。</summary>
    private const int MaxRenderedHtmlLength = 512 * 1024;

    /// <summary>伺服器 <c>AiAnalysisFailureReason.Canceled</c> 的名稱（<see cref="AiResultDto.Reason"/>）。</summary>
    private const string CanceledReason = "Canceled";

    private const string UnexpectedMessage = "系統管理助理發生未預期的錯誤，請稍後再試。";

    /// <summary>串流中重新轉 HTML 的節流（同其他 AI 畫面）。</summary>
    private static readonly TimeSpan StreamRenderInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>空白對話時的範例問句。</summary>
    private static readonly IReadOnlyList<string> SamplePrompts =
        ["幫我巡一下系統", "最近排程的執行狀況", "昨晚備份為什麼失敗", "哪些帳號超過 180 天沒登入", "把日誌等級調成 Debug"];

    /// <summary>AI 回覆的 HTML（以紀錄物件為鍵，每則只轉一次）；null 表示內容異常龐大、不顯示。</summary>
    private readonly Dictionary<AiAgentTranscriptEntry, string?> htmlCache = [];

    private readonly List<AiAgentTranscriptEntry> entries = [];

    private bool isAccessChecked;
    private string? unavailableReason;
    private string displayName = string.Empty;
    private int retentionDays;

    /// <summary>目前這段對話（還沒送出第一則前為 null，由伺服器在 start 那一行給）。</summary>
    private Guid? conversationId;
    private string modelName = string.Empty;
    private IReadOnlyList<AiAgentConversationSummary> conversations = [];
    private string inputText = string.Empty;
    private bool isRunning;
    private bool isOpening;
    private bool hasUpdate;
    private bool stopRequested;
    private int elapsedSeconds;
    private string streamingHtml = string.Empty;
    private bool approvalVisible;
    private IReadOnlyList<AiToolApprovalRequest> pendingApprovals = [];

    /// <summary>這一輪「由誰回答」（分派完成時伺服器送來）；停止產生時補上的回覆用它標示。</summary>
    private string? currentLabel;

    /// <summary>核准窗的確認輸入（0.9.159 起，有不可復原的動作時才出現）；每次決定後清空。</summary>
    private string confirmInput = string.Empty;
    private bool renameVisible;
    private Guid renameTarget;
    private string renameTitle = string.Empty;
    private CancellationTokenSource? cts;
    private bool isDisposed;

    /// <summary>「巡檢報告」視窗（0.9.161 起）；只在管理員分支裡渲染，非管理員永遠打不開。</summary>
    private AdminPatrolReportsModal? patrolReportsModal;

    [Inject]
    public AdminAssistantApi Api { get; set; } = default!;

    [Inject]
    public PagePermissions Permissions { get; set; } = default!;

    [Inject]
    public NotificationService NotificationService { get; set; } = default!;

    [Inject]
    public ModalService ModalService { get; set; } = default!;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    [Inject]
    public ILogger<AdminAssistantView> Logger { get; set; } = default!;

    public string RoleMessage { get; set; } = string.Empty;

    /// <summary>定時巡檢通知的連結帶過來的報告編號（0.9.161 起）；有值時自動打開「巡檢報告」並選那一份。</summary>
    [Parameter]
    public Guid? InitialPatrolReportId { get; set; }

    /// <summary>網址帶過來的對話（重新整理前開著的那一段）。</summary>
    [Parameter]
    public Guid? InitialConversationId { get; set; }

    private bool IsAwaitingApproval => pendingApprovals.Count > 0;

    private bool IsBusy => isRunning || isOpening;

    /// <summary>執行中的狀態文字：還沒收到任何更新時是在分派，之後依有沒有文字分思考中／產生中。</summary>
    private string RunningText => hasUpdate == false ? "分派中" : streamingHtml.Length > 0 ? "產生中" : "思考中";

    protected override async Task OnInitializedAsync()
    {
        // 此頁為管理員專屬：權限鍵刻意未上架角色矩陣，以 IsAdmin 直接判斷。沒有權限不呼叫任何 API；
        // 開頁被拒的稽核由伺服器在開頁紀錄時寫（shell/page-view）。
        if (Permissions.IsAdmin == false)
        {
            RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
            isAccessChecked = true;
            Logger.LogWarning("Admin assistant denied because the current user is not an administrator.");
            return;
        }

        var info = await Api.InfoAsync();
        if (info.Success && info.Data is { } data)
        {
            displayName = data.DisplayName;
            unavailableReason = data.UnavailableReason;
            retentionDays = data.RetentionDays;
        }
        else
        {
            Logger.LogWarning("Admin assistant failed to load the page info. Message={Message}", info.UserMessage());
            displayName = "系統管理助理";
            unavailableReason = info.UserMessage();
        }

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

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // 深連結（同 AI 對話紀錄頁）：等畫面渲染完才開，視窗的 @ref 才有值；只開一次。
        if (InitialPatrolReportId is not { } reportId || patrolReportsModal is null)
        {
            return;
        }

        InitialPatrolReportId = null;
        if (await patrolReportsModal.OpenAsync(reportId) == false)
        {
            ViewNotification.Caution(NotificationService, "找不到這份巡檢報告，可能已超過保存期限被清除。");
        }
    }

    private async Task OnOpenPatrolReportsAsync()
    {
        if (patrolReportsModal is not null)
        {
            await patrolReportsModal.OpenAsync();
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
            Logger.LogWarning("Admin assistant failed to open a conversation. ConversationId={ConversationId}, Status={Status}", id, result.StatusCode);
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
        entries.AddRange(opened.Transcript.Select(AgentDtoMapper.ToModel));
        if (opened.PendingApprovals.Count > 0)
        {
            pendingApprovals = opened.PendingApprovals.Select(AgentDtoMapper.ToModel).ToList();
            confirmInput = string.Empty;
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
        var request = new AgentMessageRequestDto { ConversationId = conversationId, Message = text };
        await RunAsync(text, (onFrame, token) => Api.SendAsync(request, onFrame, token));
    }

    /// <summary>核准或拒絕所有待決定的工具，然後讓助理繼續（核准／拒絕的說明由伺服器記進紀錄，一起送回來）。</summary>
    private async Task OnDecideAsync(bool approve)
    {
        if (conversationId is not { } id || isRunning)
        {
            return;
        }

        // 按鈕停用之外再擋一次：不可復原的動作沒有輸入「確認」就不核准（伺服器也會擋）。
        if (approve && AdminAssistantApproval.CanApprove(pendingApprovals, confirmInput) == false)
        {
            return;
        }

        approvalVisible = false;
        var ids = pendingApprovals.Select(item => item.RequestId).ToList();
        var request = approve
            ? new AgentApprovalResponseRequestDto { ApprovedRequestIds = ids, ConfirmText = confirmInput }
            : new AgentApprovalResponseRequestDto { RejectedRequestIds = ids };
        pendingApprovals = [];
        confirmInput = string.Empty;

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
        hasUpdate = false;
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
        var awaitingApproval = false;
        var startedNow = false;
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
                        if (isNew)
                        {
                            SyncUrl();
                        }

                        StateHasChanged();
                        break;
                    case AgentStreamFrameKinds.Label:
                        currentLabel = frame.Label;
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
                    case AgentStreamFrameKinds.Tool:
                        hasUpdate = true;
                        if (frame.Tool is { Completed: false })
                        {
                            ClearStreaming(streamed);
                            StateHasChanged();
                        }

                        break;
                    case AgentStreamFrameKinds.Approvals when frame.Approvals is { } approvals:
                        hasUpdate = true;
                        ClearStreaming(streamed);
                        pendingApprovals = approvals.Select(AgentDtoMapper.ToModel).ToList();
                        confirmInput = string.Empty;
                        approvalVisible = true;
                        awaitingApproval = true;
                        break;
                    case AgentStreamFrameKinds.Result:
                        hasUpdate = true;
                        ClearStreaming(streamed);
                        result = frame.Result;
                        break;
                    case AgentStreamFrameKinds.Delta when string.IsNullOrEmpty(frame.Text) == false:
                        hasUpdate = true;
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
            Logger.LogError(ex, "Admin assistant run failed unexpectedly.");
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
            // 分派中就停止：伺服器把訊息與「已停止」一起記下。
            entries.Add(new AiAgentTranscriptEntry { Kind = AiAgentTranscriptKinds.User, Text = sentMessage });
        }

        if (streamedText.Length > 0)
        {
            entries.Add(new AiAgentTranscriptEntry { Kind = AiAgentTranscriptKinds.Assistant, Text = AiInterruption.Annotate(streamedText), Agent = currentLabel });
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
            Logger.LogWarning("Admin assistant failed to load conversations. Message={Message}", result.UserMessage());
            ViewNotification.Error(NotificationService, "無法讀取「我的對話」，請稍後再試。");
            return;
        }

        conversations = items.Select(item => AgentDtoMapper.ToModel(item, "AdminAssistant")).ToList();
    }

    private void ResetConversation()
    {
        conversationId = null;
        entries.Clear();
        modelName = string.Empty;
        currentLabel = null;
        inputText = string.Empty;
        pendingApprovals = [];
        approvalVisible = false;
        htmlCache.Clear();
    }

    /// <summary>
    /// 網址帶目前的對話（重新整理後回到同一段）；沒有對話時拿掉。巡檢報告的參數一併拿掉：已經開過了，
    /// 留著的話頁面重新給參數時會再開一次。
    /// </summary>
    private void SyncUrl()
    {
        var uri = NavigationManager.GetUriWithQueryParameters(new Dictionary<string, object?>
        {
            ["conversation"] = conversationId?.ToString(),
            ["patrolReport"] = null,
        });
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
