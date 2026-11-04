using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Options;
using MyProject.Business.Services.DataAccess;
using MyProject.Models.Systems;
using MyProject.Web.Ai.Agents;
using MyProject.Web.Configuration;

namespace MyProject.Web.Ai.Workflows;

/// <summary>
/// 多 Agent 工作流程的執行器（0.9.144 起）：每次執行重新建立 agent 與步驟、接上人工介入通道，交給 MAF Workflows 執行。
///
/// <para>
/// 每個 agent 都經 <see cref="IAiAgentFactory"/> 建立，所以每一次模型呼叫照常記帳；一次執行裡所有 agent 共用
/// 同一個 ConversationId 與 RunId，作業名稱是流程名稱，「關聯說明」是「流程／agent」。
/// </para>
/// </summary>
public interface IAiWorkflowRunner
{
    /// <summary>AI 設定不完整時的中文原因；可用時為 null。</summary>
    string? UnavailableReason { get; }

    /// <summary>準備一次執行（建立 agent、取提示詞）。之後呼叫 <see cref="AiWorkflowRun.RunAsync"/> 開始。</summary>
    Task<AiWorkflowRun> StartAsync(AiWorkflowDefinition definition, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAiWorkflowRunner" />
public sealed class AiWorkflowRunner : IAiWorkflowRunner
{
    private readonly IAiAgentFactory agentFactory;
    private readonly AiAgentToolFactory toolFactory;
    private readonly IAiToolPermissionGuard guard;
    private readonly IAiSystemPromptProvider promptProvider;
    private readonly IOptionsMonitor<AiSettings> settingsMonitor;
    private readonly IOptionsMonitor<AiModelSettings> modelMonitor;
    private readonly IAiAgentConversationStore store;
    private readonly ILogger<AiWorkflowRunner> logger;

    public AiWorkflowRunner(
        IAiAgentFactory agentFactory,
        AiAgentToolFactory toolFactory,
        IAiToolPermissionGuard guard,
        IAiSystemPromptProvider promptProvider,
        IOptionsMonitor<AiSettings> settingsMonitor,
        IOptionsMonitor<AiModelSettings> modelMonitor,
        IAiAgentConversationStore store,
        ILogger<AiWorkflowRunner> logger)
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

    public async Task<AiWorkflowRun> StartAsync(AiWorkflowDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var settings = settingsMonitor.CurrentValue;
        var conversationId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var cancellation = new CancellationTokenSource();
        var handles = new List<AiAgentHandle>();
        var steps = new Dictionary<string, AiAgentStep>(StringComparer.Ordinal);

        foreach (var spec in definition.Steps)
        {
            var agent = spec.Agent;
            var timeoutSeconds = agent.TimeoutSeconds ?? (settings.TimeoutSeconds > 0 ? settings.TimeoutSeconds : 120);

            // 提示詞在開始時取一次（含固定附加的安全規則）；執行途中管理員改版本，下一次執行才生效。
            var instructions = await promptProvider.GetAsync(agent.PromptTemplateKey, cancellationToken);
            var tools = toolFactory.Create(agent);
            if (spec.CanAskUser)
            {
                tools.Add(AiAgentStep.AskUserDeclaration);
            }

            var handle = agentFactory.CreateAgent(
                new AiCallContext
                {
                    Operation = definition.Operation,
                    Timeout = TimeSpan.FromSeconds(timeoutSeconds),
                    RelatedInfo = $"{definition.DisplayName}／{agent.DisplayName}",
                    ConversationId = conversationId,
                    RunId = runId,
                    ModelKey = definition.ModelKey,

                    // 流程裡每個 agent（含不帶工具的步驟）都走 Responses API，同一個流程不混用兩種 API（0.9.149 起）。
                    UseResponsesApi = true,
                    ContextLengthExceededMessage = "內容已超過模型的內容視窗上限，請縮短輸入後重新執行。",
                    TimeoutHint = "請稍後再試。",
                    SubmittedContentLabel = "工作流程內容",
                },
                instructions,
                tools);
            handle.State.RunId = runId;
            handles.Add(handle);
            steps[agent.Name] = new AiAgentStep(spec, handle, guard, cancellation.Token);
        }

        var graph = new AiWorkflowGraph(steps);
        definition.Wire(graph);
        foreach (var step in graph.HumanSteps)
        {
            ExecutorBinding port = RequestPort.Create<AiWorkflowHumanRequest, AiWorkflowHumanResponse>(AiWorkflowGraph.HumanPortId(step.Id));
            graph.Builder.AddEdge(step, port).AddEdge(port, step);
        }

        var workflow = graph.Builder
            .WithOutputFrom(graph.HumanSteps.Select(step => (ExecutorBinding)step).ToArray())
            .WithName(definition.Name)
            .Build();

        var modelName = modelMonitor.CurrentValue.Resolve(definition.ModelKey, settings.Model);
        logger.LogInformation(
            "AI workflow prepared. Workflow={Workflow}, ConversationId={ConversationId}, RunId={RunId}, Model={Model}",
            definition.Name, conversationId, runId, modelName);

        return new AiWorkflowRun(definition, conversationId, modelName, workflow, handles, cancellation, store, logger);
    }
}

/// <summary>
/// 一次工作流程執行（0.9.144 起）。<see cref="RunAsync"/> 開始，停下來等使用者時以 <see cref="AiWorkflowUpdate.Request"/> 回報，
/// 畫面呼叫 <see cref="RespondAsync"/> 繼續；最後一筆是 <see cref="AiWorkflowUpdate.Result"/>。
///
/// <para>
/// 執行紀錄（<see cref="Transcript"/>）由這個物件維護，每次停下來等使用者與結束時都存起來（與 Agent 對話共用 <see cref="IAiAgentConversationStore"/>，
/// <c>AgentName</c> 是流程名稱），只能回顧、不能接續：沒有跨重開機的檢查點，離開頁面（<see cref="DisposeAsync"/>）就算中斷。
/// </para>
/// <para>⚠️ 不是執行緒安全：同一次執行一次只能有一個 <see cref="RunAsync"/> 或 <see cref="RespondAsync"/> 在讀取。</para>
/// </summary>
public sealed class AiWorkflowRun : IAsyncDisposable
{
    private static readonly JsonElement NoSession = JsonDocument.Parse("null").RootElement.Clone();

    private readonly Workflow workflow;
    private readonly IReadOnlyList<AiAgentHandle> handles;
    private readonly CancellationTokenSource cancellation;
    private readonly IAiAgentConversationStore store;
    private readonly ILogger logger;
    private readonly List<AiAgentTranscriptEntry> transcript = [];
    private readonly List<(AiWorkflowPendingRequest Pending, ExternalRequest Request)> pending = [];
    private readonly Dictionary<string, StringBuilder> buffers = new(StringComparer.Ordinal);
    private readonly Stopwatch stopwatch = new();
    private StreamingRun? streamingRun;
    private string title = string.Empty;
    private bool ended;
    private bool pumping;
    private bool disposed;

    internal AiWorkflowRun(
        AiWorkflowDefinition definition,
        Guid conversationId,
        string modelName,
        Workflow workflow,
        IReadOnlyList<AiAgentHandle> handles,
        CancellationTokenSource cancellation,
        IAiAgentConversationStore store,
        ILogger logger)
    {
        Definition = definition;
        ConversationId = conversationId;
        ModelName = modelName;
        this.workflow = workflow;
        this.handles = handles;
        this.cancellation = cancellation;
        this.store = store;
        this.logger = logger;
    }

    public AiWorkflowDefinition Definition { get; }

    /// <summary>AI 對話紀錄的 ConversationId，也是「我的執行紀錄」的識別碼。</summary>
    public Guid ConversationId { get; }

    public string ModelName { get; }

    /// <summary>執行紀錄（依時間順序）。串流中的文字請看 <see cref="AiWorkflowUpdate.TextDelta"/>，agent 完成後才收進來。</summary>
    public IReadOnlyList<AiAgentTranscriptEntry> Transcript => transcript;

    /// <summary>等使用者回覆的請求；沒有時為空。</summary>
    public IReadOnlyList<AiWorkflowPendingRequest> PendingRequests => pending.Select(item => item.Pending).ToList();

    /// <summary>結束後的結果；還在執行或等使用者時為 null。</summary>
    public AiWorkflowResult? Result { get; private set; }

    /// <summary>開始執行。一次執行只能呼叫一次。</summary>
    /// <exception cref="InvalidOperationException">已經開始過。</exception>
    public IAsyncEnumerable<AiWorkflowUpdate> RunAsync(string input, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        if (streamingRun is not null || transcript.Count > 0)
        {
            throw new InvalidOperationException("這次執行已經開始過，請重新開始一次新的執行。");
        }

        title = AiAgentConversationService.TitleFrom(input);
        transcript.Add(Entry(AiAgentTranscriptKinds.User, input));
        stopwatch.Start();
        logger.LogInformation("AI workflow started. Workflow={Workflow}, ConversationId={ConversationId}", Definition.Name, ConversationId);

        return PumpAsync(
            async () => streamingRun = await InProcessExecution.OffThread.RunStreamingAsync(
                workflow, new AiWorkflowMessage(input), ConversationId.ToString("N"), cancellation.Token),
            cancellationToken);
    }

    /// <summary>
    /// 回覆一筆等待中的請求並繼續執行。工具核准：清單裡的核准、其餘拒絕（寫 <c>Ai.ToolRejected</c>）；
    /// 提問：回答不可空白；審閱：退回要附意見，且還有退回次數。
    /// </summary>
    /// <exception cref="InvalidOperationException">找不到這筆請求，或回覆不符合規則（畫面應先擋下）。</exception>
    public IAsyncEnumerable<AiWorkflowUpdate> RespondAsync(string requestId, AiWorkflowHumanResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        var index = pending.FindIndex(item => item.Pending.RequestId == requestId);
        if (index < 0 || streamingRun is null || Result is not null)
        {
            throw new InvalidOperationException("找不到等待回覆的請求。");
        }

        var (item, request) = pending[index];
        Record(item.Request, response);
        pending.RemoveAt(index);

        return PumpAsync(() => streamingRun.SendResponseAsync(request.CreateResponse(response)).AsTask(), cancellationToken);
    }

    /// <summary>離開頁面：還沒結束就取消並存成「中斷」。</summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (Result is null && transcript.Count > 0)
        {
            await cancellation.CancelAsync();

            // 正在讀取的那一輪會在自己的 finally 收尾；沒有在讀（停在等使用者）的由這裡收尾。
            if (pumping == false)
            {
                Finish(failure: null);
                await PersistAsync();
                await CloseEngineAsync();
            }
        }

        // ⚠️ 刻意不 Dispose cancellation：正在讀取的那一輪可能還在收尾，步驟裡的模型呼叫也還握著它的權杖。
        // 它沒有計時器，不 Dispose 不會留下資源。
    }

    /// <summary>
    /// 讀取一輪工作流程事件，直到停下來等使用者或結束。每一輪結束都存檔（用 <see cref="CancellationToken.None"/>）。
    /// 呼叫端的 <paramref name="cancellationToken"/>（畫面的「停止」）會取消整個工作流程。
    /// </summary>
    private async IAsyncEnumerable<AiWorkflowUpdate> PumpAsync(
        Func<Task> kick,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(cancellation.Cancel);
        pumping = true;
        Exception? failure = null;
        IAsyncEnumerator<WorkflowEvent>? events = null;
        try
        {
            try
            {
                await kick();
                events = streamingRun!.WatchStreamAsync(blockOnPendingRequest: false, cancellation.Token).GetAsyncEnumerator(cancellation.Token);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            while (events is not null && failure is null)
            {
                // ⚠️ C# 不允許在有 catch 的 try 裡 yield，所以只把「取下一筆」包起來。
                WorkflowEvent current;
                try
                {
                    if (await events.MoveNextAsync() == false)
                    {
                        break;
                    }

                    current = events.Current;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    break;
                }

                if (current is ExecutorFailedEvent or WorkflowErrorEvent)
                {
                    failure = current.Data as Exception ?? new InvalidOperationException(current.Data?.ToString());
                    break;
                }

                if (Apply(current) is { } update)
                {
                    yield return update;
                }
            }

            if (failure is null && cancellation.IsCancellationRequested == false
                && pending.Count > 0 && await streamingRun!.GetStatusAsync() == RunStatus.PendingRequests)
            {
                yield break;
            }

            Finish(failure);
            yield return new AiWorkflowUpdate { Result = Result };
        }
        finally
        {
            if (events is not null)
            {
                await events.DisposeAsync();
            }

            pumping = false;

            // 呼叫端中途不再讀取（例如離開頁面）也要收尾。
            if (Result is null && (disposed || cancellation.IsCancellationRequested))
            {
                Finish(failure);
            }

            await PersistAsync();
            if (Result is not null)
            {
                await CloseEngineAsync();
            }
        }
    }

    /// <summary>把一筆工作流程事件收進紀錄，回傳要給畫面的更新（不需要的回 null）。</summary>
    private AiWorkflowUpdate? Apply(WorkflowEvent workflowEvent)
    {
        switch (workflowEvent)
        {
            case AiWorkflowStepEvent { Update: var update }:
                var agent = update.Agent ?? string.Empty;
                if (update.TextDelta.Length > 0)
                {
                    BufferOf(agent).Append(update.TextDelta);
                }
                else if (update.ToolActivity is { Completed: false } activity)
                {
                    // 工具前面若有文字（例如「我來查一下」），先收成一則，順序才對。
                    Flush(agent, interrupted: false);
                    transcript.Add(Entry(AiAgentTranscriptKinds.Tool, $"呼叫工具：{activity.DisplayName}", agent));
                }
                else if (update.AgentCompleted)
                {
                    Flush(agent, interrupted: false);
                }

                return update;

            case RequestInfoEvent info when info.Request.TryGetDataAs<AiWorkflowHumanRequest>(out var request):
                Flush(request.Agent, interrupted: false);
                if (request.Kind == AiWorkflowRequestKind.Question)
                {
                    transcript.Add(Entry(AiAgentTranscriptKinds.Notice, $"提問：{request.Question}", request.Agent));
                }

                var pendingRequest = new AiWorkflowPendingRequest(info.Request.RequestId, request);
                pending.Add((pendingRequest, info.Request));
                return new AiWorkflowUpdate { Agent = request.Agent, Request = pendingRequest };

            case WorkflowOutputEvent { Data: AiWorkflowOutput output }:
                transcript.Add(Entry(AiAgentTranscriptKinds.Output, output.Markdown, output.Title));
                return new AiWorkflowUpdate { Output = output };

            default:
                return null;
        }
    }

    /// <summary>把使用者的回覆記進紀錄，並檢查是否符合規則。</summary>
    private void Record(AiWorkflowHumanRequest request, AiWorkflowHumanResponse response)
    {
        switch (request.Kind)
        {
            case AiWorkflowRequestKind.ToolApproval:
                var approved = request.Approvals.Where(item => response.ApprovedRequestIds.Contains(item.RequestId)).Select(item => item.DisplayName).ToList();
                var rejected = request.Approvals.Where(item => response.ApprovedRequestIds.Contains(item.RequestId) == false).Select(item => item.DisplayName).ToList();
                if (approved.Count > 0)
                {
                    transcript.Add(Entry(AiAgentTranscriptKinds.Notice, $"你核准了：{string.Join("、", approved)}", request.Agent));
                }

                if (rejected.Count > 0)
                {
                    transcript.Add(Entry(AiAgentTranscriptKinds.Notice, $"你拒絕了：{string.Join("、", rejected)}", request.Agent));
                }

                break;

            case AiWorkflowRequestKind.Question:
                if (string.IsNullOrWhiteSpace(response.Answer))
                {
                    throw new InvalidOperationException("請輸入回答。");
                }

                transcript.Add(Entry(AiAgentTranscriptKinds.User, response.Answer.Trim(), request.Agent));
                break;

            case AiWorkflowRequestKind.Review:
                switch (response.Decision)
                {
                    case AiReviewDecision.Approve:
                        transcript.Add(Entry(AiAgentTranscriptKinds.Notice, "你通過了這份草稿。", request.Agent));
                        break;
                    case AiReviewDecision.Reject:
                        if (request.RejectionsLeft <= 0)
                        {
                            throw new InvalidOperationException("已經沒有退回次數，只能通過或結束。");
                        }

                        if (string.IsNullOrWhiteSpace(response.Comment))
                        {
                            throw new InvalidOperationException("退回時請寫下意見。");
                        }

                        transcript.Add(Entry(AiAgentTranscriptKinds.Notice, $"你退回了草稿：{response.Comment.Trim()}", request.Agent));
                        break;
                    default:
                        ended = true;
                        transcript.Add(Entry(AiAgentTranscriptKinds.Notice, "你結束了流程，報告沒有定稿。", request.Agent));
                        break;
                }

                break;
        }
    }

    /// <summary>決定結果並收尾紀錄（只做一次）。</summary>
    private void Finish(Exception? failure)
    {
        if (Result is not null)
        {
            return;
        }

        stopwatch.Stop();
        AiWorkflowOutcome outcome;
        string? error = null;
        if (disposed)
        {
            outcome = AiWorkflowOutcome.Interrupted;
            FlushAll(interrupted: true);
            transcript.Add(Entry(AiAgentTranscriptKinds.Notice, "執行中斷（離開頁面），工作流程無法從中途接續。"));
        }
        else if (cancellation.IsCancellationRequested)
        {
            outcome = AiWorkflowOutcome.Canceled;
            FlushAll(interrupted: true);
            transcript.Add(Entry(AiAgentTranscriptKinds.Notice, "已停止工作流程。"));
        }
        else if (failure is not null)
        {
            outcome = AiWorkflowOutcome.Failed;
            FlushAll(interrupted: true);
            if (Find<AiCallException>(failure) is { } callException)
            {
                // 已在記帳中介層記過帳、寫過日誌。
                error = callException.Message;
            }
            else
            {
                logger.LogError(failure, "AI workflow failed unexpectedly. Workflow={Workflow}, ConversationId={ConversationId}", Definition.Name, ConversationId);
                error = $"工作流程執行失敗：{(failure.GetBaseException()).GetType().Name}。";
            }

            transcript.Add(Entry(AiAgentTranscriptKinds.Error, error));
        }
        else
        {
            outcome = ended ? AiWorkflowOutcome.Ended : AiWorkflowOutcome.Completed;
            FlushAll(interrupted: false);
        }

        pending.Clear();
        var outcomes = handles.SelectMany(handle => handle.State.Outcomes).ToList();
        Result = new AiWorkflowResult(outcome, error, outcomes.Count, AiTokenUsage.Sum(outcomes.Select(item => item.Usage)), stopwatch.Elapsed);
        logger.LogInformation(
            "AI workflow finished. Workflow={Workflow}, ConversationId={ConversationId}, Outcome={Outcome}, ModelCalls={ModelCalls}",
            Definition.Name, ConversationId, outcome, outcomes.Count);
    }

    /// <summary>存檔。失敗只記警告，不影響執行。</summary>
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
                Session = NoSession,
                Transcript = transcript.ToList(),
            };

            await store.SaveAsync(new AiAgentConversationSnapshot(ConversationId, Definition.Name, title, HasPendingApproval: false, content));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist AI workflow run. Workflow={Workflow}, ConversationId={ConversationId}", Definition.Name, ConversationId);
        }
    }

    private async Task CloseEngineAsync()
    {
        if (streamingRun is null)
        {
            return;
        }

        try
        {
            await streamingRun.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Ignoring failure while disposing workflow run. ConversationId={ConversationId}", ConversationId);
        }

        streamingRun = null;
    }

    private StringBuilder BufferOf(string agent)
    {
        if (buffers.TryGetValue(agent, out var buffer) == false)
        {
            buffer = new StringBuilder();
            buffers[agent] = buffer;
        }

        return buffer;
    }

    private void Flush(string agent, bool interrupted)
    {
        if (buffers.TryGetValue(agent, out var buffer) == false || buffer.Length == 0)
        {
            return;
        }

        var markdown = buffer.ToString();
        transcript.Add(Entry(AiAgentTranscriptKinds.Assistant, interrupted ? AiInterruption.Annotate(markdown) : markdown, agent));
        buffer.Clear();
    }

    private void FlushAll(bool interrupted)
    {
        foreach (var agent in buffers.Keys.ToList())
        {
            Flush(agent, interrupted);
        }
    }

    private static T? Find<T>(Exception exception)
        where T : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static AiAgentTranscriptEntry Entry(string kind, string text, string? agent = null)
        => new() { Kind = kind, Text = text, Agent = agent };
}
