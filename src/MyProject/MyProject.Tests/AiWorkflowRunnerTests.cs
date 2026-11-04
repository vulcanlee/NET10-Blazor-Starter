using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Others;
using MyProject.Models.Systems;
using MyProject.Web.Ai;
using MyProject.Web.Ai.Agents;
using MyProject.Web.Ai.Workflows;
using MyProject.Web.Configuration;
using Fixture = MyProject.Tests.ProjectServiceTeamAccessTests.ProjectServiceFixture;

namespace MyProject.Tests;

/// <summary>
/// 多 Agent 工作流程（0.9.144 起）：兩條範例實際跑在 MAF Workflows 上，上游依系統提示詞分給各 agent 的劇本回應，
/// 工具用真的 <see cref="ProjectAgentTools"/>（SQLite 記憶體資料庫、團隊範圍照常生效）。
/// </summary>
public sealed class AiWorkflowRunnerTests : IAsyncLifetime
{
    private static readonly Regex AgentKey = new("AGENT:(\\w+)", RegexOptions.Compiled);

    private readonly FakeToolGuard guard = new();
    private readonly RecordingTokenUsageRecorder usage = new();
    private readonly FakeAiCallLogRecorder callLog = new();
    private readonly WorkflowConversationStore store = new();
    private readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> scripts = [];
    private readonly List<(string Key, string Body)> requests = [];
    private readonly object gate = new();
    private Fixture fixture = null!;
    private Dictionary<string, int> projectIds = null!;

    public async Task InitializeAsync()
    {
        fixture = await Fixture.CreateAsync();
        projectIds = await fixture.SeedDefaultProjectsAsync();
        var project = await fixture.Context.Project.SingleAsync(x => x.Id == projectIds["團隊A專案"]);
        project.Description = "原本的描述";
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();
        guard.Allowed.UnionWith(["search_projects", "get_project_detail", "append_project_status_summary"]);
    }

    public async Task DisposeAsync() => await fixture.DisposeAsync();

