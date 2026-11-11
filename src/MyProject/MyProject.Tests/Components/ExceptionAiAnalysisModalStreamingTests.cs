using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Ai;
using MyProject.Dtos.Commons;
using MyProject.Models.AdapterModel;
using MyProject.Web.Ai;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Client.Views.Admins;

namespace MyProject.Tests.Components;

/// <summary>
/// AI 例外分析視窗的串流、「停止產生」與追問（0.9.141 起；0.10.5 起在 WASM 執行，資料走 <c>POST api/v1/exception-logs/{id}/ai</c> 的 NDJSON 串流）。
/// HTTP 換成可控的假回應：先送一段文字，然後卡住直到請求被中斷 —— 模擬模型還在產生時使用者按下停止。
/// </summary>
public sealed class ExceptionAiAnalysisModalStreamingTests : ComponentTestBase
{
    private readonly FakeAiHandler handler = new();

    public ExceptionAiAnalysisModalStreamingTests()
    {
        Services.AddLogging();
        Services.AddSingleton(sp =>
        {
            var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") };
            var navigation = sp.GetRequiredService<NavigationManager>();
            var session = new ClientSession(http, new TokenStore(sp.GetRequiredService<IJSRuntime>()), navigation, NullLogger<ClientSession>.Instance);
            return new ExceptionLogApi(new ApiClient(http, session, navigation, NullLogger<ApiClient>.Instance));
        });
        SetupTextAreaInterop();
    }

    /// <summary>
    /// 追問輸入框（AntDesign TextArea）第一次渲染時會呼叫 JS 量測尺寸，並直接讀回傳物件的欄位。
    /// Loose 模式對參考型別回 null 會讓它丟 NullReferenceException，所以替這兩個呼叫回一個空物件。
    /// 回傳型別是 AntDesign 的內部型別，只能用反射設定。
    /// </summary>
    private void SetupTextAreaInterop()
    {
        var infoType = typeof(AntDesign.TextArea).Assembly.GetTypes().Single(type => type.Name == "TextAreaInfo");
        var setup = typeof(BunitJSInteropSetupExtensions).GetMethods().Single(method =>
            method.Name == nameof(BunitJSInteropSetupExtensions.Setup)
            && method.IsGenericMethodDefinition
            && method.GetParameters().Select(parameter => parameter.ParameterType)
                .SequenceEqual([typeof(BunitJSInterop), typeof(string), typeof(InvocationMatcher)]));

        foreach (var identifier in new[]
        {
            "AntDesign.interop.inputHelper.registerResizeTextArea",
            "AntDesign.interop.inputHelper.getTextAreaInfo",
        })
        {
            var handlerSetup = setup.MakeGenericMethod(infoType)
                .Invoke(null, [JSInterop, identifier, (InvocationMatcher)(_ => true)])!;
            handlerSetup.GetType().GetMethod("SetResult")!
                .Invoke(handlerSetup, [Activator.CreateInstance(infoType, nonPublic: true)]);
        }
    }

