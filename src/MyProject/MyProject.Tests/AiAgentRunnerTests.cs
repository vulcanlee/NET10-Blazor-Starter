using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Models.Others;
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
        upstream.Enqueue(AiSse.ToolCall("call_1", SampleTools.Read.Name, """{"keyword":"A"}"""));
        upstream.Enqueue(AiSse.Build(["找到 1 筆。"]));
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
    }

    [Fact]
    public async Task ReadTool_WithoutPermission_ShouldNotRun_AndTellModel()
    {
        upstream.Enqueue(AiSse.ToolCall("call_1", SampleTools.Read.Name, """{"keyword":"A"}"""));
        upstream.Enqueue(AiSse.Build(["你沒有權限。"]));
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
        upstream.Enqueue(AiSse.ToolCall("call_9", SampleTools.Write.Name, """{"value":"新內容"}"""));
        var chat = await StartAsync();

        var first = await CollectAsync(chat.SendAsync("把值改成新內容"));

        // 停下來等核准：沒有結果、工具還沒執行。
        Assert.DoesNotContain(first, update => update.Result is not null);
        var approval = Assert.Single(Assert.Single(first, update => update.Approvals is not null).Approvals!);
        Assert.Equal("寫入", approval.DisplayName);
        Assert.Contains(approval.Arguments, argument => argument.Key == "value" && argument.Value == "新內容");
        Assert.True(chat.IsAwaitingApproval);
        Assert.Empty(sample.WriteCalls);

        upstream.Enqueue(AiSse.Build(["已修改。"]));
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
        upstream.Enqueue(AiSse.ToolCall("call_9", SampleTools.Write.Name, """{"value":"x"}"""));
        var chat = await StartAsync();
        await CollectAsync(chat.SendAsync("改"));

        upstream.Enqueue(AiSse.Build(["好的，沒有修改。"]));
        var updates = await CollectAsync(chat.RespondAsync([]));

        Assert.True(updates[^1].Result!.Success);
        Assert.Empty(sample.WriteCalls);
        Assert.Equal([(AuditActions.Ai.ToolRejected, SampleTools.Write.Name, false)], guard.Audits);
    }

    /// <summary>沒有權限的異動工具不跳核准：直接拒絕、寫 ToolDenied，讓 agent 自己告訴使用者。</summary>
    [Fact]
    public async Task MutatingTool_WithoutPermission_ShouldBeRejectedWithoutAskingUser()
    {
        upstream.Enqueue(AiSse.ToolCall("call_9", SampleTools.Write.Name, """{"value":"x"}"""));
        upstream.Enqueue(AiSse.Build(["你沒有權限修改。"]));
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
        upstream.Enqueue(AiSse.ToolCall("call_9", SampleTools.Write.Name, """{"value":"x"}"""));
        var chat = await StartAsync();
        await CollectAsync(chat.SendAsync("改"));

        Assert.Throws<InvalidOperationException>(() => chat.SendAsync("再問一次"));
    }

    [Fact]
    public async Task Tools_ShouldBeSentToModel()
    {
        upstream.Enqueue(AiSse.Build(["你好"]));
        var chat = await StartAsync();

        await CollectAsync(chat.SendAsync("嗨"));

        var body = Assert.Single(handler.RequestBodies);
        Assert.Contains(SampleTools.Read.Name, body, StringComparison.Ordinal);
        Assert.Contains(SampleTools.Write.Name, body, StringComparison.Ordinal);
    }

    /// <summary>測試替身看得到的「沒有權限」文字開頭（PermissionCheckedAIFunction 是 internal）。</summary>
    private const string PermissionCheckedAIFunctionDeniedPrefix = "使用者沒有執行這個操作的權限";

    private async Task<AiAgentChat> StartAsync()
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
        var runner = new AiAgentRunner(
            factory,
            new AiAgentToolFactory(services, guard),
            guard,
            new StubSystemPromptProvider(),
            options,
            new StaticOptionsMonitor<AiModelSettings>(new AiModelSettings()),
            NullLogger<AiAgentRunner>.Instance);

        return await runner.StartAsync(SampleTools.Agent);
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
