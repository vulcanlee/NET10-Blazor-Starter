using System.Runtime.CompilerServices;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MyProject.Business.Services.Other;
using MyProject.Models.AdapterModel;
using MyProject.Models.Others;
using MyProject.Web.Ai;
using MyProject.Web.Components.Views.Admins;

namespace MyProject.Tests.Components;

/// <summary>
/// AI 例外分析視窗的串流與「停止產生」（0.9.141 起）。
/// AI 服務換成可控的假串流：先送一段文字，然後卡住直到被取消 —— 模擬模型還在產生時使用者按下停止。
/// </summary>
public sealed class ExceptionAiAnalysisModalStreamingTests : ComponentTestBase
{
    private readonly ControlledStreamService ai = new();

    public ExceptionAiAnalysisModalStreamingTests()
    {
        Services.AddLogging();
        Services.AddSingleton<IAiExceptionAnalysisService>(ai);
        Services.AddSingleton<IAuditLogService>(new RecordingAuditLogService());
        Services.AddSingleton(new CurrentUserService { CurrentUser = new CurrentUser { Id = 1, Account = "support" } });
        Services.AddSingleton<ISystemIdentity>(new FixedSystemIdentity());
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
            var handler = setup.MakeGenericMethod(infoType)
                .Invoke(null, [JSInterop, identifier, (InvocationMatcher)(_ => true)])!;
            handler.GetType().GetMethod("SetResult")!
                .Invoke(handler, [Activator.CreateInstance(infoType, nonPublic: true)]);
        }
    }

    [Fact]
    public async Task Streaming_ShouldShowPartialTextAndStopButton()
    {
        var cut = Render<ExceptionAiAnalysisModal>();

        var opening = cut.InvokeAsync(() => cut.Instance.OpenAsync(Item(), stackTraceOrNull: null));

        cut.WaitForAssertion(() => Assert.Contains("前半段", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("產生中", cut.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(StopButtons(cut));

        // 收尾：停止，讓開窗的工作結束。
        await cut.InvokeAsync(() => StopButtons(cut)[0].Click());
        await opening;
    }

    [Fact]
    public async Task Stop_AfterPartialText_ShouldKeepContentMarkedInterrupted()
    {
        var cut = Render<ExceptionAiAnalysisModal>();
        var opening = cut.InvokeAsync(() => cut.Instance.OpenAsync(Item(), stackTraceOrNull: null));
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
        Assert.True(ai.WasCanceled);
    }

    [Fact]
    public async Task Stop_BeforeAnyText_ShouldShowStoppedMessage()
    {
        ai.FirstDelta = null;
        var cut = Render<ExceptionAiAnalysisModal>();
        var opening = cut.InvokeAsync(() => cut.Instance.OpenAsync(Item(), stackTraceOrNull: null));
        cut.WaitForAssertion(() => Assert.NotEmpty(StopButtons(cut)));

        await cut.InvokeAsync(() => StopButtons(cut)[0].Click());
        await opening;

        cut.WaitForAssertion(() => Assert.Contains("已停止產生", cut.Markup, StringComparison.Ordinal));
    }

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

    /// <summary>先送 <see cref="FirstDelta"/>（null 則不送），然後卡住直到取消，最後照真的服務回一筆 Canceled 結果。</summary>
    private sealed class ControlledStreamService : IAiExceptionAnalysisService
    {
        public string? FirstDelta { get; set; } = "前半段";

        public bool WasCanceled { get; private set; }

        public bool IsAvailable => true;

        public string UnavailableReason => string.Empty;

        public int MaxFollowUpRounds => 3;

        public Task<AiAnalysisResult> AskAsync(
            IReadOnlyList<AiChatMessage> conversation,
            AiExceptionCallContext? callContext = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("視窗 0.9.141 起只用串流版。");

        public async IAsyncEnumerable<AiStreamUpdate> AskStreamingAsync(
            IReadOnlyList<AiChatMessage> conversation,
            AiExceptionCallContext? callContext = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (FirstDelta is not null)
            {
                yield return new AiStreamUpdate { TextDelta = FirstDelta };
            }

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                WasCanceled = true;
            }

            var received = FirstDelta ?? string.Empty;
            yield return new AiStreamUpdate
            {
                Result = AiAnalysisResult.Failure(AiAnalysisFailureReason.Canceled, "已取消本次 AI 分析。") with
                {
                    Markdown = received,
                    IsInterrupted = received.Length > 0,
                },
            };
        }
    }

    private sealed class FixedSystemIdentity : ISystemIdentity
    {
        public string Name => "MyProject";

        public string Description => string.Empty;

        public string Version => "0.9.141";
    }
}