    [Fact]
    public async Task Streaming_ShouldShowPartialTextAndStopButton()
    {
        var cut = Render<ExceptionAiAnalysisModal>();

        var opening = cut.InvokeAsync(() => cut.Instance.OpenAsync(Item(), maxFollowUpRounds: 3));

        cut.WaitForAssertion(() => Assert.Contains("前半段", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("產生中", cut.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(StopButtons(cut));

        // 收尾：停止，讓開窗的工作結束。
        await cut.InvokeAsync(() => StopButtons(cut)[0].Click());
        await opening;
    }

    [Fact]
    public async Task Stop_AfterPartialText_ShouldKeepContentMarkedInterrupted_AndAbortTheRequest()
    {
        var cut = Render<ExceptionAiAnalysisModal>();
        var opening = cut.InvokeAsync(() => cut.Instance.OpenAsync(Item(), maxFollowUpRounds: 3));
        cut.WaitForAssertion(() => Assert.Contains("前半段", cut.Markup, StringComparison.Ordinal));

        await cut.InvokeAsync(() => StopButtons(cut)[0].Click());
        await opening;

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("前半段", cut.Markup, StringComparison.Ordinal);
            Assert.Contains(AiInterruption.Notice, cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("產生中", cut.Markup, StringComparison.Ordinal);
            Assert.Empty(StopButtons(cut));
        });
        Assert.True(handler.WasAborted);
    }

    [Fact]
    public async Task Stop_BeforeAnyText_ShouldShowStoppedMessage()
    {
        handler.FirstDelta = null;
        var cut = Render<ExceptionAiAnalysisModal>();
        var opening = cut.InvokeAsync(() => cut.Instance.OpenAsync(Item(), maxFollowUpRounds: 3));
        cut.WaitForAssertion(() => Assert.NotEmpty(StopButtons(cut)));

        await cut.InvokeAsync(() => StopButtons(cut)[0].Click());
        await opening;

        cut.WaitForAssertion(() => Assert.Contains("已停止產生", cut.Markup, StringComparison.Ordinal));
    }

    /// <summary>
    /// 第一次分析成功後追問：送回的是「例外明細之後」的對話（第一則由伺服器重組），以這次的問題結尾，整段共用同一個對話識別碼。
    /// 中途被停止的回覆送回原文（不帶「已中止」註記）。
    /// </summary>
    [Fact]
    public async Task FollowUp_ShouldSendTheConversationAfterTheDetail_WithTheSameConversationId()
    {
        handler.Complete = true;
        var cut = Render<ExceptionAiAnalysisModal>();
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(Item(), maxFollowUpRounds: 3));
        cut.WaitForAssertion(() => Assert.Contains("報告內容", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("已追問 0／3 輪", cut.Markup, StringComparison.Ordinal);

        await TypeAsync(cut, "為什麼會這樣？");
        await cut.InvokeAsync(() => cut.FindAll("button").Single(button => button.TextContent.Trim() == "送出").Click());

        cut.WaitForAssertion(() => Assert.Contains("已追問 1／3 輪", cut.Markup, StringComparison.Ordinal));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Empty(handler.Requests[0].Messages);
        Assert.Equal(["assistant", "user"], handler.Requests[1].Messages.Select(x => x.Role));
        Assert.Equal(["報告內容", "為什麼會這樣？"], handler.Requests[1].Messages.Select(x => x.Content));
        Assert.NotEqual(Guid.Empty, handler.Requests[0].ConversationId);
        Assert.Equal(handler.Requests[0].ConversationId, handler.Requests[1].ConversationId);
        Assert.Equal("api/v1/exception-logs/42/ai", handler.Paths[0]);
    }

    /// <summary>追問被伺服器拒絕：問題放回輸入框，畫面留一則錯誤泡泡（同 Server 版）。</summary>
    [Fact]
    public async Task FollowUp_WhenTheServerRefuses_ShouldPutTheQuestionBack()
    {
        handler.Complete = true;
        var cut = Render<ExceptionAiAnalysisModal>();
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(Item(), maxFollowUpRounds: 3));
        cut.WaitForAssertion(() => Assert.Contains("報告內容", cut.Markup, StringComparison.Ordinal));
        handler.RefuseWith = "單則追問不可超過 2000 字。";

        await TypeAsync(cut, "再問一次");
        await cut.InvokeAsync(() => cut.FindAll("button").Single(button => button.TextContent.Trim() == "送出").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("追問未完成", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("單則追問不可超過 2000 字。", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("已追問 0／3 輪", cut.Markup, StringComparison.Ordinal);
        });
        Assert.Equal("再問一次", cut.FindComponent<AntDesign.TextArea>().Instance.Value);
    }

    /// <summary>
    /// 伺服器已送出失敗結果、還在寫稽核沒關串流時，使用者剛好按「停止產生」（0.10.5 獨立審查）：
    /// 仍要照失敗收尾（問題放回輸入框、輪數不變），否則對話停在提問結尾，之後的追問一律被伺服器拒絕。
    /// </summary>
    [Fact]
    public async Task Stop_AfterAFailureResultArrived_ShouldStillPutTheQuestionBack()
    {
        handler.Complete = true;
        var cut = Render<ExceptionAiAnalysisModal>();
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(Item(), maxFollowUpRounds: 3));
        cut.WaitForAssertion(() => Assert.Contains("報告內容", cut.Markup, StringComparison.Ordinal));
        handler.Complete = false;
        handler.FirstDelta = null;
        handler.FailThenHang = "已達每人每日 AI 用量上限。";

        await TypeAsync(cut, "還有別的原因嗎");
        await cut.InvokeAsync(() => cut.FindAll("button").Single(button => button.TextContent.Trim() == "送出").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(StopButtons(cut)));
        await Task.Delay(200);
        await cut.InvokeAsync(() => StopButtons(cut)[0].Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("追問未完成", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("已追問 0／3 輪", cut.Markup, StringComparison.Ordinal);
        });
        Assert.Equal("還有別的原因嗎", cut.FindComponent<AntDesign.TextArea>().Instance.Value);
    }

    /// <summary>在追問輸入框打字（AntDesign TextArea 的值經 @bind-Value 回寫；直接觸發它的 ValueChanged，不依賴 DOM 事件的去抖動）。</summary>
    private static Task TypeAsync(IRenderedComponent<ExceptionAiAnalysisModal> cut, string text)
        => cut.InvokeAsync(() => cut.FindComponent<AntDesign.TextArea>().Instance.ValueChanged.InvokeAsync(text));

    private static List<AngleSharp.Dom.IElement> StopButtons(IRenderedComponent<ExceptionAiAnalysisModal> cut)
        => cut.FindAll("button").Where(button => button.TextContent.Contains("停止產生", StringComparison.Ordinal)).ToList();

    private static ExceptionLogAdapterModel Item() => new()
    {
        Id = 42,
        ExceptionType = "System.InvalidOperationException",
        Message = "boom",
        Source = "UnitTest",
        OccurrenceCount = 1,
        FirstOccurredAt = new DateTime(2026, 11, 2, 9, 0, 0),
        LastOccurredAt = new DateTime(2026, 11, 2, 9, 0, 0),
    };

    /// <summary>
    /// 假的 AI 端點。預設：送 start 與 <see cref="FirstDelta"/>（null 則不送），然後卡住直到請求被中斷。
    /// <see cref="Complete"/>：回一段完整的成功結果。<see cref="RefuseWith"/>：回 400 ApiResult。
    /// </summary>
    private sealed class FakeAiHandler : HttpMessageHandler
    {
        public string? FirstDelta { get; set; } = "前半段";

        public bool Complete { get; set; }

        public string? RefuseWith { get; set; }

        /// <summary>送出一筆失敗結果後串流不結束（伺服器還在寫稽核）。</summary>
        public string? FailThenHang { get; set; }

        public bool WasAborted { get; private set; }

        public List<ExceptionAiRequestDto> Requests { get; } = [];

        public List<string> Paths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath.TrimStart('/'));
            Requests.Add((await request.Content!.ReadFromJsonAsync<ExceptionAiRequestDto>(JsonSerializerOptions.Web, cancellationToken))!);

            if (RefuseWith is { } message)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = JsonContent.Create(ApiResult.ValidationError(message), options: JsonSerializerOptions.Web),
                };
            }