    [Fact]
    public async Task ProjectReport_FullFlow_ShouldAskReviewReviseApproveAndAppendSummary()
    {
        var id = projectIds["團隊A專案"];
        Script(PromptTemplateKeys.ProjectReportGatherer,
            AiSse.ResponsesToolCall("call_q", "ask_user", """{"question":"是哪一個專案？"}"""),
            AiSse.ResponsesToolCall("call_d", "get_project_detail", $$"""{"projectId":{{id}}}"""),
            AiSse.Responses(["事實清單"]));
        Script(PromptTemplateKeys.ProjectReportWriter, AiSse.Responses(["草稿一"]), AiSse.Responses(["草稿二"]));
        Script(PromptTemplateKeys.ProjectReportFinalizer,
            AiSse.ResponsesToolCall("call_a", "append_project_status_summary", $$"""{"projectId":{{id}},"summary":"進度正常\n風險低"}"""),
            AiSse.Responses(["已附加摘要。"]));
        var run = await StartAsync(ProjectStatusReportWorkflow.Definition);

        var question = await NextRequestAsync(run.RunAsync("幫我寫專案報告"));
        Assert.Equal(AiWorkflowRequestKind.Question, question.Request.Kind);
        Assert.Equal("是哪一個專案？", question.Request.Question);

        var review = await NextRequestAsync(run.RespondAsync(question.RequestId, new AiWorkflowHumanResponse { Answer = "團隊A專案" }));
        Assert.Equal((AiWorkflowRequestKind.Review, "草稿一", 3), (review.Request.Kind, review.Request.Draft, review.Request.RejectionsLeft));

        review = await NextRequestAsync(run.RespondAsync(review.RequestId, new AiWorkflowHumanResponse { Decision = AiReviewDecision.Reject, Comment = "補上風險" }));
        Assert.Equal(("草稿二", 2), (review.Request.Draft, review.Request.RejectionsLeft));

        var approval = await NextRequestAsync(run.RespondAsync(review.RequestId, new AiWorkflowHumanResponse { Decision = AiReviewDecision.Approve }));
        Assert.Equal(AiWorkflowRequestKind.ToolApproval, approval.Request.Kind);
        var tool = Assert.Single(approval.Request.Approvals);
        Assert.Equal("附加專案狀態摘要", tool.DisplayName);

        var updates = await CollectAsync(run.RespondAsync(approval.RequestId, new AiWorkflowHumanResponse { ApprovedRequestIds = [tool.RequestId] }));

        var result = updates[^1].Result!;
        Assert.Equal(AiWorkflowOutcome.Completed, result.Outcome);
        Assert.Equal(7, result.ModelCalls); // 蒐集 3（提問、讀明細、整理）、撰寫 2、收尾 2

        // 摘要接在原本的描述後面（收成一行），原本的內容還在。
        var stored = await fixture.CreateService(isAdmin: true).GetAsync(id);
        Assert.Equal($"原本的描述\n\n【狀態摘要 {DateTime.Now:yyyy/MM/dd}】進度正常 風險低", stored.Description);

        // 問題的回答回到蒐集 agent；退回意見送給撰寫；收尾收到通過的那一版。
        Assert.Contains("團隊A專案", Bodies(PromptTemplateKeys.ProjectReportGatherer)[1], StringComparison.Ordinal);
        Assert.Contains("事實清單", Bodies(PromptTemplateKeys.ProjectReportWriter)[0], StringComparison.Ordinal);
        Assert.Contains("補上風險", Bodies(PromptTemplateKeys.ProjectReportWriter)[1], StringComparison.Ordinal);
        Assert.Contains("草稿二", Bodies(PromptTemplateKeys.ProjectReportFinalizer)[0], StringComparison.Ordinal);

        // 所有模型呼叫共用同一個 RunId 與 ConversationId，關聯說明分得出是哪個 agent。
        Assert.Equal(7, callLog.Entries.Count);
        Assert.Single(callLog.Entries.Select(entry => entry.RunId).Distinct());
        Assert.All(callLog.Entries, entry => Assert.Equal(run.ConversationId, entry.ConversationId));
        Assert.All(usage.Entries, entry => Assert.Equal(TokenUsageOperations.ProjectStatusReport, entry.Operation));
        Assert.Equal(
            ["專案狀態報告／蒐集", "專案狀態報告／撰寫", "專案狀態報告／收尾"],
            callLog.Entries.Select(entry => entry.RelatedInfo).Distinct().ToList());

        // 紀錄存起來了：流程名稱、帶 agent 的每一則、定稿報告是產出。
        var saved = store.Saved[run.ConversationId];
        Assert.Equal(ProjectStatusReportWorkflow.Definition.Name, saved.AgentName);
        Assert.Equal("幫我寫專案報告", saved.Title);
        var transcript = saved.Content.Transcript;
        Assert.Contains(transcript, entry => entry is { Kind: AiAgentTranscriptKinds.Notice, Agent: "蒐集", Text: "提問：是哪一個專案？" });
        Assert.Contains(transcript, entry => entry is { Kind: AiAgentTranscriptKinds.User, Agent: "蒐集", Text: "團隊A專案" });
        Assert.Contains(transcript, entry => entry is { Kind: AiAgentTranscriptKinds.Notice, Text: "你退回了草稿：補上風險" });
        Assert.Contains(transcript, entry => entry is { Kind: AiAgentTranscriptKinds.Output, Agent: "定稿報告", Text: "草稿二" });
        Assert.Contains(transcript, entry => entry is { Kind: AiAgentTranscriptKinds.Notice, Agent: "收尾", Text: "你核准了：附加專案狀態摘要" });
        Assert.Contains(transcript, entry => entry is { Kind: AiAgentTranscriptKinds.Assistant, Agent: "收尾", Text: "已附加摘要。" });
        Assert.Contains(guard.Audits, audit => audit is { Action: AuditActions.Ai.ToolInvoked, Tool: "append_project_status_summary", Success: true });
    }

