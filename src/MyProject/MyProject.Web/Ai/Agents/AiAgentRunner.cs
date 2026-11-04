using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Models.Systems;
using MyProject.Web.Configuration;

namespace MyProject.Web.Ai.Agents;

/// <summary>
/// 給畫面用的 Agent 執行器（0.9.142 起）：開一段對話，之後逐則送訊息、回覆工具核准。
/// 記帳、AI 對話紀錄、用量上限都在 agent 管線上自動處理；權限與稽核在工具外殼（<see cref="AiAgentToolFactory"/>）。
/// </summary>
public interface IAiAgentRunner
{
    /// <summary>AI 設定不完整時的中文原因（可直接當 Tooltip）；可用時為 null。</summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// 開始一段新對話。0.9.143 起每次執行結束（或停下來等核准）都會存起來，之後可用 <see cref="ResumeAsync"/> 接續；
    /// 第一次執行之前不會存（只按「新對話」不會多出空白對話）。呼叫前請先確認 <see cref="UnavailableReason"/> 為 null。
    /// </summary>
    Task<AiAgentChat> StartAsync(AiAgentDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// 接續目前使用者存下的一段對話（0.9.143 起）：AI 記得前文、畫面紀錄還原、停在核准的重新等使用者決定。
    /// 模型用<b>目前</b>的系統參數設定（可能與上次不同）。
    /// 不存在、不是本人的、不是這個 Agent 的、內容檔不見或讀不懂時回 null。
    /// </summary>
    Task<AiAgentChat?> ResumeAsync(AiAgentDefinition definition, Guid conversationId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAiAgentRunner" />
public sealed class AiAgentRunner : IAiAgentRunner
{
    private readonly IAiAgentFactory agentFactory;
    private readonly AiAgentToolFactory toolFactory;
    private readonly IAiToolPermissionGuard guard;
    private readonly IAiSystemPromptProvider promptProvider;
    private readonly IOptionsMonitor<AiSettings> settingsMonitor;
    private readonly IOptionsMonitor<AiModelSettings> modelMonitor;
    private readonly IAiAgentConversationStore store;
    private readonly ILogger<AiAgentRunner> logger;

    public AiAgentRunner(
        IAiAgentFactory agentFactory,
        AiAgentToolFactory toolFactory,
        IAiToolPermissionGuard guard,
        IAiSystemPromptProvider promptProvider,
        IOptionsMonitor<AiSettings> settingsMonitor,
        IOptionsMonitor<AiModelSettings> modelMonitor,
        IAiAgentConversationStore store,
        ILogger<AiAgentRunner> logger)
    {
        this.agentFactory = agentFactory;
        this.toolFactory = toolFactory;
        this.guard = guard;
        this.promptProvider = promptProvider;
        this.settingsMonitor = settingsMonitor;
        this.modelMonitor = modelMonitor;
        this.store = store;
        this.logger = logger;
    }

    public string? UnavailableReason => AiChatEndpoint.Validate(settingsMonitor.CurrentValue);

    public async Task<AiAgentChat> StartAsync(AiAgentDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var conversationId = Guid.NewGuid();
        var (handle, modelName) = await CreateAgentAsync(definition, conversationId, cancellationToken);
        var session = await handle.Agent.CreateSessionAsync(cancellationToken);

        logger.LogInformation(
            "AI agent conversation started. Agent={Agent}, ConversationId={ConversationId}, Model={Model}",
            definition.Name, conversationId, modelName);

        return new AiAgentChat(definition, conversationId, modelName, handle, session, guard, store, logger);
    }

    public async Task<AiAgentChat?> ResumeAsync(AiAgentDefinition definition, Guid conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var loaded = await store.LoadMineAsync(conversationId);
        if (loaded?.Content is not { } content || loaded.Summary.AgentName != definition.Name)
        {
            logger.LogWarning(
                "AI agent conversation cannot be resumed. Agent={Agent}, ConversationId={ConversationId}, Found={Found}",
                definition.Name, conversationId, loaded is not null);
            return null;
        }

        var (handle, modelName) = await CreateAgentAsync(definition, conversationId, cancellationToken);
        AgentSession session;
        try
        {
            session = await handle.Agent.DeserializeSessionAsync(content.Session, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // MAF 升版改了 session 格式這類情況：接不下去，但不要讓畫面當掉。
            logger.LogWarning(ex, "AI agent session cannot be restored. Agent={Agent}, ConversationId={ConversationId}", definition.Name, conversationId);
            return null;
        }

        logger.LogInformation(
            "AI agent conversation resumed. Agent={Agent}, ConversationId={ConversationId}, Model={Model}",
            definition.Name, conversationId, modelName);

        var chat = new AiAgentChat(definition, conversationId, modelName, handle, session, guard, store, logger, loaded.Summary.Title);
        chat.Restore(content);
        return chat;
    }

    /// <summary>建立 agent（工具、提示詞、記帳管線）。開新對話與接續共用；接續沿用原本的 ConversationId，AI 對話紀錄才串得起來。</summary>
    private async Task<(AiAgentHandle Handle, string ModelName)> CreateAgentAsync(
        AiAgentDefinition definition, Guid conversationId, CancellationToken cancellationToken)
    {
        var settings = settingsMonitor.CurrentValue;
        var timeoutSeconds = definition.TimeoutSeconds ?? (settings.TimeoutSeconds > 0 ? settings.TimeoutSeconds : 120);

        // 提示詞在開對話（或接續）時取一次（含固定附加的安全規則）；管理員在對話途中改版本，下一段新對話或下次接續才生效。
        var instructions = await promptProvider.GetAsync(definition.PromptTemplateKey, cancellationToken);

        var handle = agentFactory.CreateAgent(
            new AiCallContext
            {
                Operation = definition.Operation,
                Timeout = TimeSpan.FromSeconds(timeoutSeconds),
                RelatedInfo = definition.DisplayName,
                ConversationId = conversationId,
                ModelKey = definition.ModelKey,

                // Agent 一律走 Responses API（0.9.149 起；見 AiCallContext.UseResponsesApi）。
                UseResponsesApi = true,
                ContextLengthExceededMessage = "對話內容已超過模型的內容視窗上限，請按「新對話」重新開始。",
                TimeoutHint = "請稍後再試。",
                SubmittedContentLabel = "對話內容",
            },
            instructions,
            toolFactory.Create(definition));

        return (handle, modelMonitor.CurrentValue.Resolve(definition.ModelKey, settings.Model));
    }
}

/// <summary>
/// 一段 Agent 對話（0.9.142 起；0.9.143 起會存起來，可以接續）。
///
/// <para>
/// 每一次 <see cref="SendAsync"/> 或 <see cref="RespondAsync"/> 是<b>一次執行</b>：換一個新的執行編號（RunId），
/// 這次執行裡每一次模型呼叫（含工具呼叫迴圈）的 Token 用量與 AI 對話紀錄都帶同一個 RunId。
/// </para>
/// <para>
/// 模型要求執行「會異動資料」的工具時，這次執行會停下來，以 <see cref="AiAgentUpdate.Approvals"/> 回報，
/// 畫面請使用者核准或拒絕後呼叫 <see cref="RespondAsync"/> 繼續。使用者沒有權限的工具不會請他核准：
/// 直接以「沒有權限」拒絕並寫 <c>Ai.ToolDenied</c>，讓 agent 自己告訴使用者。
/// </para>
/// <para>
/// 畫面上的對話紀錄由這個物件維護（<see cref="Transcript"/>，畫面直接呈現它），
/// 每次執行結束、停下來等核准、失敗、取消或呼叫端中途放棄時都會連同 MAF session 存起來（<see cref="IAiAgentConversationStore"/>）。
/// </para>
/// <para>⚠️ 不是執行緒安全：同一段對話一次只能有一個執行（畫面在執行中停用送出鈕）。</para>
/// </summary>
public sealed class AiAgentChat
{
    private readonly AiAgentHandle handle;
    private readonly AgentSession session;
    private readonly IAiToolPermissionGuard guard;
    private readonly IAiAgentConversationStore store;
    private readonly ILogger logger;
    private readonly List<PendingApproval> pending = [];
    private readonly List<AiAgentTranscriptEntry> transcript = [];
    private string? title;

    internal AiAgentChat(
        AiAgentDefinition definition,
        Guid conversationId,
        string modelName,
        AiAgentHandle handle,
        AgentSession session,
        IAiToolPermissionGuard guard,
        IAiAgentConversationStore store,
        ILogger logger,
        string? title = null)
    {
        Definition = definition;
        ConversationId = conversationId;
        ModelName = modelName;
        this.handle = handle;
        this.session = session;
        this.guard = guard;
        this.store = store;
        this.logger = logger;
        this.title = title;
    }

    public AiAgentDefinition Definition { get; }

    /// <summary>對話識別碼，也是 AI 對話紀錄的 ConversationId（管理員可從 AI 對話紀錄頁查看整段）。</summary>
    public Guid ConversationId { get; }

    /// <summary>開對話（或接續）時解析出的模型（系統參數覆寫或預設模型）。</summary>
    public string ModelName { get; }

    /// <summary>畫面上的對話紀錄（依時間順序）。執行中只會在一則完整之後才加進來，串流中的文字請看 <see cref="AiAgentUpdate.TextDelta"/>。</summary>
    public IReadOnlyList<AiAgentTranscriptEntry> Transcript => transcript;

    /// <summary>等待使用者核准的工具呼叫；沒有時為空。</summary>
    public IReadOnlyList<AiToolApprovalRequest> PendingApprovals
        => pending.Where(item => item.Request is not null).Select(item => item.Request!).ToList();

    public bool IsAwaitingApproval => pending.Any(item => item.Request is not null);

    /// <summary>送出使用者訊息。有待核准的工具時不能送（先回覆核准）。</summary>
    /// <exception cref="InvalidOperationException">還有待核准的工具。</exception>
    public IAsyncEnumerable<AiAgentUpdate> SendAsync(string message, CancellationToken cancellationToken = default)
    {
        if (IsAwaitingApproval)
        {
            throw new InvalidOperationException("還有待核准的工具，請先核准或拒絕。");
        }

        title ??= AiAgentConversationService.TitleFrom(message);
        transcript.Add(Entry(AiAgentTranscriptKinds.User, message));

        // 接續時才可能發生：存下來的待核准全都不能再請使用者決定（工具已不在這個 Agent 上）。
        // 那些拒絕要先送回去，模型才不會一直等著那幾個工具呼叫的結果。
        var contents = new List<AIContent>(pending.Select(item => item.AutoResponse!)) { new TextContent(message) };
        pending.Clear();

        return PersistingRunAsync([new ChatMessage(ChatRole.User, contents)], cancellationToken);
    }

    /// <summary>
    /// 回覆待核准的工具並繼續執行。<paramref name="approvedRequestIds"/> 裡的核准、其餘一律視為拒絕（寫 <c>Ai.ToolRejected</c>）。
    /// 核准的工具真正執行時，工具外殼仍會再檢查一次權限（接續舊對話時，權限可能已經變了）。
    /// </summary>
    public async IAsyncEnumerable<AiAgentUpdate> RespondAsync(
        IReadOnlyCollection<string> approvedRequestIds,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approvedRequestIds);

        var responses = new List<AIContent>(pending.Count);
        var approvedNames = new List<string>();
        var rejectedNames = new List<string>();
        foreach (var item in pending)
        {
            // 因為沒有權限被自動拒絕的那幾個：照原本的拒絕送回去。
            if (item.AutoResponse is not null)
            {
                responses.Add(item.AutoResponse);
                continue;
            }

            var approved = approvedRequestIds.Contains(item.Request!.RequestId);
            if (approved == false)
            {
                await guard.AuditAsync(AuditActions.Ai.ToolRejected, Definition.Name, item.Tool!, success: false);
            }

            (approved ? approvedNames : rejectedNames).Add(item.Request.DisplayName);
            responses.Add(item.Content!.CreateResponse(approved, approved ? null : AiToolApprovals.RejectedReason));
        }

        pending.Clear();
        if (approvedNames.Count > 0)
        {
            transcript.Add(Entry(AiAgentTranscriptKinds.Notice, $"你核准了：{string.Join("、", approvedNames)}"));
        }

        if (rejectedNames.Count > 0)
        {
            transcript.Add(Entry(AiAgentTranscriptKinds.Notice, $"你拒絕了：{string.Join("、", rejectedNames)}"));
        }

        await foreach (var update in PersistingRunAsync([new ChatMessage(ChatRole.User, responses)], cancellationToken))
        {
            yield return update;
        }
    }

    /// <summary>接續時還原畫面紀錄與待核准（只在 <see cref="AiAgentRunner.ResumeAsync"/> 呼叫）。</summary>
    internal void Restore(AiAgentConversationContent content)
    {
        transcript.AddRange(content.Transcript);
        foreach (var record in content.PendingApprovals)
        {
            var restored = RestorePending(record);
            if (restored is not null)
            {
                pending.Add(restored);
            }
        }
    }

    /// <summary>
    /// 跑一次執行，結束時（不論成功、失敗、取消、停在核准，或呼叫端中途不再讀取）存檔。
    /// 存檔用 <see cref="CancellationToken.None"/>：使用者按停止或離開頁面時，正是最需要存下來的時候。
    /// </summary>
    private async IAsyncEnumerable<AiAgentUpdate> PersistingRunAsync(
        IList<ChatMessage> input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var update in RunAsync(input, cancellationToken))
            {
                yield return update;
            }
        }
        finally
        {
            await PersistAsync();
        }
    }

    private async IAsyncEnumerable<AiAgentUpdate> RunAsync(
        IList<ChatMessage> input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // 一次執行一個新的 RunId（見類別說明）。
        handle.State.RunId = Guid.NewGuid();
        var outcomesBefore = handle.State.Outcomes.Count;
        var stopwatch = Stopwatch.StartNew();
        var text = new StringBuilder();
        var approvals = new List<ToolApprovalRequestContent>();
        var callNames = new Dictionary<string, string>(StringComparer.Ordinal);
        AiAnalysisResult? failure = null;
        var finished = false;

        IAsyncEnumerator<AgentResponseUpdate>? enumerator = null;
        try
        {
            while (true)
            {
                // ⚠️ C# 不允許在有 catch 的 try 裡 yield，所以只把「取下一筆」包起來，yield 放在外面。
                AgentResponseUpdate update;
                try
                {
                    enumerator ??= handle.Agent
                        .RunStreamingAsync(input, session, cancellationToken: cancellationToken)
                        .GetAsyncEnumerator(cancellationToken);

                    if (await enumerator.MoveNextAsync() == false)
                    {
                        break;
                    }

                    update = enumerator.Current;
                }
                catch (AiCallException ex)
                {
                    // 已在記帳中介層記過帳、寫過日誌。
                    failure = ex.ToResult();
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    failure = AiAnalysisResult.Failure(AiAnalysisFailureReason.Canceled, "已取消本次執行。", elapsed: stopwatch.Elapsed) with
                    {
                        Markdown = text.ToString(),
                        IsInterrupted = text.Length > 0,
                    };
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "AI agent run failed unexpectedly. Agent={Agent}, ConversationId={ConversationId}", Definition.Name, ConversationId);
                    failure = AiAnalysisResult.Failure(
                        AiAnalysisFailureReason.Unexpected, $"AI 執行失敗：{ex.GetType().Name}。", elapsed: stopwatch.Elapsed, detail: ex.ToString());
                    break;
                }

                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case ToolApprovalRequestContent request:
                            approvals.Add(request);
                            break;
                        case FunctionCallContent call:
                            callNames[call.CallId] = call.Name;

                            // 工具前面若有文字（例如「我來查一下」），先收成一則回覆，順序才對。
                            FlushAssistant(text, interrupted: false);
                            transcript.Add(Entry(AiAgentTranscriptKinds.Tool, $"呼叫工具：{DisplayNameOf(call.Name)}"));
                            yield return new AiAgentUpdate { ToolActivity = new AiToolActivity(call.Name, DisplayNameOf(call.Name), Completed: false) };
                            break;
                        case FunctionResultContent result when callNames.TryGetValue(result.CallId, out var name):
                            yield return new AiAgentUpdate { ToolActivity = new AiToolActivity(name, DisplayNameOf(name), Completed: true) };
                            break;
                    }
                }

                if (string.IsNullOrEmpty(update.Text) == false)
                {
                    text.Append(update.Text);
                    yield return new AiAgentUpdate { TextDelta = update.Text };
                }
            }

