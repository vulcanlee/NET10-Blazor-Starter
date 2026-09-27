using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Others;
using MyProject.Models.Systems;
using MyProject.Web.Ai;
using MyProject.Web.Configuration;

namespace MyProject.Tests;

/// <summary>
/// 共用的 Chat Completions 呼叫核心（0.9.68 起由 AI 日誌分析與 AI 例外分析共用）。
///
/// HTTP 錯誤對應的細部行為由 <see cref="AiLogAnalysisServiceTests"/> 經由日誌分析守門；
/// 這裡只釘住「共用化之後才有的」契約：多訊息、作業名稱、呼叫端提供的提示文字。
/// </summary>
public sealed class AiChatCompletionClientTests
{
    private const string SuccessPayload = """
        {
          "model": "gpt-4o-2024-11-20",
          "choices": [ { "finish_reason": "stop", "message": { "content": "## 管理者摘要\n沒事。" } } ],
          "usage": { "prompt_tokens": 120, "completion_tokens": 45, "total_tokens": 165 }
        }
        """;

    [Fact]
    public async Task CompleteAsync_ShouldSendAllMessagesInOrder()
    {
        var (client, handler, _) = CreateClient(StubHttpMessageHandler.Json(HttpStatusCode.OK, SuccessPayload));

        var result = await client.CompleteAsync(CreateRequest(
            new AiChatMessage(AiChatRoles.System, "sys"),
            new AiChatMessage(AiChatRoles.User, "q1"),
            new AiChatMessage(AiChatRoles.Assistant, "a1"),
            new AiChatMessage(AiChatRoles.User, "q2")));

        Assert.True(result.Success);
        Assert.Equal("## 管理者摘要\n沒事。", result.Markdown);

        using var document = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        var roles = document.RootElement.GetProperty("messages").EnumerateArray()
            .Select(item => item.GetProperty("role").GetString());
        Assert.Equal(["system", "user", "assistant", "user"], roles);
    }

    [Fact]
    public async Task CompleteAsync_ShouldRecordUsage_WithCallerOperation()
    {
        var (client, _, recorder) = CreateClient(StubHttpMessageHandler.Json(HttpStatusCode.OK, SuccessPayload));

        await client.CompleteAsync(CreateRequest(new AiChatMessage(AiChatRoles.User, "q")));

        var entry = Assert.Single(recorder.Entries);
        Assert.Equal(TokenUsageOperations.AiExceptionAnalysis, entry.Operation);
        Assert.Equal(TokenUsageCallKinds.Chat, entry.CallKind);
        Assert.True(entry.Success);
        Assert.Equal(120, entry.InputCount);
    }

    [Fact]
    public async Task CompleteAsync_ShouldUseCallerMessage_WhenContextLengthExceeded()
    {
        const string payload = """
            { "error": { "message": "too long", "type": "invalid_request_error", "code": "context_length_exceeded" } }
            """;
        var (client, _, _) = CreateClient(StubHttpMessageHandler.Json(HttpStatusCode.BadRequest, payload));

        var result = await client.CompleteAsync(CreateRequest(new AiChatMessage(AiChatRoles.User, "q")));

        Assert.Equal(AiAnalysisFailureReason.UpstreamError, result.Reason);
        Assert.Equal("CALLER-CONTEXT-MESSAGE", result.ErrorMessage);
    }

    [Fact]
    public async Task CompleteAsync_ShouldNotRecord_WhenCanceledByCaller()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubHttpMessageHandler(_ =>
        {
            cts.Cancel();
            throw new TaskCanceledException();
        });
        var (client, _, recorder) = CreateClient(handler);

        var result = await client.CompleteAsync(CreateRequest(new AiChatMessage(AiChatRoles.User, "q")), cts.Token);

        Assert.Equal(AiAnalysisFailureReason.Canceled, result.Reason);
        Assert.Empty(recorder.Entries);
    }

    [Fact]
    public async Task CompleteAsync_ShouldNotSend_WhenSettingsIncomplete()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, SuccessPayload);
        var (client, _, recorder) = CreateClient(handler, new AiSettings());

        var result = await client.CompleteAsync(CreateRequest(new AiChatMessage(AiChatRoles.User, "q")));

        Assert.Equal(AiAnalysisFailureReason.NotConfigured, result.Reason);
        Assert.Equal(0, handler.CallCount);
        Assert.Empty(recorder.Entries);
    }

    private static AiChatCompletionRequest CreateRequest(params AiChatMessage[] messages) => new()
    {
        Operation = TokenUsageOperations.AiExceptionAnalysis,
        Messages = messages,
        ContextLengthExceededMessage = "CALLER-CONTEXT-MESSAGE",
        TimeoutHint = "請稍後再試。",
        SubmittedContentLabel = "送出的內容",
    };

    private static (AiChatCompletionClient Client, StubHttpMessageHandler Handler, RecordingTokenUsageRecorder Recorder)
        CreateClient(StubHttpMessageHandler handler, AiSettings? settings = null)
    {
        var recorder = new RecordingTokenUsageRecorder();
        var client = new AiChatCompletionClient(
            NullLogger<AiChatCompletionClient>.Instance,
            new StubHttpClientFactory(handler),
            new StaticOptionsMonitor<AiSettings>(settings ?? new AiSettings
            {
                Provider = nameof(AiProvider.AzureOpenAI),
                Endpoint = "https://unit-test.openai.azure.com/openai/v1",
                ApiKey = "key",
                Model = "my-deployment",
            }),
            recorder,
            new CurrentUserService { CurrentUser = new CurrentUser { Id = 7, Account = "support" } });

        return (client, handler, recorder);
    }
}

/// <summary>只把收到的項目留下來，不碰資料庫也不碰檔案系統。</summary>
internal sealed class RecordingTokenUsageRecorder : ITokenUsageRecorder
{
    public List<TokenUsageEntry> Entries { get; } = [];

    public Task RecordAsync(TokenUsageEntry entry)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }
}