    [Fact]
    public async Task ProjectReport_AfterThreeRejections_ShouldOnlyAllowApproveOrEnd()
    {
        Script(PromptTemplateKeys.ProjectReportGatherer, AiSse.Responses(["事實"]));
        Script(PromptTemplateKeys.ProjectReportWriter, AiSse.Responses(["v1"]), AiSse.Responses(["v2"]), AiSse.Responses(["v3"]), AiSse.Responses(["v4"]));
        var run = await StartAsync(ProjectStatusReportWorkflow.Definition);

        var review = await NextRequestAsync(run.RunAsync("報告"));
        for (var i = 0; i < ProjectStatusReportWorkflow.MaxRejections; i++)
        {
            review = await NextRequestAsync(run.RespondAsync(review.RequestId, new AiWorkflowHumanResponse { Decision = AiReviewDecision.Reject, Comment = $"意見{i}" }));
        }

        Assert.Equal(("v4", 0), (review.Request.Draft, review.Request.RejectionsLeft));
        Assert.Throws<InvalidOperationException>(
            () => run.RespondAsync(review.RequestId, new AiWorkflowHumanResponse { Decision = AiReviewDecision.Reject, Comment = "再改" }));

        var updates = await CollectAsync(run.RespondAsync(review.RequestId, new AiWorkflowHumanResponse { Decision = AiReviewDecision.End }));

        Assert.Equal(AiWorkflowOutcome.Ended, updates[^1].Result!.Outcome);
        Assert.Empty(Bodies(PromptTemplateKeys.ProjectReportFinalizer));
        Assert.DoesNotContain(store.Saved[run.ConversationId].Content.Transcript, entry => entry.Kind == AiAgentTranscriptKinds.Output);
    }

    [Fact]
    public async Task ProjectReport_RejectWithoutComment_ShouldBeRefused()
    {
        Script(PromptTemplateKeys.ProjectReportGatherer, AiSse.Responses(["事實"]));
        Script(PromptTemplateKeys.ProjectReportWriter, AiSse.Responses(["v1"]));
        var run = await StartAsync(ProjectStatusReportWorkflow.Definition);
        var review = await NextRequestAsync(run.RunAsync("報告"));

        Assert.Throws<InvalidOperationException>(
            () => run.RespondAsync(review.RequestId, new AiWorkflowHumanResponse { Decision = AiReviewDecision.Reject, Comment = "  " }));
        Assert.Single(run.PendingRequests);
    }

    [Fact]
    public async Task Finalizer_WithoutEditPermission_ShouldBeDeniedWithoutAsking()
    {
        guard.Allowed.Remove("append_project_status_summary");
        var id = projectIds["團隊A專案"];
        var run = await RunToFinalizerAsync(id);

        var updates = await CollectAsync(run.Pending);

        Assert.Equal(AiWorkflowOutcome.Completed, updates[^1].Result!.Outcome);
        Assert.DoesNotContain(updates, update => update.Request is not null);
        Assert.Contains(guard.Audits, audit => audit is { Action: AuditActions.Ai.ToolDenied, Tool: "append_project_status_summary" });
        Assert.Contains(AiToolApprovals.NoPermissionReason, Bodies(PromptTemplateKeys.ProjectReportFinalizer)[1], StringComparison.Ordinal);
        Assert.Equal("原本的描述", (await fixture.CreateService(isAdmin: true).GetAsync(id)).Description);
    }

