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

    /// <summary>
    /// 核准窗的「目前狀態」（0.9.160 起）：工具的 ApprovalPreview 在提出核准時算一次，跟著待核准紀錄存起來，接續時照樣顯示（不重算）。
    /// </summary>
    [Fact]
    public async Task Approval_Preview_ShownPersistedAndRestored()
    {
        guard.Allowed.Add(SampleTools.Write.Name);
        var calls = 0;
        var write = SampleTools.Write with
        {
            ApprovalPreview = (_, arguments, _) =>
            {
                calls++;
                return Task.FromResult<string?>($"目前值 A；改為 {arguments["value"]}");
            },
        };
        var agent = SampleTools.Agent with { Tools = [SampleTools.Read, write] };
        upstream.Enqueue(AiSse.ResponsesToolCall("call_p", SampleTools.Write.Name, """{"value":"x"}"""));
        var first = await CreateRunner().StartAsync(agent);
        var updates = await CollectAsync(first.SendAsync("改"));
        Assert.Equal("目前值 A；改為 x", Assert.Single(Assert.Single(updates, update => update.Approvals is not null).Approvals!).Preview);

        var resumed = await CreateRunner().ResumeAsync(agent, first.ConversationId);

        Assert.Equal("目前值 A；改為 x", Assert.Single(resumed!.PendingApprovals).Preview);
        Assert.Equal(1, calls);
    }

    /// <summary>預覽失敗不能擋住核准：照樣跳核准窗，只是沒有「目前狀態」。</summary>
    [Fact]
    public async Task Approval_PreviewThrows_StillAsksWithoutPreview()
    {
        guard.Allowed.Add(SampleTools.Write.Name);
        var write = SampleTools.Write with { ApprovalPreview = (_, _, _) => throw new InvalidOperationException("boom") };
        var agent = SampleTools.Agent with { Tools = [SampleTools.Read, write] };
        upstream.Enqueue(AiSse.ResponsesToolCall("call_q", SampleTools.Write.Name, """{"value":"x"}"""));
        var first = await CreateRunner().StartAsync(agent);

        var updates = await CollectAsync(first.SendAsync("改"));

        var approval = Assert.Single(Assert.Single(updates, update => update.Approvals is not null).Approvals!);
        Assert.Null(approval.Preview);
        Assert.True(first.IsAwaitingApproval);
    }

    /// <summary>停在不可復原動作的核准時離開再回來（0.9.159 起）：接續後的核准請求仍標示不可復原（紅字與輸入確認才會出現）。</summary>
    [Fact]
    public async Task Resume_PendingIrreversibleApproval_KeepsFlag()
    {
        guard.Allowed.Add(SampleTools.Write.Name);
        var agent = SampleTools.Agent with { Tools = [SampleTools.Read, SampleTools.Write with { IsIrreversible = true }] };
        upstream.Enqueue(AiSse.ResponsesToolCall("call_x", SampleTools.Write.Name, """{"value":"x"}"""));
        var first = await CreateRunner().StartAsync(agent);
        var updates = await CollectAsync(first.SendAsync("刪掉"));
        Assert.True(Assert.Single(Assert.Single(updates, update => update.Approvals is not null).Approvals!).IsIrreversible);

        var resumed = await CreateRunner().ResumeAsync(agent, first.ConversationId);

        Assert.True(Assert.Single(resumed!.PendingApprovals).IsIrreversible);
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

    // ---------- 依對話選擇推論等級（0.9.170 起） ----------

    /// <summary>開對話時選的推論等級會送出；之後改了，下一則訊息起生效，前文照樣帶著。</summary>
    [Fact]
    public async Task ReasoningEffort_ChosenAtStart_AndChangedMidConversation()
    {
        upstream.Enqueue(AiSse.Responses(["第一輪回覆"]));
        upstream.Enqueue(AiSse.Responses(["第二輪回覆"]));
        var chat = await CreateRunner().StartAsync(SampleTools.Agent, reasoningEffort: "high");

        await CollectAsync(chat.SendAsync("第一輪問題"));
        chat.SetReasoningEffort("LOW");
        await CollectAsync(chat.SendAsync("第二輪問題"));

        Assert.Equal("high", EffortOf(handler.RequestBodies[0]));
        Assert.Equal("low", EffortOf(handler.RequestBodies[1]));
        Assert.Contains("第一輪回覆", handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.Equal("low", chat.ReasoningEffort);
        Assert.Equal("low", store.Saved[chat.ConversationId].Content.ReasoningEffort);
    }

    /// <summary>改回「系統預設」（null）就不再帶對話的值。</summary>
    [Fact]
    public async Task ReasoningEffort_SetBackToSystemDefault_ShouldNotSendIt()
    {
        upstream.Enqueue(AiSse.Responses(["回覆"]));
        var chat = await CreateRunner().StartAsync(SampleTools.Agent, reasoningEffort: "high");

        chat.SetReasoningEffort(null);
        await CollectAsync(chat.SendAsync("問題"));

        Assert.Null(EffortOf(handler.RequestBodies[0]));
        Assert.Null(store.Saved[chat.ConversationId].Content.ReasoningEffort);
    }

    /// <summary>接續時沿用那段對話存的推論等級；存的值不合法（例如手動改壞）就當作系統預設。</summary>
    [Theory]
    [InlineData("medium", "medium")]
    [InlineData("ultra", null)]
    public async Task ReasoningEffort_ShouldBeRestoredOnResume(string saved, string? expected)
    {
        upstream.Enqueue(AiSse.Responses(["第一輪回覆"]));
        var first = await CreateRunner().StartAsync(SampleTools.Agent, reasoningEffort: "medium");
        await CollectAsync(first.SendAsync("第一輪問題"));
        store.Saved[first.ConversationId].Content.ReasoningEffort = saved;

        var resumed = await CreateRunner().ResumeAsync(SampleTools.Agent, first.ConversationId);
        upstream.Enqueue(AiSse.Responses(["第二輪回覆"]));
        await CollectAsync(resumed!.SendAsync("第二輪問題"));

        Assert.Equal(expected, resumed.ReasoningEffort);
        Assert.Equal(expected, EffortOf(handler.RequestBodies[^1]));
    }

    /// <summary>
    /// 審查發現（0.9.170）：只改推論等級時，重建的 agent 要沿用開對話時的模型；
    /// 否則管理員途中改了模型，使用者一改推論等級就悄悄換模型，畫面上的「使用模型」卻還是舊的。
    /// </summary>
    [Fact]
    public async Task ReasoningEffort_ChangeShouldKeepTheModelChosenAtStart()
    {
        upstream.Enqueue(AiSse.Responses(["回覆"]));
        var chat = await StartAsync();
        runnerSettings.Model = "gpt-changed-later";

        chat.SetReasoningEffort("low");
        await CollectAsync(chat.SendAsync("問題"));

        using var document = System.Text.Json.JsonDocument.Parse(handler.RequestBodies[^1]);
        Assert.Equal("gpt-4o", document.RootElement.GetProperty("model").GetString());
        Assert.Equal("gpt-4o", chat.ModelName);
    }

    [Fact]
    public async Task ReasoningEffort_ShouldRejectUnknownValues_AndChangesWhileAwaitingApproval()
    {
        guard.Allowed.Add(SampleTools.Write.Name);
        upstream.Enqueue(AiSse.ResponsesToolCall("call_7", SampleTools.Write.Name, """{"value":"x"}"""));
        var chat = await StartAsync();

        Assert.Throws<ArgumentException>(() => chat.SetReasoningEffort("ultra"));
        await CollectAsync(chat.SendAsync("把值改成 x"));
        Assert.True(chat.IsAwaitingApproval);
        Assert.Throws<InvalidOperationException>(() => chat.SetReasoningEffort("high"));
    }

    private static string? EffortOf(string requestBody)
    {
        using var document = System.Text.Json.JsonDocument.Parse(requestBody);
        return document.RootElement.TryGetProperty("reasoning", out var reasoning) && reasoning.TryGetProperty("effort", out var effort)
            ? effort.GetString()
            : null;
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

    /// <summary>
    /// 0.10.7 起（WASM 版每個請求都重新接續）：存檔記得上次那一輪（TurnKey），接續停在核准的對話時重建那一輪，
    /// 核准後用提出核准時的工具與提示詞繼續，而不是開對話那一輪（全部工具＋全部領域）。不重新分派。
    /// </summary>
    [Fact]
    public async Task Planner_Resume_WithSavedTurnKey_ContinuesWithThatTurn()
    {
        guard.Allowed.Add(SampleTools.Write.Name);
        var planned = new AiAgentTurn(SampleTools.Agent with { Tools = [SampleTools.Read, SampleTools.Write] }, "領域指示", "帳號與權限", "accounts");
        var planner = new ScriptedPlanner(planned) { Restored = planned };
        upstream.Enqueue(AiSse.ResponsesToolCall("call_9", SampleTools.Write.Name, """{"value":"核准後"}"""));
        var chat = await CreateRunner().StartAsync(planner);
        await CollectAsync(chat.SendAsync("改"));
        Assert.Equal("accounts", store.Saved[chat.ConversationId].Content.TurnKey);

        var resumed = await CreateRunner().ResumeAsync(planner, chat.ConversationId);
        upstream.Enqueue(AiSse.Responses(["已修改。"]));
        await CollectAsync(resumed!.RespondAsync([Assert.Single(resumed.PendingApprovals).RequestId]));

        Assert.Equal(["accounts"], planner.RestoredKeys);
        Assert.Equal(1, planner.PlanCalls);
        Assert.Contains("領域指示", handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.DoesNotContain("首頁指示", handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.Equal(["核准後"], sample.WriteCalls);
        Assert.Equal("帳號與權限", resumed.Transcript[^1].Agent);
    }

    /// <summary>0.10.7 起：執行中每加一則紀錄就通知（API 以串流送給畫面）；接續還原的紀錄不通知。</summary>
    [Fact]
    public async Task EntryAdded_ShouldReportEachNewEntryInOrder()
    {
        guard.Allowed.Add(SampleTools.Read.Name);
        upstream.Enqueue(AiSse.Responses(["第一輪"]));
        var first = await StartAsync();
        await CollectAsync(first.SendAsync("嗨"));
        var resumed = await CreateRunner().ResumeAsync(SampleTools.Agent, first.ConversationId);
        var added = new List<AiAgentTranscriptEntry>();
        resumed!.EntryAdded = added.Add;
        upstream.Enqueue(AiSse.ResponsesToolCall("call_1", SampleTools.Read.Name, """{"keyword":"A"}"""));
        upstream.Enqueue(AiSse.Responses(["找到 1 筆。"]));

        await CollectAsync(resumed.SendAsync("找專案 A"));

        Assert.Equal(
            [(AiAgentTranscriptKinds.User, "找專案 A"), (AiAgentTranscriptKinds.Tool, "呼叫工具：讀取"), (AiAgentTranscriptKinds.Assistant, "找到 1 筆。")],
            added.Select(entry => (entry.Kind, entry.Text)));
        Assert.Equal(resumed.Transcript.Skip(2), added);
    }

    [Fact]
    public async Task Planner_TurnWithDifferentName_Throws()
    {
        var planner = new ScriptedPlanner(new AiAgentTurn(SampleTools.Agent with { Name = "別的Agent" }, "指示", null));
        var chat = await CreateRunner().StartAsync(planner);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(chat.SendAsync("你好")));
    }

    /// <summary>接續時從最後一則 AI 回覆還原「由誰回答」（0.9.157 起），核准回覆的那則才標得上。</summary>
    [Fact]
    public async Task Planner_Resume_RestoresCurrentLabelFromLastAssistant()
    {
        var planner = new ScriptedPlanner(Turn([SampleTools.Read], "指示", "帳號與權限"));
        upstream.Enqueue(AiSse.Responses(["回答"]));
        var chat = await CreateRunner().StartAsync(planner);
        await CollectAsync(chat.SendAsync("你好"));

        var resumed = await CreateRunner().ResumeAsync(planner, chat.ConversationId);

        Assert.Equal("帳號與權限", resumed!.CurrentLabel);
    }

    /// <summary>
    /// 停在核准的那一輪若模型直接呼叫工具、沒有先講話，就沒有 AI 回覆可取標籤（0.9.158 起）：
    /// 接續時改從最後一則有標籤的紀錄（工具活動也算）還原，核准回覆與核准窗才標得對；核准窗也帶出提出的專家。
    /// </summary>
    [Fact]
    public async Task Planner_Resume_LabelFromToolEntry_WhenTurnHadNoText()
    {
        guard.Allowed.Add(SampleTools.Write.Name);
        var planner = new ScriptedPlanner(
            Turn([SampleTools.Read], "指示", "監控與診斷"),
            Turn([SampleTools.Read, SampleTools.Write], "指示", "帳號與權限"));
        upstream.Enqueue(AiSse.Responses(["先回答一次"]));
        upstream.Enqueue(AiSse.ResponsesToolCall("call_9", SampleTools.Write.Name, """{"value":"x"}"""));
        var chat = await CreateRunner().StartAsync(planner);
        await CollectAsync(chat.SendAsync("第一則"));
        var second = await CollectAsync(chat.SendAsync("改"));
        Assert.Equal("帳號與權限", Assert.Single(Assert.Single(second, update => update.Approvals is not null).Approvals!).Label);

        var resumed = await CreateRunner().ResumeAsync(planner, chat.ConversationId);

        Assert.Equal("帳號與權限", resumed!.CurrentLabel);
        Assert.Equal("帳號與權限", Assert.Single(resumed.PendingApprovals).Label);
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

        /// <summary>接續時要重建的那一輪（0.10.7 起）；null 時退回 Home。</summary>
        public AiAgentTurn? Restored { get; init; }

        public List<string> RestoredKeys { get; } = [];

        public Task<AiAgentTurn> RestoreTurnAsync(string key, CancellationToken cancellationToken)
        {
            RestoredKeys.Add(key);
            return Restored is null ? HomeTurnAsync(cancellationToken) : Task.FromResult(Restored);
        }
    }

    /// <summary>測試替身看得到的「沒有權限」文字開頭（PermissionCheckedAIFunction 是 internal）。</summary>
    private const string PermissionCheckedAIFunctionDeniedPrefix = "使用者沒有執行這個操作的權限";

    private Task<AiAgentChat> StartAsync() => CreateRunner().StartAsync(SampleTools.Agent);

    /// <summary>所有執行器共用的 AI 設定（測試可以在對話途中改它，模擬管理員改系統參數）。</summary>
    private readonly AiSettings runnerSettings = new()
    {
        Provider = nameof(AiProvider.OpenAI),
        ApiKey = "key",
        Model = "gpt-4o",
    };

    /// <summary>每呼叫一次就是一個新的執行器（等於重新整理頁面），共用同一個上游、存檔與記帳替身。</summary>
    private AiAgentRunner CreateRunner()
    {
        var options = new StaticOptionsMonitor<AiSettings>(runnerSettings);
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
