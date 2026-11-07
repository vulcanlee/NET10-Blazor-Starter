using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Others;
using MyProject.Models.Systems;
using MyProject.Web.Ai;
using MyProject.Web.Configuration;

namespace MyProject.Tests;

/// <summary>
/// 0.9.140 起所有 LLM 呼叫改走 MAF agent 管線（<see cref="AiAgentFactory"/>）。
/// 既有的分析與探測測試證明「行為不變」；這裡守的是換成 SDK 之後<b>才會出現的風險</b>：
/// SDK 預設的重試與逾時、Azure 的認證標頭、以及 SDK 產生的請求 body 仍能被 AI 對話紀錄頁解析。
/// </summary>
public sealed class AiAgentPipelineTests
{
    private const string SuccessPayload = """
        {
          "model": "gpt-4o-2024-11-20",
          "choices": [ { "finish_reason": "stop", "message": { "content": "ok" } } ],
          "usage": { "prompt_tokens": 10, "completion_tokens": 2, "total_tokens": 12 }
        }
        """;

    private const string ResponsesSuccessPayload = """
        {
          "id": "resp_1", "object": "response", "created_at": 1, "status": "completed", "model": "gpt-6.1-sol",
          "output": [ { "id": "msg_1", "type": "message", "status": "completed", "role": "assistant",
                        "content": [ { "type": "output_text", "annotations": [], "text": "ok" } ] } ],
          "store": false,
          "usage": { "input_tokens": 10, "output_tokens": 2, "total_tokens": 12 }
        }
        """;