    [Fact]
    public async Task Finalizer_UserRejects_ShouldAuditAndKeepDescription()
    {
        var id = projectIds["團隊A專案"];
        var run = await RunToFinalizerAsync(id);
        var approval = await NextRequestAsync(run.Pending);

        var updates = await CollectAsync(run.Run.RespondAsync(approval.RequestId, new AiWorkflowHumanResponse()));

        Assert.Equal(AiWorkflowOutcome.Completed, updates[^1].Result!.Outcome);
        Assert.Contains(guard.Audits, audit => audit is { Action: AuditActions.Ai.ToolRejected, Tool: "append_project_status_summary" });
        Assert.Equal("原本的描述", (await fixture.CreateService(isAdmin: true).GetAsync(id)).Description);
        Assert.Contains(store.Saved[run.Run.ConversationId].Content.Transcript, entry => entry.Text == "你拒絕了：附加專案狀態摘要");
    }

    [Fact]
    public async Task MultiReview_ShouldRunThreeReviewersThenAggregateAllOpinions()
    {
        Script(PromptTemplateKeys.ReviewRequirements, AiSse.Responses(["意見-需求"]));
        Script(PromptTemplateKeys.ReviewTechnical, AiSse.Responses(["意見-技術"]));
        Script(PromptTemplateKeys.ReviewUsability, AiSse.Responses(["意見-體驗"]));
        Script(PromptTemplateKeys.ReviewAggregator, AiSse.Responses(["彙整結果"]));
        var run = await StartAsync(MultiPerspectiveReviewWorkflow.Definition);

        var updates = await CollectAsync(run.RunAsync("一份規格"));

        var result = updates[^1].Result!;
        Assert.Equal((AiWorkflowOutcome.Completed, 4), (result.Outcome, result.ModelCalls));
        foreach (var key in new[] { PromptTemplateKeys.ReviewRequirements, PromptTemplateKeys.ReviewTechnical, PromptTemplateKeys.ReviewUsability })
        {
            Assert.Contains("一份規格", Assert.Single(Bodies(key)), StringComparison.Ordinal);
        }

        var aggregatorBody = Assert.Single(Bodies(PromptTemplateKeys.ReviewAggregator));
        var positions = new[] { "意見-需求", "意見-技術", "意見-體驗" }.Select(text => aggregatorBody.IndexOf(text, StringComparison.Ordinal)).ToList();
        Assert.All(positions, position => Assert.True(position >= 0));
        Assert.Equal(positions.Order().ToList(), positions);

        var output = Assert.Single(updates, update => update.Output is not null).Output!;
        Assert.Equal(("彙整意見", "彙整結果"), (output.Title, output.Markdown));
        Assert.Contains(store.Saved[run.ConversationId].Content.Transcript, entry => entry is { Kind: AiAgentTranscriptKinds.Assistant, Agent: "使用者體驗", Text: "意見-體驗" });
    }