            finished = true;
        }
        finally
        {
            if (enumerator is not null)
            {
                await enumerator.DisposeAsync();
            }

            // 呼叫端中途不再讀取（例如離開頁面）：已收到的文字照樣收進紀錄，標示不完整。
            if (finished == false && text.Length > 0)
            {
                FlushAssistant(text, interrupted: true);
            }
        }

        if (failure is not null)
        {
            if (failure.Reason == AiAnalysisFailureReason.Canceled)
            {
                var hadText = text.Length > 0;
                FlushAssistant(text, interrupted: hadText);
                transcript.Add(Entry(AiAgentTranscriptKinds.Notice, hadText ? "已停止產生（上面的回覆不完整）。" : "已停止產生，尚未收到任何內容。"));
            }
            else
            {
                FlushAssistant(text, interrupted: false);
                transcript.Add(Entry(AiAgentTranscriptKinds.Error, failure.ErrorMessage));
            }

            yield return new AiAgentUpdate { Result = failure };
            yield break;
        }

        // 收進紀錄會清掉 text，結果要用的全文先留下來。
        var markdown = text.ToString();
        FlushAssistant(text, interrupted: false);

        if (approvals.Count > 0)
        {
            var autoRejected = await TriageApprovalsAsync(approvals);

            // 全部都因為沒有權限被自動拒絕：不必打擾使用者，直接把拒絕送回去，讓 agent 自己說明。
            if (pending.Count == 0)
            {
                await foreach (var update in RunAsync([new ChatMessage(ChatRole.User, autoRejected)], cancellationToken))
                {
                    yield return update;
                }

                yield break;
            }

            // 有些要使用者決定：自動拒絕的那幾個先記著，跟使用者的決定一起送回去。
            foreach (var response in autoRejected.OfType<ToolApprovalResponseContent>())
            {
                pending.Add(PendingApproval.AutoRejected(response));
            }

            yield return new AiAgentUpdate { Approvals = PendingApprovals };
            yield break;
        }

        var outcomes = handle.State.Outcomes.Skip(outcomesBefore).ToList();
        yield return new AiAgentUpdate
        {
            Result = new AiAnalysisResult
            {
                Success = true,
                Markdown = markdown,
                Usage = AiTokenUsage.Sum(outcomes.Select(outcome => outcome.Usage)),
                ModelName = outcomes.LastOrDefault()?.ModelName ?? ModelName,
                IsTruncatedByLength = string.Equals(outcomes.LastOrDefault()?.FinishReason, "length", StringComparison.OrdinalIgnoreCase),
                Elapsed = stopwatch.Elapsed,
            },
        };
    }

    /// <summary>
    /// 先替使用者檢查權限：沒有權限（或不是這個 agent 的工具）的直接拒絕並寫 <c>Ai.ToolDenied</c>；
    /// 有權限的放進 <see cref="pending"/> 等使用者決定。回傳自動拒絕的回覆。
    /// </summary>
    private async Task<List<AIContent>> TriageApprovalsAsync(List<ToolApprovalRequestContent> approvals)
    {
        var (forUser, autoRejected) = await AiToolApprovals.TriageAsync(Definition, approvals, guard);
        foreach (var (content, tool, request) in forUser)
        {
            pending.Add(PendingApproval.ForUser(content, tool, request));
        }

        return autoRejected;
    }

    /// <summary>把累積的文字收成一則 AI 回覆（中途停止的加註已中止）。</summary>
    private void FlushAssistant(StringBuilder text, bool interrupted)
    {
        if (text.Length == 0)
        {
            return;
        }

        var markdown = text.ToString();
        transcript.Add(Entry(AiAgentTranscriptKinds.Assistant, interrupted ? AiInterruption.Annotate(markdown) : markdown));
        text.Clear();
    }

    /// <summary>存檔：session、畫面紀錄與待核准。還沒有任何紀錄（只開了新對話）時不存。失敗只記警告。</summary>
    private async Task PersistAsync()
    {
        if (transcript.Count == 0)
        {
            return;
        }

        try
        {
            var content = new AiAgentConversationContent
            {
                Session = await handle.Agent.SerializeSessionAsync(session, cancellationToken: CancellationToken.None),
                Transcript = transcript.ToList(),
                PendingApprovals = pending.Select(item => item.ToRecord()).ToList(),
            };

            await store.SaveAsync(new AiAgentConversationSnapshot(
                ConversationId, Definition.Name, title ?? Definition.DisplayName, IsAwaitingApproval, content));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist AI agent conversation. Agent={Agent}, ConversationId={ConversationId}", Definition.Name, ConversationId);
        }
    }

    /// <summary>
    /// 從存檔還原一筆待核准。工具已不在這個 Agent 上的改成自動拒絕；內容讀不懂的略過（記警告）。
    /// 權限不在這裡重新檢查：使用者核准後，工具外殼執行前一定會再檢查一次。
    /// </summary>
    private PendingApproval? RestorePending(AiAgentPendingApprovalRecord record)
    {
        AIContent? content;
        try
        {
            content = record.Content.Deserialize<AIContent>(AIJsonUtilities.DefaultOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to restore a pending tool approval. ConversationId={ConversationId}, Tool={Tool}", ConversationId, record.ToolName);
            return null;
        }

        switch (content)
        {
            case ToolApprovalResponseContent response:
                return PendingApproval.AutoRejected(response);
            case ToolApprovalRequestContent request when request.ToolCall is FunctionCallContent call
                && Definition.Tools.FirstOrDefault(item => item.Name == call.Name) is { } tool:
                return PendingApproval.ForUser(request, tool, AiToolApprovals.Describe(request, tool, call));
            case ToolApprovalRequestContent request:
                return PendingApproval.AutoRejected(request.CreateResponse(false, AiToolApprovals.UnknownToolReason));
            default:
                logger.LogWarning("Unexpected pending tool approval content. ConversationId={ConversationId}, Tool={Tool}", ConversationId, record.ToolName);
                return null;
        }
    }

    private static AiAgentTranscriptEntry Entry(string kind, string text) => new() { Kind = kind, Text = text };

    private string DisplayNameOf(string toolName)
        => Definition.Tools.FirstOrDefault(tool => tool.Name == toolName)?.DisplayName ?? toolName;

    /// <summary>一筆待決定的核准。自動拒絕的也放這裡（<see cref="Request"/> 為 null 的那種不給畫面看）。</summary>
    private sealed class PendingApproval
    {
        private PendingApproval(ToolApprovalRequestContent content, AiToolDefinition tool, AiToolApprovalRequest request)
        {
            Content = content;
            Tool = tool;
            Request = request;
        }

        private PendingApproval(ToolApprovalResponseContent autoResponse)
        {
            AutoResponse = autoResponse;
        }

        public ToolApprovalRequestContent? Content { get; }

        public AiToolDefinition? Tool { get; }

        public AiToolApprovalRequest? Request { get; }

        public ToolApprovalResponseContent? AutoResponse { get; }

        public static PendingApproval ForUser(ToolApprovalRequestContent content, AiToolDefinition tool, AiToolApprovalRequest request)
            => new(content, tool, request);

        public static PendingApproval AutoRejected(ToolApprovalResponseContent response) => new(response);

        /// <summary>存檔用：MAF 的 AIContent 以 <see cref="AIJsonUtilities.DefaultOptions"/> 序列化（含型別標記，讀得回同一個型別）。</summary>
        public AiAgentPendingApprovalRecord ToRecord() => new()
        {
            ToolName = Tool?.Name ?? (AutoResponse?.ToolCall as FunctionCallContent)?.Name ?? string.Empty,
            AutoRejected = AutoResponse is not null,
            Content = JsonSerializer.SerializeToElement<AIContent>(
                AutoResponse is not null ? AutoResponse : Content!, AIJsonUtilities.DefaultOptions),
        };
    }
}

/// <summary>Agent 執行的一筆更新：文字片段、工具活動、待核准清單或最後結果，一次只有一種。</summary>
public sealed record AiAgentUpdate
{
    public string TextDelta { get; init; } = string.Empty;

    public AiToolActivity? ToolActivity { get; init; }

    /// <summary>這次執行停下來等使用者核准（之後不會再有 <see cref="Result"/>，請呼叫 <see cref="AiAgentChat.RespondAsync"/>）。</summary>
    public IReadOnlyList<AiToolApprovalRequest>? Approvals { get; init; }

    /// <summary>這次執行結束（成功、失敗或取消）。</summary>
    public AiAnalysisResult? Result { get; init; }
}

/// <summary>工具開始或結束執行（畫面顯示「正在搜尋專案…」用）。</summary>
public sealed record AiToolActivity(string ToolName, string DisplayName, bool Completed);

/// <summary>一筆待核准的工具呼叫。</summary>
public sealed record AiToolApprovalRequest(
    string RequestId,
    string ToolName,
    string DisplayName,
    IReadOnlyList<KeyValuePair<string, string>> Arguments);
