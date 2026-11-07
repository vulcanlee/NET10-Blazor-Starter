using System.ComponentModel;
using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Models.Others;
using MyProject.Models.Systems;
using MyProject.Web.Ai;
using MyProject.Web.Ai.Agents;
using MyProject.Web.Ai.Agents.AdminAssistant;
using MyProject.Web.Configuration;

namespace MyProject.Tests;

/// <summary>
/// 定時巡檢的執行器（0.9.161 起）：沒有登入的使用者、經權限窄門只跑唯讀工具、不存對話、
/// 每次模型呼叫都記在「系統管理助理（排程巡檢）」並以報告編號當 ConversationId。
/// </summary>
public sealed class AdminPatrolRunnerTests
{
    private readonly SampleTools sample = new();
    private readonly RecordingTokenUsageRecorder usage = new();
    private readonly FakeAiCallLogRecorder callLog = new();
    private readonly RecordingAuditLogService audit = new();
    private readonly Queue<HttpResponseMessage> upstream = new();
    private readonly StubHttpMessageHandler handler;
    private readonly CurrentUserService currentUser = new();

    public AdminPatrolRunnerTests()
    {
        handler = new StubHttpMessageHandler(_ => upstream.Dequeue());
    }

    [Fact]
    public async Task Run_WithLoggedInUser_Throws_AndSendsNothing()
    {
        currentUser.CurrentUser = new CurrentUser { Id = 7, Account = "support", IsAdmin = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRunner().RunAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ReadTool_RunsWithoutUser_AndUsageIsRecordedUnderPatrol()
    {
        var reportId = Guid.NewGuid();
        upstream.Enqueue(AiSse.Response(AiSse.ResponsesToolCall("call_1", SampleTools.Read.Name, """{"keyword":"A"}""")));
        upstream.Enqueue(AiSse.Response(AiSse.Responses(["## 總結\n", "正常。"])));

        var result = await CreateRunner().RunCoreAsync(Turn(), [SampleTools.Read.Name, SampleTools.Write.Name], reportId, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("## 總結\n正常。", result.Markdown);
        Assert.Equal(AiSse.Model, result.ModelName);
        Assert.False(result.IsTruncatedByLength);
        Assert.Equal(["A"], sample.ReadCalls);

        Assert.Equal(2, usage.Entries.Count);
        Assert.All(usage.Entries, entry =>
        {
            Assert.Equal(TokenUsageOperations.AdminAssistantPatrol, entry.Operation);
            Assert.Null(entry.UserId);
        });
        Assert.Single(usage.Entries.Select(entry => entry.RunId).Distinct());
        Assert.All(callLog.Entries, entry => Assert.Equal(reportId, entry.ConversationId));

        var invoked = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.Ai.ToolInvoked, invoked.Action);
        Assert.Null(invoked.ActorUserId);
        Assert.EndsWith("排程巡檢", invoked.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolOutsideAllowedList_IsDenied_AndModelIsTold()
    {
        upstream.Enqueue(AiSse.Response(AiSse.ResponsesToolCall("call_1", SampleTools.Write.Name, """{"value":"x"}""")));
        upstream.Enqueue(AiSse.Response(AiSse.Responses(["沒有權限。"])));

        var result = await CreateRunner().RunCoreAsync(TurnWithUnwrappedWrite(), [SampleTools.Read.Name], Guid.NewGuid(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(sample.WriteCalls);
        Assert.Contains("使用者沒有執行這個操作的權限", handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.Equal(AuditActions.Ai.ToolDenied, Assert.Single(audit.Entries).Action);
    }

    [Fact]
    public async Task ApprovalRequest_FailsTheRun()
    {
        upstream.Enqueue(AiSse.Response(AiSse.ResponsesToolCall("call_1", SampleTools.Write.Name, """{"value":"x"}""")));

        var result = await CreateRunner().RunCoreAsync(Turn(), [SampleTools.Read.Name], Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(sample.WriteCalls);
    }

    [Fact]
    public async Task UpstreamError_ReturnsFailure()
    {
        upstream.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") });

        var result = await CreateRunner().RunCoreAsync(Turn(), [SampleTools.Read.Name], Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    private static AiAgentTurn Turn() => new(SampleTools.Agent, "你是巡檢助理。", "巡檢");

    /// <summary>
    /// 測窄門用：把「寫入」當成不需核准的工具交給模型（不包核准外殼），確認不在允許清單裡的工具會被窄門擋下、
    /// 模型收到「沒有權限」。「會異動但名稱在清單裡」的情況見 <see cref="ScheduledPatrolToolGuardTests"/>。
    /// </summary>
    private static AiAgentTurn TurnWithUnwrappedWrite()
        => new(SampleTools.Agent with { Tools = [SampleTools.Read, SampleTools.Write with { IsMutating = false }] }, "你是巡檢助理。", "巡檢");

    private AdminPatrolRunner CreateRunner()
    {
        var options = new StaticOptionsMonitor<AiSettings>(new AiSettings
        {
            Provider = nameof(AiProvider.OpenAI),
            ApiKey = "key",
            Model = "gpt-4o",
        });
        var factory = AiTestAgents.CreateFactory(new StubHttpClientFactory(handler), options, usage, currentUser, callLog);
        var services = new ServiceCollection().AddSingleton(sample).BuildServiceProvider();
        var guard = new AiToolPermissionGuard(new DenyAllPermissionChecker(), currentUser, audit);
        var planner = new AdminAssistantTurnPlanner(
            new NeverCalledCompletionClient(),
            new StubSystemPromptProvider(),
            new StaticOptionsMonitor<AiModelSettings>(new AiModelSettings()),
            NullLogger<AdminAssistantTurnPlanner>.Instance);

        return new AdminPatrolRunner(factory, services, guard, planner, options, currentUser, NullLogger<AdminPatrolRunner>.Instance);
    }

    private sealed class DenyAllPermissionChecker : IPermissionChecker
    {
        public Task<bool> HasPermissionAsync(int userId, string permissionKey) => Task.FromResult(false);

        public Task<IReadOnlyCollection<string>> GetEffectivePermissionKeysAsync(int userId) => Task.FromResult<IReadOnlyCollection<string>>([]);
    }

    private sealed class NeverCalledCompletionClient : IAiChatCompletionClient
    {
        public Task<AiAnalysisResult> CompleteAsync(AiChatCompletionRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("巡檢不應呼叫分派模型");

        public IAsyncEnumerable<AiStreamUpdate> StreamAsync(AiChatCompletionRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
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
            Name = AdminAssistantAgent.Name,
            DisplayName = AdminAssistantAgent.DisplayName,
            Operation = TokenUsageOperations.AdminAssistantPatrol,
            PromptTemplateKey = MyProject.AccessDatas.Models.PromptTemplateKeys.AdminAssistant,
            Tools = [Read, Write],
        };

        public Task<string> ReadAsync([Description("關鍵字")] string keyword)
        {
            ReadCalls.Add(keyword);
            return Task.FromResult("[{\"id\":1}]");
        }

        public Task<string> WriteAsync([Description("新的值")] string value)
        {
            WriteCalls.Add(value);
            return Task.FromResult("已寫入");
        }
    }
}
