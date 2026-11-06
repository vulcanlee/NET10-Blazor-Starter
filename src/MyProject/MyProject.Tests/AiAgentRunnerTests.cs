using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Others;
using MyProject.Models.Systems;
using MyProject.Web.Ai;
using MyProject.Web.Ai.Agents;
using MyProject.Web.Configuration;

namespace MyProject.Tests;

/// <summary>
/// Agent 骨架（0.9.142 起）：工具呼叫迴圈的記帳、權限、核准與稽核。
/// 上游用假的串流回應依序回覆（先要求呼叫工具，再回文字），工具與權限檢查都是可控的替身。
/// </summary>
public sealed class AiAgentRunnerTests
{
    private readonly FakeToolGuard guard = new();
    private readonly SampleTools sample = new();
    private readonly RecordingTokenUsageRecorder usage = new();
    private readonly FakeAiCallLogRecorder callLog = new();
    private readonly InMemoryConversationStore store = new();
    private readonly Queue<string> upstream = new();
    private readonly StubHttpMessageHandler handler;

    public AiAgentRunnerTests()
    {
        handler = new StubHttpMessageHandler(_ => AiSse.Response(upstream.Dequeue()));
    }

    [Fact]
    public async Task ReadTool_ShouldRunLoop_RecordEachModelCall_WithSameRunId()
    {
        guard.Allowed.Add(SampleTools.Read.Name);
        upstream.Enqueue(AiSse.ResponsesToolCall("call_1", SampleTools.Read.Name, """{"keyword":"A"}"""));
        upstream.Enqueue(AiSse.Responses(["找到 1 筆。"]));
        var chat = await StartAsync();

        var updates = await CollectAsync(chat.SendAsync("找專案 A"));

        var result = Assert.Single(updates, update => update.Result is not null).Result!;
        Assert.True(result.Success);
        Assert.Equal("找到 1 筆。", result.Markdown);
        Assert.Contains(updates, update => update.ToolActivity is { Completed: false, DisplayName: "讀取" });
        Assert.Equal(["A"], sample.ReadCalls);

        // 工具呼叫迴圈：兩次模型呼叫各記一筆，共用同一個 RunId 與 ConversationId。
        Assert.Equal(2, usage.Entries.Count);
        Assert.All(usage.Entries, entry => Assert.True(entry.Success));
        var runId = Assert.Single(usage.Entries.Select(entry => entry.RunId).Distinct());
        Assert.NotNull(runId);
        Assert.Equal(2, callLog.Entries.Count);
        Assert.All(callLog.Entries, entry =>
        {
            Assert.Equal(runId, entry.RunId);
            Assert.Equal(chat.ConversationId, entry.ConversationId);
        });

        Assert.Equal([(AuditActions.Ai.ToolInvoked, SampleTools.Read.Name, true)], guard.Audits);

        // 0.9.149 起 Agent 走 Responses API、一律 store=false（Azure gpt-6.1-sol 在 Chat Completions 上不能帶工具）；
        // 用量照樣從 Responses 原文解析出輸入／輸出／快取／推理。
        Assert.All(handler.Requests, request => Assert.EndsWith("/responses", request.RequestUri!.AbsolutePath, StringComparison.Ordinal));
        Assert.All(handler.RequestBodies, body => Assert.Contains("\"store\":false", body, StringComparison.Ordinal));
        Assert.All(usage.Entries, entry =>
        {
            Assert.Equal(10, entry.InputCount);
            Assert.Equal(2, entry.OutputCount);
            Assert.Equal(4, entry.CachedInputCount);
            Assert.Equal(1, entry.ReasoningCount);
            Assert.Contains("input_tokens", entry.RawUsageJson, StringComparison.Ordinal);
        });
        Assert.All(callLog.Entries, entry => Assert.Equal(AiSse.Model, entry.Model));
    }