    /// <summary>Azure 改送 api-key，SDK 預設的 Authorization 必須拿掉，金鑰才不會同時出現在兩個標頭。</summary>
    [Fact]
    public async Task Azure_ShouldSendApiKeyHeader_AndNoAuthorizationHeader()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, SuccessPayload);
        var client = CreateClient(handler, AzureSettings());

        var result = await client.CompleteAsync(Request());

        Assert.True(result.Success);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("key", request.Headers.GetValues(AzureApiKeyPolicy.HeaderName).Single());
        Assert.False(request.Headers.Contains("Authorization"));
    }

    /// <summary>
    /// SDK 預設對 408／429／5xx 自動重試 3 次。每次重試上游都可能計費，而且記帳只會看到一筆 ——
    /// 這正是「看不到的費用」，必須確定只打一次。
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task UpstreamFailure_ShouldNotBeRetriedBySdk(HttpStatusCode status)
    {
        var handler = StubHttpMessageHandler.Json(status, """{ "error": { "code": "x" } }""");
        var recorder = new RecordingTokenUsageRecorder();
        var client = CreateClient(handler, AzureSettings(), recorder);

        var result = await client.CompleteAsync(Request());

        Assert.False(result.Success);
        Assert.Equal(1, handler.CallCount);
        Assert.Single(recorder.Entries);
    }

    /// <summary>逾時由記帳中介層控管：時間到就放棄、記一筆 Timeout，而不是等 SDK 自己的 100 秒。</summary>
    [Fact]
    public async Task SlowUpstream_ShouldTimeOutWithOwnTimeout_AndRecord()
    {
        var recorder = new RecordingTokenUsageRecorder();
        var callLog = new FakeAiCallLogRecorder();
        var factory = AiTestAgents.CreateFactory(
            new StubHttpClientFactory(new DelayingHandler(TimeSpan.FromSeconds(30))),
            new StaticOptionsMonitor<AiSettings>(AzureSettings()),
            recorder,
            User(),
            callLog);
        var handle = factory.CreateAgent(new AiCallContext
        {
            Operation = TokenUsageOperations.AiLogAnalysis,
            Timeout = TimeSpan.FromMilliseconds(200),
            TimeoutHint = "請稍後再試。",
        });

        var failure = await Assert.ThrowsAsync<AiCallException>(
            () => handle.Agent.RunAsync([new ChatMessage(ChatRole.User, "q")]));

        Assert.Equal(AiAnalysisFailureReason.Timeout, failure.Reason);
        Assert.Contains("0 秒", failure.Message, StringComparison.Ordinal);
        Assert.Equal(nameof(AiAnalysisFailureReason.Timeout), Assert.Single(recorder.Entries).FailureReason);
        Assert.Equal(nameof(AiAnalysisFailureReason.Timeout), Assert.Single(callLog.Entries).FailureReason);
    }

    /// <summary>
    /// AI 對話紀錄頁與 PDF 用 <see cref="AiCallLogMessages.Parse"/> 讀回請求 body。
    /// body 改由 SDK 產生之後，仍必須讀得出原本的角色與內容。
    /// </summary>
    [Fact]
    public async Task CallLog_RequestBodyFromSdk_ShouldStillParseAsChatMessages()
    {
        var callLog = new FakeAiCallLogRecorder();
        var client = CreateClient(StubHttpMessageHandler.Json(HttpStatusCode.OK, SuccessPayload), AzureSettings(), callLog: callLog);

        await client.CompleteAsync(Request(
            new AiChatMessage(AiChatRoles.System, "系統"),
            new AiChatMessage(AiChatRoles.User, "問題"),
            new AiChatMessage(AiChatRoles.Assistant, "回答"),
            new AiChatMessage(AiChatRoles.User, "追問")));

        var logged = Assert.Single(callLog.Entries);
        Assert.Equal("https://contoso.openai.azure.com/openai/v1/chat/completions", logged.Endpoint);
        var messages = AiCallLogMessages.Parse(logged.RequestBody);
        Assert.NotNull(messages);
        Assert.Equal(["system", "user", "assistant", "user"], messages!.Select(message => message.Role).ToList());
        Assert.Equal(["系統", "問題", "回答", "追問"], messages.Select(message => message.Content).ToList());
        Assert.DoesNotContain("key", logged.RequestBody, StringComparison.Ordinal);
    }

    /// <summary>健康探測不受用量上限影響（0.9.139 以前就是如此）：被擋下會讓健康頁誤報 AI 服務故障。</summary>
    [Fact]
    public async Task HealthProbe_ShouldIgnoreQuota()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, SuccessPayload);
        var quota = new StubAiQuotaService
        {
            Block = new AiQuotaLimitStatus(AiQuotaScope.User, AiQuotaPeriod.Daily, 50, 51.25, new DateTime(2026, 10, 4), new DateTime(2026, 10, 5)),
        };
        var options = new StaticOptionsMonitor<AiSettings>(AzureSettings());
        var probe = new AiHealthProbe(
            NullLogger<AiHealthProbe>.Instance,
            options,
            AiTestAgents.CreateFactory(
                new StubHttpClientFactory(handler), options, new RecordingTokenUsageRecorder(), User(), new FakeAiCallLogRecorder(), quota));

        var result = await probe.ProbeAsync();

        Assert.True(result.Success);
        Assert.Equal(1, handler.CallCount);
        Assert.Empty(quota.Checked);
    }

    /// <summary>
    /// 0.9.149 起 Agent 功能（<see cref="AiCallContext.UseResponsesApi"/>）走 Responses API：
    /// 一律 <c>store=false</c>（對話不留雲端），<c>AiSettings:AgentReasoningEffort</c> 有設才送 <c>reasoning.effort</c>。
    /// 實際故障：Azure gpt-6.1-sol 在 Chat Completions 上帶工具一律 400（不送會因推論被拒、送 none 不被接受）。
    /// 分析功能仍走 Chat Completions，<b>永遠不送</b> reasoning_effort（回到 0.9.145 的行為）。
    /// </summary>
    [Theory]
    [InlineData(null, true, null)]
    [InlineData("none", true, "none")]
    [InlineData("HIGH", true, "high")]
    [InlineData("xhigh", true, "xhigh")]
    [InlineData("high", false, null)]
    public async Task AgentReasoningEffort_ShouldOnlyBeSent_ByResponsesAgents(string? setting, bool useResponses, string? expected)
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, useResponses ? ResponsesSuccessPayload : SuccessPayload);
        var settings = AzureSettings();
        settings.AgentReasoningEffort = setting;
        var factory = AiTestAgents.CreateFactory(
            new StubHttpClientFactory(handler),
            new StaticOptionsMonitor<AiSettings>(settings),
            new RecordingTokenUsageRecorder(),
            User(),
            new FakeAiCallLogRecorder());
        var handle = factory.CreateAgent(
            new AiCallContext
            {
                Operation = TokenUsageOperations.AgentPlayground,
                Timeout = TimeSpan.FromSeconds(30),
                TimeoutHint = "請稍後再試。",
                UseResponsesApi = useResponses,
            },
            tools: [AIFunctionFactory.Create(() => "ok", "read_things", "讀取資料。")]);

        var response = await handle.Agent.RunAsync([new ChatMessage(ChatRole.User, "q")]);

        Assert.Equal("ok", response.Text);
        var request = Assert.Single(handler.Requests);
        Assert.EndsWith(useResponses ? "/responses" : "/chat/completions", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        using var document = System.Text.Json.JsonDocument.Parse(handler.RequestBodies.Single());
        var root = document.RootElement;
        Assert.True(root.TryGetProperty("tools", out _));
        Assert.False(root.TryGetProperty("reasoning_effort", out _));
        if (useResponses)
        {
            Assert.False(root.GetProperty("store").GetBoolean());
        }

        if (expected is null)
        {
            Assert.False(root.TryGetProperty("reasoning", out var reasoning) && reasoning.TryGetProperty("effort", out _));
        }
        else
        {
            Assert.Equal(expected, root.GetProperty("reasoning").GetProperty("effort").GetString());
        }
    }

    /// <summary>
    /// 0.9.168 起各 Agent 功能可在系統參數設自己的推論等級（<see cref="AiReasoningSettings"/>，以 <c>ModelKey</c> 對應），
    /// 空白時沿用 <c>AiSettings:AgentReasoningEffort</c>；分析功能（Chat Completions）有設定也不送。
    /// </summary>
    [Theory]
    [InlineData("low", "high", true, "low")]
    [InlineData("", "high", true, "high")]
    [InlineData("", null, true, null)]
    [InlineData("xhigh", null, true, "xhigh")]
    [InlineData("low", "high", false, null)]
    public async Task FeatureReasoningEffort_ShouldOverrideTheGlobalValue(string feature, string? global, bool useResponses, string? expected)
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, useResponses ? ResponsesSuccessPayload : SuccessPayload);
        var settings = AzureSettings();
        settings.AgentReasoningEffort = global;
        var factory = CreateFactory(handler, settings, new AiReasoningSettings { AgentPlayground = feature, AdminAssistant = "medium" });
        var handle = factory.CreateAgent(PlaygroundContext(useResponses));

        await handle.Agent.RunAsync([new ChatMessage(ChatRole.User, "q")]);

        using var document = System.Text.Json.JsonDocument.Parse(handler.RequestBodies.Single());
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("reasoning_effort", out _));
        if (expected is null)
        {
            Assert.False(root.TryGetProperty("reasoning", out var reasoning) && reasoning.TryGetProperty("effort", out _));
        }
        else
        {
            Assert.Equal(expected, root.GetProperty("reasoning").GetProperty("effort").GetString());
        }
    }

    /// <summary>
    /// 推論等級被模型拒絕時，訊息要指出是哪個設定：用到功能自己的值就指名系統參數，空白沿用時指名 <c>AiSettings:AgentReasoningEffort</c>。
    /// </summary>
    [Theory]
    [InlineData("none", "high", "系統參數「Agent 測試台的推論等級」")]
    [InlineData("", "none", "AiSettings:AgentReasoningEffort")]
    public async Task RejectedReasoningEffort_ShouldNameTheSettingThatWasUsed(string feature, string? global, string expectedSource)
    {
        const string payload = """
            {
              "error": {
                "message": "Unsupported value: 'reasoning.effort' does not support 'none' with this model.",
                "type": "invalid_request_error",
                "param": "reasoning.effort",
                "code": "unsupported_value"
              }
            }
            """;
        var settings = AzureSettings();
        settings.AgentReasoningEffort = global;
        var factory = CreateFactory(
            StubHttpMessageHandler.Json(HttpStatusCode.BadRequest, payload), settings, new AiReasoningSettings { AgentPlayground = feature });
        var handle = factory.CreateAgent(PlaygroundContext(useResponses: true));

        var exception = await Assert.ThrowsAsync<AiCallException>(() => handle.Agent.RunAsync([new ChatMessage(ChatRole.User, "q")]));

        Assert.Contains(expectedSource, exception.Message, StringComparison.Ordinal);
        Assert.Contains("「none」", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Agent 測試台依對話選的推論等級（0.9.170 起，<see cref="AiCallContext.ReasoningEffortOverride"/>）優先於系統參數與全域值；
    /// 被拒時訊息請使用者在對話上方改選，而不是去改設定。
    /// </summary>
    [Fact]
    public async Task ConversationReasoningEffort_ShouldWin_AndBeNamedWhenRejected()
    {
        var okHandler = StubHttpMessageHandler.Json(HttpStatusCode.OK, ResponsesSuccessPayload);
        var settings = AzureSettings();
        settings.AgentReasoningEffort = "medium";
        var factory = CreateFactory(okHandler, settings, new AiReasoningSettings { AgentPlayground = "low" });

        await factory.CreateAgent(PlaygroundContext(useResponses: true) with { ReasoningEffortOverride = "XHIGH" })
            .Agent.RunAsync([new ChatMessage(ChatRole.User, "q")]);

        using (var document = System.Text.Json.JsonDocument.Parse(okHandler.RequestBodies.Single()))
        {
            Assert.Equal("xhigh", document.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        }

        const string payload = """
            {
              "error": {
                "message": "Unsupported value: 'reasoning.effort' does not support 'none' with this model.",
                "type": "invalid_request_error",
                "param": "reasoning.effort",
                "code": "unsupported_value"
              }
            }
            """;
        var rejecting = CreateFactory(
            StubHttpMessageHandler.Json(HttpStatusCode.BadRequest, payload), settings, new AiReasoningSettings { AgentPlayground = "low" });
        var handle = rejecting.CreateAgent(PlaygroundContext(useResponses: true) with { ReasoningEffortOverride = "none" });

        var exception = await Assert.ThrowsAsync<AiCallException>(() => handle.Agent.RunAsync([new ChatMessage(ChatRole.User, "q")]));

        Assert.Contains("「none」（這段對話選的推論等級）", exception.Message, StringComparison.Ordinal);
        Assert.Contains("對話上方", exception.Message, StringComparison.Ordinal);
    }

    private static AiCallContext PlaygroundContext(bool useResponses) => new()
    {
        Operation = TokenUsageOperations.AgentPlayground,
        Timeout = TimeSpan.FromSeconds(30),
        TimeoutHint = "請稍後再試。",
        UseResponsesApi = useResponses,
        ModelKey = nameof(AiModelSettings.AgentPlayground),
    };

    private static AiAgentFactory CreateFactory(StubHttpMessageHandler handler, AiSettings settings, AiReasoningSettings reasoning)
        => AiTestAgents.CreateFactory(
            new StubHttpClientFactory(handler),
            new StaticOptionsMonitor<AiSettings>(settings),
            new RecordingTokenUsageRecorder(),
            User(),
            new FakeAiCallLogRecorder(),
            reasoningOptions: new StaticOptionsMonitor<AiReasoningSettings>(reasoning));

    private static AiChatCompletionRequest Request(params AiChatMessage[] messages) => new()
    {
        Operation = TokenUsageOperations.AiLogAnalysis,
        Messages = messages.Length == 0 ? [new AiChatMessage(AiChatRoles.User, "q")] : messages,
        ContextLengthExceededMessage = "too long",
        TimeoutHint = "請稍後再試。",
        SubmittedContentLabel = "送出的內容",
    };

    private static AiSettings AzureSettings() => new()
    {
        Provider = nameof(AiProvider.AzureOpenAI),
        Endpoint = "https://contoso.openai.azure.com/openai/v1",
        ApiKey = "key",
        Model = "my-deployment",
    };

    private static CurrentUserService User() => new() { CurrentUser = new CurrentUser { Id = 7, Account = "support" } };

    private static AiChatCompletionClient CreateClient(
        StubHttpMessageHandler handler,
        AiSettings settings,
        RecordingTokenUsageRecorder? recorder = null,
        FakeAiCallLogRecorder? callLog = null)
    {
        var options = new StaticOptionsMonitor<AiSettings>(settings);
        return AiTestAgents.CreateChatClient(
            NullLogger<AiChatCompletionClient>.Instance,
            new StubHttpClientFactory(handler),
            options,
            recorder ?? new RecordingTokenUsageRecorder(),
            User(),
            callLog ?? new FakeAiCallLogRecorder(),
            new StubAiQuotaService(),
            new RecordingAuditLogService());
    }

    /// <summary>一直等到被取消為止的上游（模擬卡住的端點）。</summary>
    private sealed class DelayingHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SuccessPayload, Encoding.UTF8, "application/json"),
            };
        }
    }
}
