using MyProject.Models.AdapterModel;
using MyProject.Web.Ai;

namespace MyProject.Tests;

/// <summary>「停止產生」的標示（0.9.141 起）：已中止的內容複製或下載後仍看得出不完整；Token 用量頁以「已取消」區分。</summary>
public sealed class AiInterruptionTests
{
    [Fact]
    public void Annotate_ShouldPrependNotice_AndKeepOriginalMarkdown()
    {
        var annotated = AiInterruption.Annotate("## 摘要\n寫到一半");

        Assert.StartsWith($"> {AiInterruption.Notice}", annotated, StringComparison.Ordinal);
        Assert.EndsWith("## 摘要\n寫到一半", annotated, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Canceled", true)]
    [InlineData("Timeout", false)]
    [InlineData(null, false)]
    public void TokenUsage_IsCanceled_ShouldOnlyMatchCanceled(string? failureReason, bool expected)
        => Assert.Equal(expected, new TokenUsageLogAdapterModel { FailureReason = failureReason }.IsCanceled);
}