    [Fact]
    public async Task UpstreamFailure_ShouldEndAsFailedAndPersist()
    {
        Script(PromptTemplateKeys.ProjectReportGatherer, AiSse.Responses(["事實"]));
        scripts[PromptTemplateKeys.ProjectReportWriter] = new Queue<Func<HttpResponseMessage>>(
            [() => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}", Encoding.UTF8, "application/json") }]);
        var run = await StartAsync(ProjectStatusReportWorkflow.Definition);

        var updates = await CollectAsync(run.RunAsync("報告"));

        var result = updates[^1].Result!;
        Assert.Equal(AiWorkflowOutcome.Failed, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.Equal(AiAgentTranscriptKinds.Error, store.Saved[run.ConversationId].Content.Transcript[^1].Kind);
    }

    [Fact]
    public async Task Stop_ShouldCancelWholeWorkflow()
    {
        Script(PromptTemplateKeys.ProjectReportGatherer, AiSse.Responses(["事實"]));
        Script(PromptTemplateKeys.ProjectReportWriter, AiSse.Responses(["v1"]));
        var run = await StartAsync(ProjectStatusReportWorkflow.Definition);
        var review = await NextRequestAsync(run.RunAsync("報告"));

        var updates = await CollectAsync(run.RespondAsync(review.RequestId, new AiWorkflowHumanResponse { Decision = AiReviewDecision.Approve }, new CancellationToken(canceled: true)));

        Assert.Equal(AiWorkflowOutcome.Canceled, updates[^1].Result!.Outcome);
        Assert.Equal("已停止工作流程。", store.Saved[run.ConversationId].Content.Transcript[^1].Text);
    }

    [Fact]
    public async Task LeavingWhileWaiting_ShouldPersistAsInterrupted_AndNotBeResumable()
    {
        Script(PromptTemplateKeys.ProjectReportGatherer, AiSse.Responses(["事實"]));
        Script(PromptTemplateKeys.ProjectReportWriter, AiSse.Responses(["v1"]));
        var run = await StartAsync(ProjectStatusReportWorkflow.Definition);
        await NextRequestAsync(run.RunAsync("報告"));

        await run.DisposeAsync();

        Assert.Equal(AiWorkflowOutcome.Interrupted, run.Result!.Outcome);
        var saved = store.Saved[run.ConversationId];
        Assert.Equal("執行中斷（離開頁面），工作流程無法從中途接續。", saved.Content.Transcript[^1].Text);
        Assert.False(saved.HasPendingApproval);

        // Agent 測試台的接續不會把工作流程紀錄當成對話。
        var agentRunner = new AiAgentRunner(
            CreateFactory(), new AiAgentToolFactory(new ServiceCollection().BuildServiceProvider(), guard), guard,
            new KeyPromptProvider(), Options(), new StaticOptionsMonitor<AiModelSettings>(new AiModelSettings()), store,
            NullLogger<AiAgentRunner>.Instance);
        Assert.Null(await agentRunner.ResumeAsync(AgentPlaygroundAgent.Definition, run.ConversationId));
    }

    /// <summary>跑到收尾 agent 要求附加摘要為止（蒐集、撰寫、審閱通過）。</summary>
    private async Task<(AiWorkflowRun Run, IAsyncEnumerable<AiWorkflowUpdate> Pending)> RunToFinalizerAsync(int projectId)
    {
        Script(PromptTemplateKeys.ProjectReportGatherer, AiSse.Responses(["事實"]));
        Script(PromptTemplateKeys.ProjectReportWriter, AiSse.Responses(["報告"]));
        Script(PromptTemplateKeys.ProjectReportFinalizer,
            AiSse.ResponsesToolCall("call_a", "append_project_status_summary", $$"""{"projectId":{{projectId}},"summary":"摘要"}"""),
            AiSse.Responses(["結果說明"]));
        var run = await StartAsync(ProjectStatusReportWorkflow.Definition);
        var review = await NextRequestAsync(run.RunAsync("報告"));
        return (run, run.RespondAsync(review.RequestId, new AiWorkflowHumanResponse { Decision = AiReviewDecision.Approve }));
    }

    private void Script(string promptKey, params string[] responses)
        => scripts[promptKey] = new Queue<Func<HttpResponseMessage>>(responses.Select(sse => (Func<HttpResponseMessage>)(() => AiSse.Response(sse))));

    private List<string> Bodies(string promptKey)
    {
        lock (gate)
        {
            return requests.Where(request => request.Key == promptKey).Select(request => request.Body).ToList();
        }
    }

    private Task<AiWorkflowRun> StartAsync(AiWorkflowDefinition definition)
    {
        var services = new ServiceCollection()
            .AddSingleton(new ProjectAgentTools(fixture.CreateService(isAdmin: false, "團隊A")))
            .BuildServiceProvider();
        var runner = new AiWorkflowRunner(
            CreateFactory(),
            new AiAgentToolFactory(services, guard),
            guard,
            new KeyPromptProvider(),
            Options(),
            new StaticOptionsMonitor<AiModelSettings>(new AiModelSettings()),
            store,
            NullLogger<AiWorkflowRunner>.Instance);
        return runner.StartAsync(definition);
    }

    private AiAgentFactory CreateFactory()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var key = AgentKey.Match(body).Groups[1].Value;
            lock (gate)
            {
                requests.Add((key, body));
                return scripts.TryGetValue(key, out var queue) && queue.Count > 0
                    ? queue.Dequeue()()
                    : throw new InvalidOperationException($"沒有 {key} 的劇本。");
            }
        });

