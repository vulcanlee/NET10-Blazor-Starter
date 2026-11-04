using Microsoft.Agents.AI.Workflows;
using MyProject.Web.Ai.Agents;

namespace MyProject.Web.Ai.Workflows;

/// <summary>
/// 一條多 Agent 工作流程的定義（0.9.144 起）。和 <see cref="AiAgentDefinition"/> 一樣寫在 C# 程式碼裡。
///
/// <para>
/// 流程由<b>步驟</b>（<see cref="Steps"/>，每個是一個 agent）與 <see cref="Wire"/> 接起來的邊組成，
/// 執行器（<see cref="IAiWorkflowRunner"/>）每次執行都重新建立 agent 與步驟，並自動接上每個步驟的人工介入通道與產出。
/// 新增一條工作流程的步驟見 <c>docs/architecture/MAF-AI呼叫架構.md</c>「工作流程」。
/// </para>
/// </summary>
public sealed record AiWorkflowDefinition
{
    /// <summary>程式內的識別名稱（英數），也是「我的執行紀錄」的分類（存在 <c>AiAgentConversation.AgentName</c>）。</summary>
    public required string Name { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>畫面上的一兩句說明。</summary>
    public required string Description { get; init; }

    /// <summary>輸入框的提示文字。</summary>
    public required string InputPlaceholder { get; init; }

    /// <summary>Token 用量的作業名稱（<c>TokenUsageOperations</c>），流程裡每個 agent 共用。</summary>
    public required string Operation { get; init; }

    /// <summary>模型設定（<c>nameof(AiModelSettings.屬性)</c>），流程裡每個 agent 共用；null 用預設模型。</summary>
    public string? ModelKey { get; init; }

    public required IReadOnlyList<AiAgentStepSpec> Steps { get; init; }

    /// <summary>
    /// 接邊並回傳起點。步驟以 agent 的 <see cref="AiAgentDefinition.Name"/> 取得（<see cref="AiWorkflowGraph.Step"/>）；
    /// 起點收到的是使用者輸入（<see cref="AiWorkflowMessage"/>）。
    /// </summary>
    public required Action<AiWorkflowGraph> Wire { get; init; }
}

/// <summary>畫面上可以選的工作流程（0.9.144 起）。新增一條工作流程後加在這裡。</summary>
public static class AiWorkflowCatalog
{
    public static IReadOnlyList<AiWorkflowDefinition> All { get; } =
    [
        ProjectStatusReportWorkflow.Definition,
        MultiPerspectiveReviewWorkflow.Definition,
    ];
}

/// <summary>工作流程裡的一個 agent 步驟。</summary>
public sealed record AiAgentStepSpec
{
    public required AiAgentDefinition Agent { get; init; }

    /// <summary>收齊幾則輸入才開始（並行審查的彙整 agent 要等每位審查者）。</summary>
    public int ExpectedInputs { get; init; } = 1;

    /// <summary>把收到的輸入組成給 agent 的訊息；null 時直接用第一則輸入的文字。</summary>
    public Func<IReadOnlyList<AiWorkflowMessage>, string>? ComposeInput { get; init; }

    /// <summary>收到審閱退回時給 agent 的訊息；null 表示這個步驟不接受退回。</summary>
    public Func<string, string>? ComposeRevision { get; init; }

    /// <summary>可以呼叫 <c>ask_user</c> 向使用者提問（<see cref="AiAgentStep.AskUserToolName"/>）。</summary>
    public bool CanAskUser { get; init; }

    /// <summary>完成時把結果當成工作流程的產出（例如彙整意見）；null 表示不是產出。</summary>
    public string? OutputTitle { get; init; }
}

/// <summary>
/// <see cref="AiWorkflowDefinition.Wire"/> 用的接線介面：取得步驟、建立審閱關卡與分流節點、接邊。
/// 人工介入通道與產出由執行器自動接上，這裡只接步驟之間的邊。
/// </summary>
public sealed class AiWorkflowGraph
{
    private readonly IReadOnlyDictionary<string, AiAgentStep> steps;
    private readonly List<Executor> humanSteps = [];
    private WorkflowBuilder? builder;

    internal AiWorkflowGraph(IReadOnlyDictionary<string, AiAgentStep> steps)
    {
        this.steps = steps;
        humanSteps.AddRange(steps.Values);
    }

    /// <summary>會等使用者的步驟（agent 與審閱關卡），執行器替每個接上一個 <see cref="RequestPort"/>。</summary>
    internal IReadOnlyList<Executor> HumanSteps => humanSteps;

    /// <summary>步驟的人工介入通道編號（步驟送出 <see cref="AiWorkflowHumanRequest"/> 時指定它，不會誤送給下游步驟）。</summary>
    internal static string HumanPortId(string stepId) => stepId + ".human";

    internal WorkflowBuilder Builder
        => builder ?? throw new InvalidOperationException("工作流程的 Wire 必須先呼叫 Start 指定起點。");

    /// <summary>以 agent 名稱取得步驟。</summary>
    public ExecutorBinding Step(string agentName)
        => steps.TryGetValue(agentName, out var step)
            ? step
            : throw new InvalidOperationException($"工作流程沒有名為 {agentName} 的步驟。");

    /// <summary>
    /// 建立審閱關卡：使用者通過就把草稿送往 <paramref name="approveTo"/>，退回就把意見送回 <paramref name="reviseTo"/>，
    /// 最多退回 <paramref name="maxRejections"/> 次；通過時草稿以 <paramref name="outputTitle"/> 成為工作流程的產出。
    /// 會自動接上關卡到兩個去處的邊。
    /// </summary>
    public ExecutorBinding ReviewGate(string id, string approveTo, string reviseTo, int maxRejections, string outputTitle)
    {
        var gate = new AiReviewGateStep(id, approveTo, reviseTo, maxRejections, outputTitle);
        humanSteps.Add(gate);
        return gate;
    }

    /// <summary>把收到的訊息原樣轉給所有出去的邊（並行分支的起點）。</summary>
    public ExecutorBinding Fork(string id) => new AiForwardStep(id);

    public AiWorkflowGraph Start(ExecutorBinding start)
    {
        builder = new WorkflowBuilder(start);
        return this;
    }

    public AiWorkflowGraph Edge(ExecutorBinding from, ExecutorBinding to)
    {
        Builder.AddEdge(from, to);
        return this;
    }

    public AiWorkflowGraph FanOut(ExecutorBinding from, params ExecutorBinding[] to)
    {
        Builder.AddFanOutEdge(from, to);
        return this;
    }
}
