using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyProject.Business.Services.Other;
using MyProject.Models.Others;
using MyProject.Models.Systems;
using MyProject.Web.Ai;
using MyProject.Web.Configuration;

namespace MyProject.Tests;

/// <summary>
/// 串流呼叫的記帳與結果（0.9.141 起）。守的是速查表 §6.7 的紅線在串流下仍然成立：
/// 每次呼叫都記、原文只進 AI 對話紀錄、usage 只存子物件，以及新的取消規則 ——
/// 已收到文字才停止記一筆 <c>Canceled</c>（用量空白），還沒收到就停止不記用量。
/// </summary>
public sealed class AiStreamingTests
{
    [Fact]
    public async Task StreamAsync_ShouldYieldDeltas_ThenSuccessResult_AndRecordUsage()
    {
        var recorder = new RecordingTokenUsageRecorder();
        var callLog = new FakeAiCallLogRecorder();
        var handler = new StubHttpMessageHandler(_ => AiSse.Response(AiSse.Build(["## 摘要", "\n一切正常。"])));
        var client = CreateClient(handler, recorder, callLog);

        var (deltas, result) = await CollectAsync(client.StreamAsync(Request()));

        Assert.Equal(["## 摘要", "\n一切正常。"], deltas);
        Assert.True(result.Success);
        Assert.False(result.IsInterrupted);
        Assert.Equal("## 摘要\n一切正常。", result.Markdown);
        Assert.Equal(AiSse.Model, result.ModelName);
        Assert.Equal(10, result.Usage?.InputCount);

        var entry = Assert.Single(recorder.Entries);
        Assert.True(entry.Success);
        Assert.Equal(12, entry.TotalCount);
        Assert.Equal(AiSse.Model, entry.Model);
        Assert.Equal(AiSse.UsageJson, entry.RawUsageJson);

        var logged = Assert.Single(callLog.Entries);
        Assert.True(logged.Success);
        Assert.Equal("## 摘要\n一切正常。", logged.ResponseText);
        Assert.Equal(entry.CallId, logged.CallId);
        Assert.Contains("data: [DONE]", logged.ResponseBody, StringComparison.Ordinal);
    }

