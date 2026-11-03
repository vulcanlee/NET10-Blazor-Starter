using MyProject.Web.Ai;

namespace MyProject.Tests;

/// <summary>
/// 串流回應（SSE）的解析（0.9.141 起）。記帳中介層從擷取到的 SSE 原文解析用量、模型與結束原因，
/// 與非串流的 <see cref="AiChatResponseParser.Parse"/> 走同一套規則。
/// </summary>
public sealed class AiChatStreamParserTests
{
    [Fact]
    public void ParseStream_ShouldConcatenateDeltas_AndReadFinishReasonModelAndUsage()
    {
        var result = AiChatResponseParser.ParseStream(AiSse.Build(["## 摘要", "\n一切", "正常。"]));

        Assert.Equal("## 摘要\n一切正常。", result.Content);
        Assert.Equal("stop", result.FinishReason);
        Assert.Equal(AiSse.Model, result.ModelName);
        Assert.Equal(10, result.Usage?.InputCount);
        Assert.Equal(2, result.Usage?.OutputCount);
        Assert.Equal(12, result.Usage?.TotalCount);
    }

    /// <summary>Azure 第一個 chunk 只有 prompt_filter_results、choices 為空，model 為空字串，不可蓋掉真正的模型名稱。</summary>
    [Fact]
    public void ParseStream_ShouldTolerateAzurePromptFilterChunk()
    {
        var result = AiChatResponseParser.ParseStream(AiSse.Build(["ok"], azurePromptFilterFirst: true));

        Assert.Equal("ok", result.Content);
        Assert.Equal(AiSse.Model, result.ModelName);
    }

    [Fact]
    public void ParseStream_ShouldReportFilterCategory()
    {
        var result = AiChatResponseParser.ParseStream(
            AiSse.Build([], finishReason: "content_filter", filteredCategory: "violence"));

        Assert.Equal(string.Empty, result.Content);
        Assert.Equal("content_filter", result.FinishReason);
        Assert.Equal("violence", result.FilterCategory);
    }

    /// <summary>中途取消時只有前幾個 chunk，最後一行還可能不完整 —— 不可炸，能讀多少算多少。</summary>
    [Fact]
    public void ParseStream_ShouldReadPartialStream_WithTruncatedLastLine()
    {
        var full = AiSse.Build(["前半", "後半"]);
        var partial = full[..(full.IndexOf("後半", StringComparison.Ordinal) - 5)];

        var result = AiChatResponseParser.ParseStream(partial);

        Assert.Equal("前半", result.Content);
        Assert.Null(result.Usage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>gateway error</html>")]
    [InlineData("data: [DONE]\n\n")]
    public void ParseStream_ShouldReturnEmpty_WhenNothingUsable(string sse)
    {
        var result = AiChatResponseParser.ParseStream(sse);

        Assert.Equal(string.Empty, result.Content);
        Assert.Null(result.Usage);
    }

    /// <summary>紅線 1：只回 usage 子物件，絕不帶出模型產生的內文。</summary>
    [Fact]
    public void ExtractStreamUsageJson_ShouldReturnOnlyUsageObject()
    {
        var usage = AiChatResponseParser.ExtractStreamUsageJson(AiSse.Build(["機密內文"]));

        Assert.Equal(AiSse.UsageJson, usage);
    }

    [Fact]
    public void ExtractStreamUsageJson_ShouldReturnNull_WhenNoUsageChunk()
        => Assert.Null(AiChatResponseParser.ExtractStreamUsageJson(AiSse.Build(["x"], usageJson: null)));
}