    [Fact]
    public async Task ReadTool_WithoutPermission_ShouldNotRun_AndTellModel()
    {
        upstream.Enqueue(AiSse.ResponsesToolCall("call_1", SampleTools.Read.Name, """{"keyword":"A"}"""));
        upstream.Enqueue(AiSse.Responses(["你沒有權限。"]));
        var chat = await StartAsync();

        var updates = await CollectAsync(chat.SendAsync("找專案 A"));

        Assert.True(updates[^1].Result!.Success);
        Assert.Empty(sample.ReadCalls);
        Assert.Contains(PermissionCheckedAIFunctionDeniedPrefix, handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.Equal([(AuditActions.Ai.ToolDenied, SampleTools.Read.Name, false)], guard.Audits);
    }

    [Fact]
    public async Task MutatingTool_ShouldPauseForApproval_ThenRunWhenApproved()
    {
        guard.Allowed.Add(SampleTools.Write.Name);
        upstream.Enqueue(AiSse.ResponsesToolCall("call_9", SampleTools.Write.Name, """{"value":"新內容"}"""));
        var chat = await StartAsync();

        var first = await CollectAsync(chat.SendAsync("把值改成新內容"));

        // 停下來等核准：沒有結果、工具還沒執行。
        Assert.DoesNotContain(first, update => update.Result is not null);
        var approval = Assert.Single(Assert.Single(first, update => update.Approvals is not null).Approvals!);
        Assert.Equal("寫入", approval.DisplayName);
        Assert.Contains(approval.Arguments, argument => argument.Key == "value" && argument.Value == "新內容");
        Assert.True(chat.IsAwaitingApproval);
        Assert.Empty(sample.WriteCalls);

        upstream.Enqueue(AiSse.Responses(["已修改。"]));
        var second = await CollectAsync(chat.RespondAsync([approval.RequestId]));

        Assert.True(second[^1].Result!.Success);
        Assert.Equal(["新內容"], sample.WriteCalls);
        Assert.False(chat.IsAwaitingApproval);
        Assert.Equal([(AuditActions.Ai.ToolInvoked, SampleTools.Write.Name, true)], guard.Audits);

        // 核准前後是兩次執行，各有自己的 RunId。
        Assert.Equal(2, usage.Entries.Select(entry => entry.RunId).Distinct().Count());
    }

    [Fact]
    public async Task MutatingTool_Rejected_ShouldNotRun_AndAuditRejection()
    {
        guard.Allowed.Add(SampleTools.Write.Name);
        upstream.Enqueue(AiSse.ResponsesToolCall("call_9", SampleTools.Write.Name, """{"value":"x"}"""));
        var chat = await StartAsync();
        await CollectAsync(chat.SendAsync("改"));

        upstream.Enqueue(AiSse.Responses(["好的，沒有修改。"]));
        var updates = await CollectAsync(chat.RespondAsync([]));

        Assert.True(updates[^1].Result!.Success);
        Assert.Empty(sample.WriteCalls);
        Assert.Equal([(AuditActions.Ai.ToolRejected, SampleTools.Write.Name, false)], guard.Audits);
    }

    /// <summary>沒有權限的異動工具不跳核准：直接拒絕、寫 ToolDenied，讓 agent 自己告訴使用者。</summary>
    [Fact]
    public async Task MutatingTool_WithoutPermission_ShouldBeRejectedWithoutAskingUser()
    {
        upstream.Enqueue(AiSse.ResponsesToolCall("call_9", SampleTools.Write.Name, """{"value":"x"}"""));
        upstream.Enqueue(AiSse.Responses(["你沒有權限修改。"]));
        var chat = await StartAsync();

        var updates = await CollectAsync(chat.SendAsync("改"));

        Assert.DoesNotContain(updates, update => update.Approvals is not null);
        Assert.True(updates[^1].Result!.Success);
        Assert.Equal("你沒有權限修改。", updates[^1].Result!.Markdown);
        Assert.Empty(sample.WriteCalls);
        Assert.False(chat.IsAwaitingApproval);
        Assert.Equal([(AuditActions.Ai.ToolDenied, SampleTools.Write.Name, false)], guard.Audits);
    }

    [Fact]
    public async Task SendAsync_WhileAwaitingApproval_ShouldThrow()
    {
        guard.Allowed.Add(SampleTools.Write.Name);
        upstream.Enqueue(AiSse.ResponsesToolCall("call_9", SampleTools.Write.Name, """{"value":"x"}"""));
        var chat = await StartAsync();
        await CollectAsync(chat.SendAsync("改"));

        Assert.Throws<InvalidOperationException>(() => chat.SendAsync("再問一次"));
    }

    [Fact]
    public async Task Tools_ShouldBeSentToModel()
    {
        upstream.Enqueue(AiSse.Responses(["你好"]));
        var chat = await StartAsync();

        await CollectAsync(chat.SendAsync("嗨"));

        var body = Assert.Single(handler.RequestBodies);
        Assert.Contains(SampleTools.Read.Name, body, StringComparison.Ordinal);
        Assert.Contains(SampleTools.Write.Name, body, StringComparison.Ordinal);
    }

    /// <summary>每次執行結束都存檔：畫面紀錄（使用者、工具、AI 回覆）、session 與自動標題。</summary>
    [Fact]
    public async Task Run_ShouldPersistTranscriptAndSession()
    {
        guard.Allowed.Add(SampleTools.Read.Name);
        upstream.Enqueue(AiSse.ResponsesToolCall("call_1", SampleTools.Read.Name, """{"keyword":"A"}"""));
        upstream.Enqueue(AiSse.Responses(["找到 1 筆。"]));
        var chat = await StartAsync();

        await CollectAsync(chat.SendAsync("找專案 A"));

        var saved = store.Saved[chat.ConversationId];
        Assert.Equal("找專案 A", saved.Title);
        Assert.Equal(SampleTools.Agent.Name, saved.AgentName);
        Assert.False(saved.HasPendingApproval);
        Assert.Equal(
            [(AiAgentTranscriptKinds.User, "找專案 A"), (AiAgentTranscriptKinds.Tool, "呼叫工具：讀取"), (AiAgentTranscriptKinds.Assistant, "找到 1 筆。")],
            saved.Content.Transcript.Select(entry => (entry.Kind, entry.Text)));
        Assert.Equal(System.Text.Json.JsonValueKind.Object, saved.Content.Session.ValueKind);
    }

    /// <summary>接續：另一個執行器（等於重新整理頁面）讀回存檔，模型收到的請求包含之前的對話。</summary>
    [Fact]
    public async Task Resume_ShouldRestoreHistory_SoModelRemembersEarlierTurns()
    {
        upstream.Enqueue(AiSse.Responses(["第一輪回覆"]));
        var first = await StartAsync();
        await CollectAsync(first.SendAsync("第一輪問題"));

        var resumed = await CreateRunner().ResumeAsync(SampleTools.Agent, first.ConversationId);

        Assert.NotNull(resumed);
        Assert.Equal(first.ConversationId, resumed.ConversationId);
        Assert.Equal(2, resumed.Transcript.Count);
        upstream.Enqueue(AiSse.Responses(["第二輪回覆"]));
        await CollectAsync(resumed.SendAsync("第二輪問題"));

        // ⚠️ store=false：雲端沒有保存前文，所以必須整段歷史重送，不能靠 previous_response_id（0.9.149 起走 Responses）。
        var body = handler.RequestBodies[^1];
        Assert.Contains("第一輪問題", body, StringComparison.Ordinal);
        Assert.Contains("第一輪回覆", body, StringComparison.Ordinal);
        Assert.Contains("第二輪問題", body, StringComparison.Ordinal);
        Assert.DoesNotContain("previous_response_id", body, StringComparison.Ordinal);
        Assert.Equal(4, store.Saved[first.ConversationId].Content.Transcript.Count);

        // 接續沿用原本的 ConversationId，AI 對話紀錄串得起來。
        Assert.All(callLog.Entries, entry => Assert.Equal(first.ConversationId, entry.ConversationId));
    }

    /// <summary>停在核准時離開：回來後仍在等核准，核准後工具照常執行。</summary>
    [Fact]
    public async Task Resume_WhileAwaitingApproval_ShouldAskAgain_AndRunWhenApproved()
    {
        guard.Allowed.Add(SampleTools.Write.Name);
        upstream.Enqueue(AiSse.ResponsesToolCall("call_9", SampleTools.Write.Name, """{"value":"新內容"}"""));
        var first = await StartAsync();
        await CollectAsync(first.SendAsync("把值改成新內容"));
        Assert.True(store.Saved[first.ConversationId].HasPendingApproval);

        var resumed = await CreateRunner().ResumeAsync(SampleTools.Agent, first.ConversationId);

        Assert.NotNull(resumed);
        Assert.True(resumed.IsAwaitingApproval);
        var approval = Assert.Single(resumed.PendingApprovals);
        Assert.Equal("寫入", approval.DisplayName);
        Assert.Contains(approval.Arguments, argument => argument.Key == "value" && argument.Value == "新內容");

        upstream.Enqueue(AiSse.Responses(["已修改。"]));
        var updates = await CollectAsync(resumed.RespondAsync([approval.RequestId]));

        Assert.True(updates[^1].Result!.Success);
        Assert.Equal(["新內容"], sample.WriteCalls);
        Assert.False(store.Saved[first.ConversationId].HasPendingApproval);
        Assert.Contains(resumed.Transcript, entry => entry.Kind == AiAgentTranscriptKinds.Notice && entry.Text == "你核准了：寫入");
    }

    /// <summary>接續時工具已不在這個 Agent 上（程式改版）：不請使用者核准，下一則訊息連同拒絕一起送回去。</summary>
    [Fact]
    public async Task Resume_WithRemovedTool_ShouldAutoRejectWithNextMessage()
    {
        guard.Allowed.Add(SampleTools.Write.Name);
        upstream.Enqueue(AiSse.ResponsesToolCall("call_9", SampleTools.Write.Name, """{"value":"x"}"""));
        var first = await StartAsync();
        await CollectAsync(first.SendAsync("改"));
        var readOnlyAgent = SampleTools.Agent with { Tools = [SampleTools.Read] };

        var resumed = await CreateRunner().ResumeAsync(readOnlyAgent, first.ConversationId);

        Assert.NotNull(resumed);
        Assert.False(resumed.IsAwaitingApproval);
        upstream.Enqueue(AiSse.Responses(["好的。"]));
        var updates = await CollectAsync(resumed.SendAsync("算了"));

        Assert.True(updates[^1].Result!.Success);
        Assert.Empty(sample.WriteCalls);
        Assert.Contains("這不是這個 Agent 可以使用的工具", handler.RequestBodies[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resume_ShouldReturnNull_WhenNotFoundOrAnotherAgent()
    {
        upstream.Enqueue(AiSse.Responses(["你好"]));
        var chat = await StartAsync();
        await CollectAsync(chat.SendAsync("嗨"));

        Assert.Null(await CreateRunner().ResumeAsync(SampleTools.Agent, Guid.NewGuid()));
        Assert.Null(await CreateRunner().ResumeAsync(SampleTools.Agent with { Name = "Other" }, chat.ConversationId));
    }

    /// <summary>只開新對話、還沒送出任何訊息：不存檔（清單不會多出空白對話）。</summary>
    [Fact]
    public async Task Start_WithoutSending_ShouldNotPersist()
    {
        await StartAsync();

        Assert.Empty(store.Saved);
    }

    /// <summary>串流途中停止：已收到的文字收進紀錄並標示不完整，照樣存檔。</summary>
    [Fact]
    public async Task Cancel_MidStream_ShouldKeepPartialReplyAndPersist()
    {
        upstream.Enqueue(AiSse.Responses(["第一段", "第二段"]));
        var chat = await StartAsync();
        using var cts = new CancellationTokenSource();

        var updates = new List<AiAgentUpdate>();
        await foreach (var update in chat.SendAsync("說個故事", cts.Token))
        {
            updates.Add(update);
            if (update.TextDelta.Length > 0)
            {
                cts.Cancel();
            }
        }

        Assert.Equal(AiAnalysisFailureReason.Canceled, updates[^1].Result!.Reason);
        var transcript = store.Saved[chat.ConversationId].Content.Transcript;
        Assert.Equal(AiAgentTranscriptKinds.Assistant, transcript[1].Kind);
        Assert.Equal(AiInterruption.Annotate("第一段"), transcript[1].Text);
        Assert.Equal((AiAgentTranscriptKinds.Notice, "已停止產生（上面的回覆不完整）。"), (transcript[^1].Kind, transcript[^1].Text));
    }

    /// <summary>每輪規劃器（系統管理助理，0.9.155 起）：每次送出前換一組指示、工具與標籤，session 延續。</summary>
    [Fact]
    public async Task Planner_EachSend_UsesPlannedInstructionsToolsAndLabel()
    {
        guard.Allowed.Add(SampleTools.Read.Name);
        var planner = new ScriptedPlanner(
            Turn([SampleTools.Read], "指示甲", "監控與診斷"),
            Turn([SampleTools.Read, SampleTools.Write], "指示乙", "帳號與權限"));
        upstream.Enqueue(AiSse.Responses(["第一輪回答"]));
        upstream.Enqueue(AiSse.Responses(["第二輪回答"]));
        var chat = await CreateRunner().StartAsync(planner);

        await CollectAsync(chat.SendAsync("第一則訊息"));
        await CollectAsync(chat.SendAsync("第二則訊息"));

        Assert.Contains("指示甲", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("write_things", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.Contains("指示乙", handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.Contains("write_things", handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.Contains("第一則訊息", handler.RequestBodies[1], StringComparison.Ordinal);

        var assistants = chat.Transcript.Where(entry => entry.Kind == AiAgentTranscriptKinds.Assistant).Select(entry => entry.Agent).ToList();
        Assert.Equal(["監控與診斷", "帳號與權限"], assistants);
        Assert.Equal("帳號與權限", chat.CurrentLabel);
        Assert.Equal(ScriptedPlanner.HomeDefinition.Name, store.Saved[chat.ConversationId].AgentName);
    }

    [Fact]
    public async Task Planner_ApprovalResponse_DoesNotReplan()
    {
        guard.Allowed.Add(SampleTools.Write.Name);
        var planner = new ScriptedPlanner(Turn([SampleTools.Read, SampleTools.Write], "指示", "帳號與權限"));
        upstream.Enqueue(AiSse.ResponsesToolCall("call_9", SampleTools.Write.Name, """{"value":"新內容"}"""));
        var chat = await CreateRunner().StartAsync(planner);
        var first = await CollectAsync(chat.SendAsync("改"));
        var approval = Assert.Single(Assert.Single(first, update => update.Approvals is not null).Approvals!);

        upstream.Enqueue(AiSse.Responses(["已修改。"]));
        var second = await CollectAsync(chat.RespondAsync([approval.RequestId]));

        Assert.True(second[^1].Result!.Success);
        Assert.Equal(1, planner.PlanCalls);
        Assert.Equal(["新內容"], sample.WriteCalls);
    }

    [Fact]
    public async Task Planner_Resume_UsesHomeTurn_AndRestoresPendingApproval()
    {
        guard.Allowed.Add(SampleTools.Write.Name);
        var planner = new ScriptedPlanner(Turn([SampleTools.Read, SampleTools.Write], "指示", "帳號與權限"));
        upstream.Enqueue(AiSse.ResponsesToolCall("call_9", SampleTools.Write.Name, """{"value":"接續後"}"""));
        var chat = await CreateRunner().StartAsync(planner);
        await CollectAsync(chat.SendAsync("改"));

        var resumed = await CreateRunner().ResumeAsync(planner, chat.ConversationId);

        Assert.NotNull(resumed);
        var approval = Assert.Single(resumed.PendingApprovals);
        Assert.Equal("寫入", approval.DisplayName);
        upstream.Enqueue(AiSse.Responses(["已修改。"]));
        await CollectAsync(resumed.RespondAsync([approval.RequestId]));
        Assert.Equal(["接續後"], sample.WriteCalls);
    }

    [Fact]
    public async Task Planner_TurnWithDifferentName_Throws()
    {
        var planner = new ScriptedPlanner(new AiAgentTurn(SampleTools.Agent with { Name = "別的Agent" }, "指示", null));
        var chat = await CreateRunner().StartAsync(planner);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(chat.SendAsync("你好")));
    }

    /// <summary>分派中按「停止產生」：不是錯誤，訊息留在紀錄、記「已停止」並存檔，不呼叫模型。</summary>
    [Fact]
    public async Task Planner_CanceledWhilePlanning_ReturnsCanceledResult_KeepsMessageAndPersists()
    {
        var planner = new BlockingPlanner();
        var chat = await CreateRunner().StartAsync(planner);
        using var cts = new CancellationTokenSource();

        var collecting = CollectAsync(chat.SendAsync("你好", cts.Token));
        await planner.Started.Task;
        cts.Cancel();
        var updates = await collecting;

        Assert.Equal(AiAnalysisFailureReason.Canceled, Assert.Single(updates).Result!.Reason);
        Assert.Empty(handler.Requests);
        var transcript = store.Saved[chat.ConversationId].Content.Transcript;
        Assert.Equal((AiAgentTranscriptKinds.User, "你好"), (transcript[0].Kind, transcript[0].Text));
        Assert.Equal((AiAgentTranscriptKinds.Notice, "已停止產生，尚未收到任何內容。"), (transcript[^1].Kind, transcript[^1].Text));
    }

    /// <summary>規劃時一直等到被取消（模擬分派呼叫還沒回來）。</summary>
    private sealed class BlockingPlanner : IAiAgentTurnPlanner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AiAgentDefinition Home => SampleTools.Agent;

        public Task<AiAgentTurn> HomeTurnAsync(CancellationToken cancellationToken)
            => Task.FromResult(new AiAgentTurn(Home, "首頁指示", null));

        public async Task<AiAgentTurn> PlanAsync(Guid conversationId, string message, IReadOnlyList<AiAgentTranscriptEntry> transcript, CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("不會到這裡");
        }
    }

    private static AiAgentTurn Turn(IReadOnlyList<AiToolDefinition> tools, string instructions, string? label)
        => new(SampleTools.Agent with { Tools = tools }, instructions, label);

    /// <summary>依序回傳預先排好的每一輪；接續與開對話時用含全部工具的 Home。</summary>
    private sealed class ScriptedPlanner(params AiAgentTurn[] turns) : IAiAgentTurnPlanner
    {
        private readonly Queue<AiAgentTurn> queue = new(turns);

        public static AiAgentDefinition HomeDefinition => SampleTools.Agent;

        public int PlanCalls { get; private set; }

        public AiAgentDefinition Home => HomeDefinition;

        public Task<AiAgentTurn> HomeTurnAsync(CancellationToken cancellationToken)
            => Task.FromResult(new AiAgentTurn(HomeDefinition, "首頁指示", null));

        public Task<AiAgentTurn> PlanAsync(Guid conversationId, string message, IReadOnlyList<AiAgentTranscriptEntry> transcript, CancellationToken cancellationToken)
        {
            PlanCalls++;
            return Task.FromResult(queue.Dequeue());
        }
    }

    /// <summary>測試替身看得到的「沒有權限」文字開頭（PermissionCheckedAIFunction 是 internal）。</summary>
    private const string PermissionCheckedAIFunctionDeniedPrefix = "使用者沒有執行這個操作的權限";

    private Task<AiAgentChat> StartAsync() => CreateRunner().StartAsync(SampleTools.Agent);

    /// <summary>每呼叫一次就是一個新的執行器（等於重新整理頁面），共用同一個上游、存檔與記帳替身。</summary>
    private AiAgentRunner CreateRunner()
    {
        var options = new StaticOptionsMonitor<AiSettings>(new AiSettings
        {
            Provider = nameof(AiProvider.OpenAI),
            ApiKey = "key",
            Model = "gpt-4o",
        });
        var factory = AiTestAgents.CreateFactory(
            new StubHttpClientFactory(handler),
            options,
            usage,
            new CurrentUserService { CurrentUser = new CurrentUser { Id = 7, Account = "support" } },
            callLog);

        var services = new ServiceCollection().AddSingleton(sample).BuildServiceProvider();
        return new AiAgentRunner(
            factory,
            new AiAgentToolFactory(services, guard),
            guard,
            new StubSystemPromptProvider(),
            options,
            new StaticOptionsMonitor<AiModelSettings>(new AiModelSettings()),
            store,
            NullLogger<AiAgentRunner>.Instance);
    }

    private static async Task<List<AiAgentUpdate>> CollectAsync(IAsyncEnumerable<AiAgentUpdate> stream)
    {
        var updates = new List<AiAgentUpdate>();
        await foreach (var update in stream)
        {
            updates.Add(update);
        }

        return updates;
    }

    /// <summary>記憶體版的對話存檔：存的時候經過一次 JSON 來回（同真的存檔）；標題只在第一次存時採用（同真的服務）。</summary>
    private sealed class InMemoryConversationStore : IAiAgentConversationStore
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
                    new AiAgentConversationSummary(saved.ConversationId, saved.AgentName, saved.Title, DateTime.Now, DateTime.Now, saved.HasPendingApproval),
                    saved.Content)
                : null);
    }

    /// <summary>可控的權限檢查：只有 <see cref="Allowed"/> 裡的工具可以執行；稽核只記下來。</summary>
    private sealed class FakeToolGuard : IAiToolPermissionGuard
    {
        public HashSet<string> Allowed { get; } = [];

        public List<(string Action, string Tool, bool Success)> Audits { get; } = [];

        public Task<bool> CanInvokeAsync(AiToolDefinition tool) => Task.FromResult(Allowed.Contains(tool.Name));

        public Task AuditAsync(string action, string agentName, AiToolDefinition tool, bool success, string? note = null)
        {
            Audits.Add((action, tool.Name, success));
            return Task.CompletedTask;
        }
    }

    /// <summary>兩支範例工具：唯讀「讀取」與會異動的「寫入」，只記下被呼叫的參數。</summary>
    private sealed class SampleTools
    {
        public List<string> ReadCalls { get; } = [];

        public List<string> WriteCalls { get; } = [];

        public static AiToolDefinition Read { get; } = new()
        {
            Name = "read_things",
            DisplayName = "讀取",
            Description = "讀取資料。",
            PermissionPage = "測試頁",
            PermissionAction = "view",
            Create = services => AIFunctionFactory.Create(services.GetRequiredService<SampleTools>().ReadAsync, "read_things", "讀取資料。"),
        };

        public static AiToolDefinition Write { get; } = new()
        {
            Name = "write_things",
            DisplayName = "寫入",
            Description = "寫入資料。",
            PermissionPage = "測試頁",
            PermissionAction = "edit",
            IsMutating = true,
            Create = services => AIFunctionFactory.Create(services.GetRequiredService<SampleTools>().WriteAsync, "write_things", "寫入資料。"),
        };

        public static AiAgentDefinition Agent { get; } = new()
        {
            Name = "TestAgent",
            DisplayName = "測試 Agent",
            Operation = "測試作業",
            PromptTemplateKey = MyProject.AccessDatas.Models.PromptTemplateKeys.AgentPlayground,
            Tools = [Read, Write],
        };

        public Task<string> ReadAsync([Description("關鍵字")] string keyword)
        {
            ReadCalls.Add(keyword);
            return Task.FromResult("[{\"id\":1,\"title\":\"A\"}]");
        }

        public Task<string> WriteAsync([Description("新的值")] string value)
        {
            WriteCalls.Add(value);
            return Task.FromResult("已寫入");
        }
    }
}