    /// <summary>串流請求要帶 stream 與 include_usage —— 少了後者，usage 永遠拿不到、整筆變成未定價。</summary>
    [Fact]
    public async Task StreamAsync_ShouldRequestStreamWithUsage()
    {
        var handler = new StubHttpMessageHandler(_ => AiSse.Response(AiSse.Build(["ok"])));

        await CollectAsync(CreateClient(handler).StreamAsync(Request()));

        using var document = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.True(document.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(document.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    /// <summary>新規則：已經收到文字才停止 → 記一筆 Canceled、用量空白；結果保留已收到的部分並標示已中止。</summary>
    [Fact]
    public async Task StreamAsync_CanceledAfterText_ShouldRecordCanceledWithoutUsage_AndKeepPartialText()
    {
        var recorder = new RecordingTokenUsageRecorder();
        var callLog = new FakeAiCallLogRecorder();
        var handler = PausingHandler(AiSse.Build(["前半段", "後半段"]), pauseBefore: "後半段");
        var client = CreateClient(handler, recorder, callLog);
        using var cts = new CancellationTokenSource();

        var deltas = new List<string>();
        AiAnalysisResult? result = null;
        await foreach (var update in client.StreamAsync(Request(), cts.Token))
        {
            if (update.Result is not null)
            {
                result = update.Result;
                continue;
            }

            deltas.Add(update.TextDelta);
            await cts.CancelAsync();
        }

        Assert.Equal(["前半段"], deltas);
        Assert.NotNull(result);
        Assert.Equal(AiAnalysisFailureReason.Canceled, result!.Reason);
        Assert.True(result.IsInterrupted);
        Assert.Equal("前半段", result.Markdown);

        var entry = Assert.Single(recorder.Entries);
        Assert.False(entry.Success);
        Assert.Equal(nameof(AiAnalysisFailureReason.Canceled), entry.FailureReason);
        Assert.Null(entry.InputCount);
        Assert.Null(entry.OutputCount);

        var logged = Assert.Single(callLog.Entries);
        Assert.Equal(nameof(AiAnalysisFailureReason.Canceled), logged.FailureReason);
        Assert.Equal("前半段", logged.ResponseText);
        Assert.Equal(entry.CallId, logged.CallId);
    }

    /// <summary>還沒收到任何文字就停止：照舊不記用量，但 AI 對話紀錄要有（請求確實送出過）。</summary>
    [Fact]
    public async Task StreamAsync_CanceledBeforeText_ShouldNotRecordUsage_ButLogCall()
    {
        var recorder = new RecordingTokenUsageRecorder();
        var callLog = new FakeAiCallLogRecorder();
        using var cts = new CancellationTokenSource();

        // 上游收到請求之後才開始倒數，才能確定取消時請求已經送出（整套測試一起跑時機器很忙）。
        var handler = new StubHttpMessageHandler(request =>
        {
            cts.CancelAfter(TimeSpan.FromMilliseconds(200));
            return PausingResponse(AiSse.Build(["內容"]), pauseBefore: "內容");
        });
        var client = CreateClient(handler, recorder, callLog);

        var (deltas, result) = await CollectAsync(client.StreamAsync(Request(), cts.Token));

        Assert.Empty(deltas);
        Assert.Equal(AiAnalysisFailureReason.Canceled, result.Reason);
        Assert.False(result.IsInterrupted);
        Assert.Empty(recorder.Entries);
        Assert.Equal(nameof(AiAnalysisFailureReason.Canceled), Assert.Single(callLog.Entries).FailureReason);
    }

    [Fact]
    public async Task StreamAsync_UpstreamError_ShouldMapAndRecord()
    {
        var recorder = new RecordingTokenUsageRecorder();
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.TooManyRequests, """{ "error": { "code": "rate_limit" } }""");

        var (_, result) = await CollectAsync(CreateClient(handler, recorder).StreamAsync(Request()));

        Assert.Equal(AiAnalysisFailureReason.RateLimited, result.Reason);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(nameof(AiAnalysisFailureReason.RateLimited), Assert.Single(recorder.Entries).FailureReason);
    }

    /// <summary>串流結束卻沒有任何文字（例如被內容過濾）：和非串流一樣記 EmptyResponse，用量照記。</summary>
    [Fact]
    public async Task StreamAsync_EmptyContent_ShouldRecordEmptyResponseWithUsage()
    {
        var recorder = new RecordingTokenUsageRecorder();
        var handler = new StubHttpMessageHandler(
            _ => AiSse.Response(AiSse.Build([], finishReason: "content_filter", filteredCategory: "violence")));

        var (_, result) = await CollectAsync(CreateClient(handler, recorder).StreamAsync(Request()));

        Assert.Equal(AiAnalysisFailureReason.EmptyResponse, result.Reason);
        Assert.Contains("violence", result.ErrorMessage, StringComparison.Ordinal);
        var entry = Assert.Single(recorder.Entries);
        Assert.Equal(nameof(AiAnalysisFailureReason.EmptyResponse), entry.FailureReason);
        Assert.Equal(12, entry.TotalCount);
    }

    [Fact]
    public async Task StreamAsync_ShouldReportTruncation_WhenFinishReasonIsLength()
    {
        var handler = new StubHttpMessageHandler(_ => AiSse.Response(AiSse.Build(["寫到一半"], finishReason: "length")));

        var (_, result) = await CollectAsync(CreateClient(handler).StreamAsync(Request()));

        Assert.True(result.Success);
        Assert.True(result.IsTruncatedByLength);
    }

    /// <summary>逾時涵蓋整段串流：收到一部分後上游卡住，時間到就記 Timeout（不是 Canceled）。</summary>
    [Fact]
    public async Task StreamAsync_StalledMidStream_ShouldTimeOut_AndRecordTimeout()
    {
        var recorder = new RecordingTokenUsageRecorder();
        var handler = PausingHandler(AiSse.Build(["前半段", "後半段"]), pauseBefore: "後半段");
        var client = CreateClient(handler, recorder, settings: new AiSettings
        {
            Provider = nameof(AiProvider.OpenAI),
            ApiKey = "key",
            Model = "gpt-4o",
            TimeoutSeconds = 1,
        });

        var (deltas, result) = await CollectAsync(client.StreamAsync(Request()));

        Assert.Equal(["前半段"], deltas);
        Assert.Equal(AiAnalysisFailureReason.Timeout, result.Reason);
        Assert.Equal(nameof(AiAnalysisFailureReason.Timeout), Assert.Single(recorder.Entries).FailureReason);
    }

    /// <summary>日誌分析的串流結果要帶上提示詞資訊（送出筆數、是否被筆數上限截斷），畫面與 PDF 都要用。</summary>
    [Fact]
    public async Task LogAnalysis_AnalyzeStreamingAsync_ShouldAttachPromptToResult()
    {
        var handler = new StubHttpMessageHandler(_ => AiSse.Response(AiSse.Build(["ok"])));
        var options = new StaticOptionsMonitor<AiSettings>(new AiSettings { Provider = nameof(AiProvider.OpenAI), ApiKey = "key", Model = "gpt-4o" });
        var service = new AiLogAnalysisService(
            options,
            AiTestAgents.CreateChatClient(
                NullLogger<AiChatCompletionClient>.Instance,
                new StubHttpClientFactory(handler),
                options,
                new RecordingTokenUsageRecorder(),
                new CurrentUserService { CurrentUser = new CurrentUser { Id = 7, Account = "support" } },
                new FakeAiCallLogRecorder(),
                new StubAiQuotaService(),
                new RecordingAuditLogService()),
            new StubSystemPromptProvider());
        var entries = Enumerable.Range(1, 3)
            .Select(index => new MyProject.Web.Diagnostics.LogEntry
            {
                Sequence = index,
                Timestamp = new DateTime(2026, 11, 2, 10, 0, 0).AddMinutes(index),
                Level = "ERROR",
                Raw = $"boom {index}",
            })
            .ToList();

        var (_, result) = await CollectAsync(service.AnalyzeStreamingAsync(entries));

        Assert.True(result.Success);
        Assert.Equal(3, result.Prompt.IncludedEntryCount);
    }

    [Fact]
    public async Task StreamAsync_NotConfigured_ShouldYieldSingleFailure_WithoutRequest()
    {
        var handler = new StubHttpMessageHandler(_ => AiSse.Response(AiSse.Build(["x"])));
        var client = CreateClient(handler, settings: new AiSettings { Provider = nameof(AiProvider.OpenAI) });

        var (deltas, result) = await CollectAsync(client.StreamAsync(Request()));

        Assert.Empty(deltas);
        Assert.Equal(AiAnalysisFailureReason.NotConfigured, result.Reason);
        Assert.Equal(0, handler.CallCount);
    }

    private static async Task<(List<string> Deltas, AiAnalysisResult Result)> CollectAsync(
        IAsyncEnumerable<AiStreamUpdate> stream)
    {
        var deltas = new List<string>();
        AiAnalysisResult? result = null;
        await foreach (var update in stream)
        {
            if (update.Result is not null)
            {
                Assert.Null(result);
                result = update.Result;
            }
            else
            {
                deltas.Add(update.TextDelta);
            }
        }

        Assert.NotNull(result);
        return (deltas, result!);
    }

    private static AiChatCompletionRequest Request() => new()
    {
        Operation = TokenUsageOperations.AiLogAnalysis,
        Messages = [new AiChatMessage(AiChatRoles.System, "sys"), new AiChatMessage(AiChatRoles.User, "q")],
        ContextLengthExceededMessage = "too long",
        TimeoutHint = "請稍後再試。",
        SubmittedContentLabel = "送出的內容",
    };

    private static AiChatCompletionClient CreateClient(
        StubHttpMessageHandler handler,
        RecordingTokenUsageRecorder? recorder = null,
        FakeAiCallLogRecorder? callLog = null,
        AiSettings? settings = null)
    {
        var options = new StaticOptionsMonitor<AiSettings>(settings ?? new AiSettings
        {
            Provider = nameof(AiProvider.AzureOpenAI),
            Endpoint = "https://contoso.openai.azure.com/openai/v1",
            ApiKey = "key",
            Model = "my-deployment",
        });
        return AiTestAgents.CreateChatClient(
            NullLogger<AiChatCompletionClient>.Instance,
            new StubHttpClientFactory(handler),
            options,
            recorder ?? new RecordingTokenUsageRecorder(),
            new CurrentUserService { CurrentUser = new CurrentUser { Id = 7, Account = "support" } },
            callLog ?? new FakeAiCallLogRecorder(),
            new StubAiQuotaService(),
            new RecordingAuditLogService());
    }

    /// <summary>送出 SSE 直到 <paramref name="pauseBefore"/> 所在的那個 chunk 之前，然後卡住直到被取消（模擬模型還在想）。</summary>
    private static StubHttpMessageHandler PausingHandler(string sse, string pauseBefore)
        => new(_ => PausingResponse(sse, pauseBefore));

    private static HttpResponseMessage PausingResponse(string sse, string pauseBefore)
    {
        var cut = sse.LastIndexOf("data: ", sse.IndexOf(pauseBefore, StringComparison.Ordinal), StringComparison.Ordinal);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new PausingStream(Encoding.UTF8.GetBytes(sse[..cut]))),
        };
    }

    private sealed class PausingStream(byte[] head) : Stream
    {
        private int position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (position < head.Length)
            {
                var count = Math.Min(buffer.Length, head.Length - position);
                head.AsMemory(position, count).CopyTo(buffer);
                position += count;
                return count;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
