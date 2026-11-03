using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using MyProject.Business.Helpers;
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
    /// 開始一段新對話。0.9.142 起對話只存在記憶體（這個物件），關掉頁面就沒了；保存與接續在下一個版本。
    /// 呼叫前請先確認 <see cref="UnavailableReason"/> 為 null。
    /// </summary>
    Task<AiAgentChat> StartAsync(AiAgentDefinition definition, CancellationToken cancellationToken = default);
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
    private readonly ILogger<AiAgentRunner> logger;

    public AiAgentRunner(
        IAiAgentFactory agentFactory,
        AiAgentToolFactory toolFactory,
        IAiToolPermissionGuard guard,
        IAiSystemPromptProvider promptProvider,
        IOptionsMonitor<AiSettings> settingsMonitor,
        IOptionsMonitor<AiModelSettings> modelMonitor,
        ILogger<AiAgentRunner> logger)
    {
        this.agentFactory = agentFactory;
        this.toolFactory = toolFactory;
        this.guard = guard;
        this.promptProvider = promptProvider;
        this.settingsMonitor = settingsMonitor;
        this.modelMonitor = modelMonitor;
        this.logger = logger;
    }

    public string? UnavailableReason => AiChatEndpoint.Validate(settingsMonitor.CurrentValue);

    public async Task<AiAgentChat> StartAsync(AiAgentDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var settings = settingsMonitor.CurrentValue;
        var conversationId = Guid.NewGuid();
        var timeoutSeconds = definition.TimeoutSeconds ?? (settings.TimeoutSeconds > 0 ? settings.TimeoutSeconds : 120);

        // 提示詞在開對話時取一次（含固定附加的安全規則）；管理員在對話途中改版本，下一段新對話才生效。
        var instructions = await promptProvider.GetAsync(definition.PromptTemplateKey, cancellationToken);

        var handle = agentFactory.CreateAgent(
            new AiCallContext
            {
                Operation = definition.Operation,
                Timeout = TimeSpan.FromSeconds(timeoutSeconds),
                RelatedInfo = definition.DisplayName,
                ConversationId = conversationId,
                ModelKey = definition.ModelKey,
                ContextLengthExceededMessage = "對話內容已超過模型的內容視窗上限，請按「新對話」重新開始。",
                TimeoutHint = "請稍後再試。",
                SubmittedContentLabel = "對話內容",
            },
            instructions,
            toolFactory.Create(definition));

        var session = await handle.Agent.CreateSessionAsync(cancellationToken);
        var modelName = modelMonitor.CurrentValue.Resolve(definition.ModelKey, settings.Model);

        logger.LogInformation(
            "AI agent conversation started. Agent={Agent}, ConversationId={ConversationId}, Model={Model}",
            definition.Name, conversationId, modelName);

        return new AiAgentChat(definition, conversationId, modelName, handle, session, guard, logger);
    }
}

/// <summary>
/// 一段 Agent 對話（0.9.142 起只存在記憶體）。
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
/// <para>⚠️ 不是執行緒安全：同一段對話一次只能有一個執行（畫面在執行中停用送出鈕）。</para>
/// </summary>
public sealed class AiAgentChat
{
    private const string NoPermissionReason = "使用者沒有執行這個操作的權限";
    private const string RejectedReason = "使用者拒絕執行";

    private readonly AiAgentHandle handle;
    private readonly AgentSession session;
    private readonly IAiToolPermissionGuard guard;
    private readonly ILogger logger;
    private readonly List<PendingApproval> pending = [];

    internal AiAgentChat(
        AiAgentDefinition definition,
        Guid conversationId,
        string modelName,
        AiAgentHandle handle,
        AgentSession session,
        IAiToolPermissionGuard guard,
        ILogger logger)
    {
        Definition = definition;
        ConversationId = conversationId;
        ModelName = modelName;
        this.handle = handle;
        this.session = session;
        this.guard = guard;
        this.logger = logger;
    }

    public AiAgentDefinition Definition { get; }

    /// <summary>對話識別碼，也是 AI 對話紀錄的 ConversationId（管理員可從 AI 對話紀錄頁查看整段）。</summary>
    public Guid ConversationId { get; }

    /// <summary>開對話時解析出的模型（系統參數覆寫或預設模型）。</summary>
    public string ModelName { get; }

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

        return RunAsync([new ChatMessage(ChatRole.User, message)], cancellationToken);
    }

    /// <summary>
    /// 回覆待核准的工具並繼續執行。<paramref name="approvedRequestIds"/> 裡的核准、其餘一律視為拒絕（寫 <c>Ai.ToolRejected</c>）。
    /// </summary>
    public async IAsyncEnumerable<AiAgentUpdate> RespondAsync(
        IReadOnlyCollection<string> approvedRequestIds,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approvedRequestIds);

        var responses = new List<AIContent>(pending.Count);
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

            responses.Add(item.Content!.CreateResponse(approved, approved ? null : RejectedReason));
        }

        pending.Clear();

        await foreach (var update in RunAsync([new ChatMessage(ChatRole.User, responses)], cancellationToken))
        {
            yield return update;
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
        }
        finally
        {
            if (enumerator is not null)
            {
                await enumerator.DisposeAsync();
            }
        }

        if (failure is not null)
        {
            yield return new AiAgentUpdate { Result = failure };
            yield break;
        }

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
                Markdown = text.ToString(),
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
        var autoRejected = new List<AIContent>();
        foreach (var request in approvals)
        {
            var call = request.ToolCall as FunctionCallContent;
            var tool = Definition.Tools.FirstOrDefault(item => item.Name == call?.Name);
            if (call is null || tool is null)
            {
                autoRejected.Add(request.CreateResponse(false, "這不是這個 Agent 可以使用的工具"));
                continue;
            }

            if (await guard.CanInvokeAsync(tool) == false)
            {
                await guard.AuditAsync(AuditActions.Ai.ToolDenied, Definition.Name, tool, success: false, note: "stage=approval");
                autoRejected.Add(request.CreateResponse(false, NoPermissionReason));
                continue;
            }

            pending.Add(new PendingApproval(
                request,
                tool,
                new AiToolApprovalRequest(request.RequestId, tool.Name, tool.DisplayName, DescribeArguments(call))));
        }

        return autoRejected;
    }

    private string DisplayNameOf(string toolName)
        => Definition.Tools.FirstOrDefault(tool => tool.Name == toolName)?.DisplayName ?? toolName;

    /// <summary>核准窗顯示的參數：名稱＝值，值一律轉成文字（太長截斷）。</summary>
    private static IReadOnlyList<KeyValuePair<string, string>> DescribeArguments(FunctionCallContent call)
        => (call.Arguments ?? new Dictionary<string, object?>())
            .Select(argument => new KeyValuePair<string, string>(argument.Key, Describe(argument.Value)))
            .ToList();

    private static string Describe(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
            JsonElement element => element.GetRawText(),
            _ => value.ToString() ?? string.Empty,
        };

        return text.Length <= 2000 ? text : string.Concat(text.AsSpan(0, 2000), "…");
    }

    /// <summary>一筆待決定的核准。自動拒絕的也放這裡（<see cref="Request"/> 為 null 的那種不給畫面看）。</summary>
    private sealed class PendingApproval
    {
        public PendingApproval(ToolApprovalRequestContent content, AiToolDefinition tool, AiToolApprovalRequest request)
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

        public static PendingApproval AutoRejected(ToolApprovalResponseContent response) => new(response);
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