        return AiTestAgents.CreateFactory(
            new StubHttpClientFactory(handler),
            Options(),
            usage,
            new CurrentUserService { CurrentUser = new CurrentUser { Id = 7, Account = "support" } },
            callLog);
    }

    private static StaticOptionsMonitor<AiSettings> Options() => new(new AiSettings
    {
        Provider = nameof(AiProvider.OpenAI),
        ApiKey = "key",
        Model = "gpt-4o",
    });

    private static async Task<AiWorkflowPendingRequest> NextRequestAsync(IAsyncEnumerable<AiWorkflowUpdate> stream)
    {
        var updates = await CollectAsync(stream);
        var request = updates.LastOrDefault(update => update.Request is not null)?.Request;
        return request ?? throw new Xunit.Sdk.XunitException(
            $"工作流程沒有停下來等使用者：{updates.LastOrDefault()?.Result?.Outcome} {updates.LastOrDefault()?.Result?.ErrorMessage}");
    }

    private static async Task<List<AiWorkflowUpdate>> CollectAsync(IAsyncEnumerable<AiWorkflowUpdate> stream)
    {
        var updates = new List<AiWorkflowUpdate>();
        await foreach (var update in stream)
        {
            updates.Add(update);
        }

        return updates;
    }

    /// <summary>系統提示詞只放種類代碼，上游依它分給各 agent 的劇本。</summary>
    private sealed class KeyPromptProvider : IAiSystemPromptProvider
    {
        public Task<string> GetAsync(string templateKey, CancellationToken cancellationToken = default)
            => Task.FromResult($"AGENT:{templateKey}");
    }

    /// <summary>記憶體版存檔（經過一次 JSON 來回，同真的存檔）；標題只在第一次存時採用。</summary>
    private sealed class WorkflowConversationStore : IAiAgentConversationStore
    {
        public Dictionary<Guid, AiAgentConversationSnapshot> Saved { get; } = [];

        public Task SaveAsync(AiAgentConversationSnapshot snapshot)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(snapshot.Content, AiAgentConversationService.FileJsonOptions);
            var content = System.Text.Json.JsonSerializer.Deserialize<AiAgentConversationContent>(json, AiAgentConversationService.FileJsonOptions)!;
            var title = Saved.TryGetValue(snapshot.ConversationId, out var existing) ? existing.Title : snapshot.Title;
            Saved[snapshot.ConversationId] = snapshot with { Title = title, Content = content };
            return Task.CompletedTask;
        }

        public Task<AiAgentConversationLoaded?> LoadMineAsync(Guid conversationId)
            => Task.FromResult(Saved.TryGetValue(conversationId, out var saved)
                ? new AiAgentConversationLoaded(
                    new AiAgentConversationSummary(saved.ConversationId, saved.AgentName, saved.Title, DateTime.Now, DateTime.Now, false),
                    saved.Content)
                : null);
    }

    private sealed class FakeToolGuard : IAiToolPermissionGuard
    {
        public HashSet<string> Allowed { get; } = [];

        public List<(string Action, string Tool, bool Success)> Audits { get; } = [];

        public Task<bool> CanInvokeAsync(AiToolDefinition tool) => Task.FromResult(Allowed.Contains(tool.Name));

        public Task AuditAsync(string action, string agentName, AiToolDefinition tool, bool success, string? note = null)
        {
            lock (Audits)
            {
                Audits.Add((action, tool.Name, success));
            }

            return Task.CompletedTask;
        }
    }
}
