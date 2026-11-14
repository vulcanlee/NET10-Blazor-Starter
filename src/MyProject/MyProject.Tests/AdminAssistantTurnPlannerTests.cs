using Microsoft.Extensions.Logging.Abstractions;
using MyProject.AccessDatas.Models;
using MyProject.Models.Systems;
using MyProject.Web.Ai;
using MyProject.Web.Ai.Agents.AdminAssistant;
using MyProject.Web.Configuration;

namespace MyProject.Tests;

/// <summary>系統管理助理的分派規劃器（0.9.155 起）：分派呼叫、退回全部領域、提示詞組裝。</summary>
public sealed class AdminAssistantTurnPlannerTests
{
    private readonly FakeCompletionClient client = new();
    private readonly AiModelSettings models = new();
    private readonly AiSettings ai = new() { Model = "default-model" };

    [Fact]
    public async Task Plan_RouterSaysOps_LabelAndInstructions()
    {
        client.Replies.Enqueue(Success("ops"));

        var turn = await CreatePlanner().PlanAsync(Guid.NewGuid(), "最近排程的執行狀況", [], CancellationToken.None);

        Assert.Equal("系統設定與維運", turn.Label);
        Assert.Contains("[AdminAssistant]", turn.Instructions, StringComparison.Ordinal);
        Assert.Contains("[AdminAssistantOps]", turn.Instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("[AdminAssistantAccounts]", turn.Instructions, StringComparison.Ordinal);
        Assert.EndsWith(AiPromptGuardrails.For(PromptTemplateKeys.AdminAssistant), turn.Instructions, StringComparison.Ordinal);
        Assert.Equal(AdminAssistantAgent.Name, turn.Definition.Name);
    }

    /// <summary>
    /// 分派用哪個模型（0.10.22 起）：系統參數「系統管理助理分派使用的模型」有值就用它；
    /// 否則用快速模型 <c>AiSettings:FastModel</c>；快速模型也空白時照舊用「系統管理助理使用的模型」。
    /// （<c>ModelKey</c> 指向分派、<c>UseFastModel</c> 一律為 true：分派沒設定時才輪到快速模型，見 AiAgentFactory。）
    /// </summary>
    [Theory]
    [InlineData("gpt-mini", "", nameof(AiModelSettings.AdminAssistantRouter))]
    [InlineData("gpt-mini", "fast-model", nameof(AiModelSettings.AdminAssistantRouter))]
    [InlineData("", "fast-model", nameof(AiModelSettings.AdminAssistantRouter))]
    [InlineData("", "", nameof(AiModelSettings.AdminAssistant))]
    public async Task Plan_RouterRequest_UsesRouterOperationConversationAndModel(string routerModel, string fastModel, string expectedModelKey)
    {
        models.AdminAssistantRouter = routerModel;
        ai.FastModel = fastModel;
        client.Replies.Enqueue(Success("monitoring"));
        var conversationId = Guid.NewGuid();
        List<AiAgentTranscriptEntry> transcript = [new() { Kind = AiAgentTranscriptKinds.User, Text = "前一則" }];

        await CreatePlanner().PlanAsync(conversationId, "這一則", transcript, CancellationToken.None);

        var request = Assert.Single(client.Requests);
        Assert.Equal(TokenUsageOperations.AdminAssistantRouter, request.Operation);
        Assert.Equal(conversationId, request.ConversationId);
        Assert.Equal(expectedModelKey, request.ModelKey);
        Assert.True(request.UseFastModel);
        Assert.Equal(AdminAssistantTurnPlanner.RouterTimeout, request.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(20), AdminAssistantTurnPlanner.RouterTimeout);
        Assert.Equal(2, request.Messages.Count);
        Assert.Equal(AiChatRoles.System, request.Messages[0].Role);
        Assert.Contains("[AdminAssistantRouter]", request.Messages[0].Content, StringComparison.Ordinal);
        Assert.Equal(AiChatRoles.User, request.Messages[1].Role);
        Assert.Equal(AdminAssistantRouting.BuildRouterInput("這一則", transcript), request.Messages[1].Content);
    }

    [Fact]
    public async Task Plan_RouterFails_FallsBackToAllDomains()
    {
        client.Replies.Enqueue(AiAnalysisResult.Failure(AiAnalysisFailureReason.Timeout, "逾時"));

        var turn = await CreatePlanner().PlanAsync(Guid.NewGuid(), "你好", [], CancellationToken.None);

        AssertAllDomains(turn);
    }

    [Fact]
    public async Task Plan_RouterReturnsProse_FallsBackToAllDomains()
    {
        client.Replies.Enqueue(Success("我認為這是監控問題"));

        var turn = await CreatePlanner().PlanAsync(Guid.NewGuid(), "你好", [], CancellationToken.None);

        AssertAllDomains(turn);
    }

    [Fact]
    public async Task HomeTurn_DoesNotCallRouter()
    {
        var planner = CreatePlanner();

        var turn = await planner.HomeTurnAsync(CancellationToken.None);

        Assert.Empty(client.Requests);
        Assert.Equal(AdminAssistantAgent.Home.Tools, turn.Definition.Tools);
        Assert.Same(AdminAssistantAgent.Home, planner.Home);
    }

    /// <summary>定時巡檢（0.9.161 起）：不呼叫分派模型，直接組出巡檢的定義與提示詞。</summary>
    [Fact]
    public async Task PatrolTurn_DoesNotCallRouter_UsesPatrolDefinitionAndAllDomains()
    {
        var turn = await CreatePlanner().PatrolTurnAsync(CancellationToken.None);

        Assert.Empty(client.Requests);
        Assert.Same(AdminAssistantAgent.ScheduledPatrol, turn.Definition);
        Assert.Equal(AdminAssistantRouting.NameOf(AdminAssistantDomain.Patrol), turn.Label);
        foreach (var key in AdminAssistantAgent.DomainPromptKeys.Values)
        {
            Assert.Contains($"[{key}]", turn.Instructions, StringComparison.Ordinal);
        }

        Assert.Contains("這一則是巡檢", turn.Instructions, StringComparison.Ordinal);
        Assert.EndsWith(AiPromptGuardrails.For(PromptTemplateKeys.AdminAssistant), turn.Instructions, StringComparison.Ordinal);
    }

    /// <summary>
    /// 0.10.7 起：分派結果的代碼跟著對話存起來（TurnKey），接續停在核准的對話時用它重建同一輪（同樣的工具、提示詞與標籤），不再呼叫分派模型。
    /// </summary>
    [Fact]
    public async Task RestoreTurn_FromThePlannedKey_RebuildsTheSameTurnWithoutCallingTheRouter()
    {
        client.Replies.Enqueue(Success("ops, accounts"));
        var planner = CreatePlanner();
        var planned = await planner.PlanAsync(Guid.NewGuid(), "停用帳號並調整排程", [], CancellationToken.None);

        var restored = await planner.RestoreTurnAsync(planned.Key!, CancellationToken.None);

        Assert.Equal("accounts,ops", planned.Key);
        Assert.Single(client.Requests);
        Assert.Equal(planned.Definition.Tools, restored.Definition.Tools);
        Assert.Equal(planned.Instructions, restored.Instructions);
        Assert.Equal(planned.Label, restored.Label);
        Assert.Equal(planned.Key, restored.Key);
        Assert.Contains(restored.Definition.Tools, tool => tool.Name == "set_users_enabled");
        Assert.DoesNotContain(restored.Definition.Tools, tool => tool.Name == "delete_exceptions");
    }

    /// <summary>沒有分出領域（分派失敗）那一輪的代碼是空字串，重建後一樣只有唯讀工具、接上全部領域說明。</summary>
    [Fact]
    public async Task RestoreTurn_EmptyKey_IsTheFallbackTurn()
    {
        var turn = await CreatePlanner().RestoreTurnAsync(string.Empty, CancellationToken.None);

        Assert.Empty(client.Requests);
        Assert.Equal(string.Empty, turn.Key);
        Assert.Equal(AdminAssistantAgent.ReadTools, turn.Definition.Tools);
        AssertAllDomains(turn);
    }

    private static void AssertAllDomains(MyProject.Web.Ai.Agents.AiAgentTurn turn)
    {
        Assert.Equal(AdminAssistantRouting.DefaultLabel, turn.Label);
        foreach (var key in AdminAssistantAgent.DomainPromptKeys.Values)
        {
            Assert.Contains($"[{key}]", turn.Instructions, StringComparison.Ordinal);
        }
    }

    private static AiAnalysisResult Success(string markdown) => new() { Success = true, Markdown = markdown };

    private AdminAssistantTurnPlanner CreatePlanner() => new(
        client,
        new KeyNamePromptProvider(),
        new StaticOptionsMonitor<AiModelSettings>(models),
        new StaticOptionsMonitor<AiSettings>(ai),
        NullLogger<AdminAssistantTurnPlanner>.Instance);

    /// <summary>提示詞內容就是「[種類]」，看得出組了哪些段落。</summary>
    private sealed class KeyNamePromptProvider : IAiSystemPromptProvider
    {
        public Task<string> GetAsync(string templateKey, CancellationToken cancellationToken = default)
            => Task.FromResult(AiPromptGuardrails.Compose(templateKey, $"[{templateKey}]"));

        public Task<string> GetInstructionsAsync(string templateKey, CancellationToken cancellationToken = default)
            => Task.FromResult($"[{templateKey}]");
    }

    private sealed class FakeCompletionClient : IAiChatCompletionClient
    {
        public Queue<AiAnalysisResult> Replies { get; } = new();

        public List<AiChatCompletionRequest> Requests { get; } = [];

        public Task<AiAnalysisResult> CompleteAsync(AiChatCompletionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Replies.Dequeue());
        }

        public IAsyncEnumerable<AiStreamUpdate> StreamAsync(AiChatCompletionRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
