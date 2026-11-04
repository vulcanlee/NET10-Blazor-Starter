using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using MyProject.Business.Helpers;
using MyProject.Web.Ai.Agents;

namespace MyProject.Web.Ai.Workflows;

/// <summary>
/// 工作流程裡的一個 agent 步驟（0.9.144 起）。
///
/// <para>
/// 刻意不用 MAF 內建的 agent executor（<c>AIAgent.BindAsExecutor</c>）：它會把前一個 agent 的整段對話（含工具呼叫）
/// 轉給下一個，並行分支收齊時也會讓下游 agent 跑好幾次。這裡自己跑 agent，步驟之間只傳文字（<see cref="AiWorkflowMessage"/>），
/// 工具核准的規則與 Agent 測試台相同（<see cref="AiToolApprovals"/>）。
/// </para>
/// <para>
/// 要等使用者時送出 <see cref="AiWorkflowHumanRequest"/>（經執行器接上的 <see cref="RequestPort"/>），
/// 使用者回覆後以 <see cref="AiWorkflowHumanResponse"/> 回到這個步驟，用同一個 MAF session 接著跑。
/// 每次執行都建新的步驟（有狀態），不跨執行重用。
/// </para>
/// </summary>
public sealed class AiAgentStep : Executor
{
    /// <summary>向使用者提問的工具名稱。只有宣告、沒有實作：模型呼叫它時工具呼叫迴圈會停下來，由這個步驟轉成提問。</summary>
    public const string AskUserToolName = "ask_user";

    private readonly AiAgentStepSpec spec;
    private readonly AiAgentHandle handle;
    private readonly IAiToolPermissionGuard guard;
    private readonly CancellationToken runToken;
    private readonly List<AiWorkflowMessage> inputs = [];
    private readonly List<(ToolApprovalRequestContent Content, AiToolDefinition Tool, AiToolApprovalRequest Request)> pendingApprovals = [];
    private readonly List<AIContent> pendingAutoRejected = [];
    private readonly List<FunctionCallContent> pendingQuestions = [];
    private AgentSession? session;

    internal AiAgentStep(AiAgentStepSpec spec, AiAgentHandle handle, IAiToolPermissionGuard guard, CancellationToken runToken)
        : base(spec.Agent.Name)
    {
        this.spec = spec;
        this.handle = handle;
        this.guard = guard;
        this.runToken = runToken;
    }

    private string AgentName => spec.Agent.DisplayName;

    /// <summary>給模型看的 <c>ask_user</c> 宣告。</summary>
    internal static AITool AskUserDeclaration { get; } = AIFunctionFactory.CreateDeclaration(
        AskUserToolName,
        "資訊不足、無法繼續時向使用者提問（例如有好幾個符合的專案，要使用者選一個）。一次只問一個問題，問完就停下來等回答。",
        JsonDocument.Parse("""
            {"type":"object","properties":{"question":{"type":"string","description":"要問使用者的問題"}},"required":["question"]}
            """).RootElement.Clone());

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
        => protocolBuilder
            .ConfigureRoutes(routes =>
            {
                routes.AddHandler<AiWorkflowMessage>(OnMessageAsync);
                routes.AddHandler<AiWorkflowRevision>(OnRevisionAsync);
                routes.AddHandler<AiWorkflowHumanResponse>(OnHumanResponseAsync);
            })
            .SendsMessage<AiWorkflowMessage>()
            .SendsMessage<AiWorkflowHumanRequest>()
            .YieldsOutput<AiWorkflowOutput>();

    private async ValueTask OnMessageAsync(AiWorkflowMessage message, IWorkflowContext context, CancellationToken cancellationToken)
    {
        inputs.Add(message);
        if (inputs.Count < spec.ExpectedInputs)
        {
            return;
        }

        var text = spec.ComposeInput?.Invoke(inputs.ToList()) ?? inputs[0].Text;
        inputs.Clear();
        await EmitAsync(context, new AiWorkflowUpdate { Agent = AgentName, AgentStarted = true });
        await RunAsync([new ChatMessage(ChatRole.User, text)], context);
    }

    private async ValueTask OnRevisionAsync(AiWorkflowRevision revision, IWorkflowContext context, CancellationToken cancellationToken)
    {
        var text = spec.ComposeRevision?.Invoke(revision.Comment)
            ?? throw new InvalidOperationException($"步驟 {Id} 不接受退回。");
        await EmitAsync(context, new AiWorkflowUpdate { Agent = AgentName, AgentStarted = true });
        await RunAsync([new ChatMessage(ChatRole.User, text)], context);
    }

