using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Others;
using MyProject.Models.Systems;
using MyProject.Web.Ai;
using MyProject.Web.Configuration;

namespace MyProject.Tests;

/// <summary>
/// 系統健康監控頁的 LLM 探測。
///
/// 重點在兩件事：未設定時絕不發請求也不記帳（否則沒接 AI 的部署會被記一堆空帳），
/// 以及失敗時不可以把例外丟出去（健康檢查的單一項目不能炸掉整份報告）。
/// </summary>
public sealed class AiHealthProbeTests
{
    private const string SuccessPayload = """
    {
      "model": "gpt-5.6-sol",
      "choices": [ { "message": { "content": "hello" }, "finish_reason": "stop" } ],
      "usage": { "prompt_tokens": 12, "completion_tokens": 3, "total_tokens": 15 }
    }
    """;

    [Fact]
    public async Task ProbeAsync_NotConfigured_ShouldNotCallApiAndNotRecord()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, SuccessPayload);
        var (probe, recorder) = CreateProbe(new AiSettings(), handler);

        var result = await probe.ProbeAsync();

        Assert.False(result.IsConfigured);
        Assert.False(result.Success);
        Assert.Equal(0, handler.CallCount);
        Assert.Empty(recorder.Entries);
    }

    [Fact]
    public async Task ProbeAsync_Success_ShouldSendHelloAndRecordUsage()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, SuccessPayload);
        var (probe, recorder) = CreateProbe(CreateConfiguredSettings(), handler);

        var result = await probe.ProbeAsync();

        Assert.True(result.IsConfigured);
        Assert.True(result.Success);
        Assert.Equal("gpt-5.6-sol", result.ModelName);
        Assert.Equal(1, handler.CallCount);
        Assert.Contains("hello", handler.RequestBodies[0], StringComparison.Ordinal);

        var entry = Assert.Single(recorder.Entries);
        Assert.Equal(TokenUsageOperations.SystemHealthCheck, entry.Operation);
        Assert.Equal(TokenUsageCallKinds.Chat, entry.CallKind);
        Assert.True(entry.Success);
        Assert.Equal(12, entry.InputCount);
        Assert.Equal(3, entry.OutputCount);
    }

    [Fact]
    public async Task ProbeAsync_UpstreamError_ShouldRecordFailureAndNotThrow()
    {
        var handler = StubHttpMessageHandler.Json(
            HttpStatusCode.InternalServerError,
            """{ "error": { "code": "server_error", "message": "boom" } }""");
        var (probe, recorder) = CreateProbe(CreateConfiguredSettings(), handler);

        var result = await probe.ProbeAsync();

        Assert.True(result.IsConfigured);
        Assert.False(result.Success);
        Assert.Contains("500", result.Message, StringComparison.Ordinal);

        var entry = Assert.Single(recorder.Entries);
        Assert.False(entry.Success);
        Assert.Equal(TokenUsageOperations.SystemHealthCheck, entry.Operation);
    }

    [Fact]
    public async Task ProbeAsync_TransportFailure_ShouldReturnFailureInsteadOfThrowing()
    {
        var handler = StubHttpMessageHandler.Throws(new HttpRequestException("no route to host"));
        var (probe, recorder) = CreateProbe(CreateConfiguredSettings(), handler);

        var result = await probe.ProbeAsync();

        Assert.True(result.IsConfigured);
        Assert.False(result.Success);
        Assert.Single(recorder.Entries);
    }

    [Fact]
    public async Task ProbeAsync_ErrorMessage_ShouldNotLeakUpstreamBody()
    {
        // 上游訊息可能夾帶提示詞片段，健康頁只能顯示 error.code，不可回吐 message。
        var handler = StubHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            """{ "error": { "code": "unsupported_value", "message": "SENSITIVE-UPSTREAM-TEXT" } }""");
        var (probe, _) = CreateProbe(CreateConfiguredSettings(), handler);

        var result = await probe.ProbeAsync();

        Assert.Contains("unsupported_value", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SENSITIVE-UPSTREAM-TEXT", result.Message, StringComparison.Ordinal);
    }

    private static AiSettings CreateConfiguredSettings()
        => new()
        {
            Provider = nameof(AiProvider.OpenAI),
            Endpoint = "https://example.invalid",
            ApiKey = "test-key",
            Model = "gpt-5.6-sol",
        };

    private static (AiHealthProbe Probe, FakeRecorder Recorder) CreateProbe(
        AiSettings settings, StubHttpMessageHandler handler)
    {
        var recorder = new FakeRecorder();

        var probe = new AiHealthProbe(
            NullLogger<AiHealthProbe>.Instance,
            new StubHttpClientFactory(handler),
            new StaticOptionsMonitor<AiSettings>(settings),
            recorder,
            new CurrentUserService { CurrentUser = new CurrentUser { Id = 7, Account = "support" } });

        return (probe, recorder);
    }

    /// <summary>只把收到的項目留下來，不碰資料庫也不碰檔案系統。</summary>
    private sealed class FakeRecorder : ITokenUsageRecorder
    {
        public List<TokenUsageEntry> Entries { get; } = [];

        public Task RecordAsync(TokenUsageEntry entry)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }
}
