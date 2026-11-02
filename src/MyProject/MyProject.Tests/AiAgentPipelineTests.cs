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