    private async ValueTask OnHumanResponseAsync(AiWorkflowHumanResponse response, IWorkflowContext context, CancellationToken cancellationToken)
    {
        if (pendingApprovals.Count > 0 || pendingAutoRejected.Count > 0)
        {
            var contents = new List<AIContent>(pendingAutoRejected);
            foreach (var (content, tool, request) in pendingApprovals)
            {
                var approved = response.ApprovedRequestIds.Contains(request.RequestId);
                if (approved == false)
                {
                    await guard.AuditAsync(AuditActions.Ai.ToolRejected, spec.Agent.Name, tool, success: false);
                }

                contents.Add(content.CreateResponse(approved, approved ? null : AiToolApprovals.RejectedReason));
            }

            pendingApprovals.Clear();
            pendingAutoRejected.Clear();
            await RunAsync([new ChatMessage(ChatRole.User, contents)], context);
            return;
        }

        if (pendingQuestions.Count > 0)
        {
            // 一次只該問一個；模型若同時問了好幾個，第一個給回答，其餘說明使用者只回答了第一個（每個工具呼叫都要有結果）。
            var results = pendingQuestions
                .Select((call, index) => (AIContent)new FunctionResultContent(
                    call.CallId,
                    index == 0 ? response.Answer ?? string.Empty : "（使用者只回答了第一個問題，請一次只問一個。）"))
                .ToList();
            pendingQuestions.Clear();
            await RunAsync([new ChatMessage(ChatRole.Tool, results)], context);
        }
    }

    /// <summary>
    /// 跑 agent 直到它完成或需要使用者。沒有權限而全部被自動拒絕的核准不打擾使用者，直接送回去再跑一次。
    /// 模型呼叫失敗（<see cref="AiCallException"/>，已記帳）或取消時直接往外丟，由執行器轉成結果。
    /// </summary>
    private async Task RunAsync(IList<ChatMessage> messages, IWorkflowContext context)
    {
        session ??= await handle.Agent.CreateSessionAsync(runToken);

        while (true)
        {
            var text = new StringBuilder();
            var approvals = new List<ToolApprovalRequestContent>();
            var questions = new List<FunctionCallContent>();
            var callNames = new Dictionary<string, string>(StringComparer.Ordinal);

            await foreach (var update in handle.Agent.RunStreamingAsync(messages, session, cancellationToken: runToken))
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case ToolApprovalRequestContent request:
                            approvals.Add(request);
                            break;
                        case FunctionCallContent call when call.Name == AskUserToolName:
                            questions.Add(call);
                            break;
                        case FunctionCallContent call:
                            callNames[call.CallId] = call.Name;
                            await EmitAsync(context, new AiWorkflowUpdate { Agent = AgentName, ToolActivity = new AiToolActivity(call.Name, DisplayNameOf(call.Name), Completed: false) });
                            break;
                        case FunctionResultContent result when callNames.TryGetValue(result.CallId, out var name):
                            await EmitAsync(context, new AiWorkflowUpdate { Agent = AgentName, ToolActivity = new AiToolActivity(name, DisplayNameOf(name), Completed: true) });
                            break;
                    }
                }

                if (string.IsNullOrEmpty(update.Text) == false)
                {
                    text.Append(update.Text);
                    await EmitAsync(context, new AiWorkflowUpdate { Agent = AgentName, TextDelta = update.Text });
                }
            }

            if (approvals.Count > 0)
            {
                var (forUser, autoRejected) = await AiToolApprovals.TriageAsync(spec.Agent, approvals, guard);
                if (forUser.Count == 0)
                {
                    messages = [new ChatMessage(ChatRole.User, autoRejected)];
                    continue;
                }

                pendingApprovals.AddRange(forUser);
                pendingAutoRejected.AddRange(autoRejected);
                await context.SendMessageAsync(new AiWorkflowHumanRequest
                {
                    Kind = AiWorkflowRequestKind.ToolApproval,
                    Agent = AgentName,
                    Approvals = forUser.Select(item => item.Request).ToList(),
                }, AiWorkflowGraph.HumanPortId(Id), runToken);
                return;
            }

            if (questions.Count > 0)
            {
                pendingQuestions.AddRange(questions);
                await context.SendMessageAsync(new AiWorkflowHumanRequest
                {
                    Kind = AiWorkflowRequestKind.Question,
                    Agent = AgentName,
                    Question = QuestionOf(questions[0]),
                }, AiWorkflowGraph.HumanPortId(Id), runToken);
                return;
            }

            var markdown = text.ToString();
            await EmitAsync(context, new AiWorkflowUpdate { Agent = AgentName, AgentCompleted = true });
            if (spec.OutputTitle is not null)
            {
                await context.YieldOutputAsync(new AiWorkflowOutput(spec.OutputTitle, markdown), runToken);
            }

            await context.SendMessageAsync(new AiWorkflowMessage(markdown, AgentName), cancellationToken: runToken);
            return;
        }
    }

    private ValueTask EmitAsync(IWorkflowContext context, AiWorkflowUpdate update)
        => context.AddEventAsync(new AiWorkflowStepEvent(update), runToken);

    private string DisplayNameOf(string toolName)
        => spec.Agent.Tools.FirstOrDefault(tool => tool.Name == toolName)?.DisplayName ?? toolName;

    private static string QuestionOf(FunctionCallContent call)
        => call.Arguments?.TryGetValue("question", out var value) == true
            ? value switch
            {
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
                null => string.Empty,
                _ => value.ToString() ?? string.Empty,
            }
            : string.Empty;
}