            var frames = new List<AiStreamFrameDto> { new() { Kind = AiStreamFrameKinds.Start } };
            if (Complete)
            {
                frames.Add(new() { Kind = AiStreamFrameKinds.Delta, Text = "報告內容" });
                frames.Add(new() { Kind = AiStreamFrameKinds.Result, Result = new AiResultDto { Success = true, Markdown = "報告內容", ModelName = "fake-model", ElapsedMilliseconds = 1000 } });
            }
            else if (FailThenHang is { } failure)
            {
                frames.Add(new() { Kind = AiStreamFrameKinds.Result, Result = new AiResultDto { Success = false, Reason = "QuotaExceeded", ErrorMessage = failure } });
            }
            else if (FirstDelta is not null)
            {
                frames.Add(new() { Kind = AiStreamFrameKinds.Delta, Text = FirstDelta });
            }

            var head = Encoding.UTF8.GetBytes(string.Concat(frames.Select(frame => JsonSerializer.Serialize(frame, JsonSerializerOptions.Web) + "\n")));
            var content = new StreamContent(Complete ? new MemoryStream(head) : new HangingStream(head, () => WasAborted = true));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-ndjson");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    /// <summary>先讀出 <c>head</c>，之後的讀取卡住直到被取消（模擬模型還在產生）。</summary>
    private sealed class HangingStream(byte[] head, Action onAborted) : Stream
    {
        private int position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (position < head.Length)
            {
                var count = Math.Min(buffer.Length, head.Length - position);
                head.AsMemory(position, count).CopyTo(buffer);
                position += count;
                return count;
            }

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                onAborted();
                throw;
            }

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