/// <summary>
/// 審閱關卡（0.9.144 起）：把草稿交給使用者，通過就送往下一步並成為產出、退回就把意見送回撰寫者、結束就停在這裡。
/// 退回次數到上限後只剩通過與結束（畫面不給退回，執行器也會擋）。
/// </summary>
public sealed class AiReviewGateStep : Executor
{
    /// <summary>畫面與紀錄上顯示的名稱。</summary>
    public const string DisplayName = "審閱關卡";

    private readonly string approveTo;
    private readonly string reviseTo;
    private readonly int maxRejections;
    private readonly string outputTitle;
    private string draft = string.Empty;
    private int rejections;

    internal AiReviewGateStep(string id, string approveTo, string reviseTo, int maxRejections, string outputTitle)
        : base(id)
    {
        this.approveTo = approveTo;
        this.reviseTo = reviseTo;
        this.maxRejections = maxRejections;
        this.outputTitle = outputTitle;
    }

    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
        => protocolBuilder
            .ConfigureRoutes(routes =>
            {
                routes.AddHandler<AiWorkflowMessage>(OnDraftAsync);
                routes.AddHandler<AiWorkflowHumanResponse>(OnDecisionAsync);
            })
            .SendsMessage<AiWorkflowMessage>()
            .SendsMessage<AiWorkflowRevision>()
            .SendsMessage<AiWorkflowHumanRequest>()
            .YieldsOutput<AiWorkflowOutput>();

    private async ValueTask OnDraftAsync(AiWorkflowMessage message, IWorkflowContext context, CancellationToken cancellationToken)
    {
        draft = message.Text;
        await context.SendMessageAsync(new AiWorkflowHumanRequest
        {
            Kind = AiWorkflowRequestKind.Review,
            Agent = DisplayName,
            Draft = draft,
            RejectionsLeft = Math.Max(0, maxRejections - rejections),
        }, AiWorkflowGraph.HumanPortId(Id), cancellationToken);
    }

    private async ValueTask OnDecisionAsync(AiWorkflowHumanResponse response, IWorkflowContext context, CancellationToken cancellationToken)
    {
        switch (response.Decision)
        {
            case AiReviewDecision.Approve:
                await context.YieldOutputAsync(new AiWorkflowOutput(outputTitle, draft), cancellationToken);
                await context.SendMessageAsync(new AiWorkflowMessage(draft, DisplayName), approveTo, cancellationToken);
                break;
            case AiReviewDecision.Reject when rejections < maxRejections && string.IsNullOrWhiteSpace(response.Comment) == false:
                rejections++;
                await context.SendMessageAsync(new AiWorkflowRevision(response.Comment!.Trim()), reviseTo, cancellationToken);
                break;
            default:
                // 結束（或不合規則的退回，執行器已先擋下）：不再送出任何訊息，工作流程就此停止。
                break;
        }
    }
}

/// <summary>把收到的訊息原樣轉給所有出去的邊（並行分支的起點）。</summary>
internal sealed class AiForwardStep(string id) : Executor(id)
{
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
        => protocolBuilder
            .ConfigureRoutes(routes => routes.AddHandler<AiWorkflowMessage>(
                (message, context, cancellationToken) => context.SendMessageAsync(message, cancellationToken: cancellationToken)))
            .SendsMessage<AiWorkflowMessage>();
}
