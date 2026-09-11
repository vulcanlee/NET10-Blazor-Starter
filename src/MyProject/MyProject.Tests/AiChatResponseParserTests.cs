using MyProject.Web.Ai;

namespace MyProject.Tests;

/// <summary>
/// Chat Completions 回應解析測試。
///
/// 重點在 Azure 與 OpenAI 的形狀差異：Azure 在內容過濾時會回空的 choices 陣列，
/// 或是 message.content 為 JSON null，兩者都必須不炸且要能回報過濾類別。
/// </summary>
public sealed class AiChatResponseParserTests
{
    [Fact]
    public void Parse_ShouldReadContentFromFirstChoice()
    {
        const string json = """
            {
              "model": "gpt-4o-2024-11-20",
              "choices": [
                { "finish_reason": "stop", "message": { "role": "assistant", "content": "## 總結\n一切正常。" } }
              ]
            }
            """;

        var result = AiChatResponseParser.Parse(json);

        Assert.Equal("## 總結\n一切正常。", result.Content);
        Assert.Equal("stop", result.FinishReason);
        Assert.Equal("gpt-4o-2024-11-20", result.ModelName);
    }

    [Fact]
    public void Parse_ShouldReadUsageCounts()
    {
        const string json = """
            {
              "choices": [ { "message": { "content": "x" } } ],
              "usage": { "prompt_tokens": 120, "completion_tokens": 45, "total_tokens": 165 }
            }
            """;

        var usage = AiChatResponseParser.Parse(json).Usage;

        Assert.NotNull(usage);
        Assert.Equal(120, usage.InputCount);
        Assert.Equal(45, usage.OutputCount);
        Assert.Equal(165, usage.TotalCount);
    }

    [Fact]
    public void Parse_ShouldReadCachedAndReasoningCounts()
    {
        const string json = """
            {
              "choices": [ { "message": { "content": "x" } } ],
              "usage": {
                "prompt_tokens": 120,
                "completion_tokens": 45,
                "total_tokens": 165,
                "prompt_tokens_details": { "cached_tokens": 64 },
                "completion_tokens_details": { "reasoning_tokens": 32 }
              }
            }
            """;

        var usage = AiChatResponseParser.Parse(json).Usage;

        Assert.NotNull(usage);
        Assert.Equal(64, usage.CachedInputCount);
        Assert.Equal(32, usage.ReasoningCount);
    }

    /// <summary>舊 api-version 沒有 details 子物件，缺的欄位要是 null 而不是 0。</summary>
    [Fact]
    public void Parse_ShouldLeaveOptionalCountsNull_WhenDetailsAbsent()
    {
        const string json = """
            {
              "choices": [ { "message": { "content": "x" } } ],
              "usage": { "prompt_tokens": 10, "completion_tokens": 5, "total_tokens": 15 }
            }
            """;

        var usage = AiChatResponseParser.Parse(json).Usage;

        Assert.NotNull(usage);
        Assert.Null(usage.CachedInputCount);
        Assert.Null(usage.ReasoningCount);
    }

    [Fact]
    public void Parse_ShouldReturnNullUsage_WhenUsageAbsent()
    {
        const string json = """{ "choices": [ { "message": { "content": "x" } } ] }""";

        Assert.Null(AiChatResponseParser.Parse(json).Usage);
    }

    [Fact]
    public void Parse_ShouldReturnNullUsage_WhenUsageIsEmptyObject()
    {
        const string json = """{ "choices": [ { "message": { "content": "x" } } ], "usage": {} }""";

        Assert.Null(AiChatResponseParser.Parse(json).Usage);
    }

    /// <summary>Azure 在回應被過濾時 content 是 JSON null，不是缺欄位。</summary>
    [Fact]
    public void Parse_ShouldHandleAzureNullContent()
    {
        const string json = """
            {
              "choices": [ { "finish_reason": "content_filter", "message": { "content": null } } ]
            }
            """;

        var result = AiChatResponseParser.Parse(json);

        Assert.Equal(string.Empty, result.Content);
        Assert.Equal("content_filter", result.FinishReason);
    }

    /// <summary>Azure 在 prompt 被過濾時 choices 是空陣列，不能直接取索引 0。</summary>
    [Fact]
    public void Parse_ShouldHandleAzureEmptyChoices()
    {
        const string json = """
            {
              "choices": [],
              "prompt_filter_results": [ { "prompt_index": 0, "content_filter_results": {} } ]
            }
            """;

        var result = AiChatResponseParser.Parse(json);

        Assert.Equal(string.Empty, result.Content);
        Assert.Equal(string.Empty, result.FilterCategory);
    }

    [Fact]
    public void Parse_ShouldReportFilterCategory_WhenContentFiltered()
    {
        const string json = """
            {
              "choices": [
                {
                  "finish_reason": "content_filter",
                  "message": { "content": null },
                  "content_filter_results": {
                    "hate": { "filtered": false, "severity": "safe" },
                    "violence": { "filtered": true, "severity": "medium" }
                  }
                }
              ]
            }
            """;

        Assert.Equal("violence", AiChatResponseParser.Parse(json).FilterCategory);
    }

    [Fact]
    public void Parse_ShouldIgnoreUnknownFields()
    {
        const string json = """
            {
              "id": "chatcmpl-1",
              "object": "chat.completion",
              "created": 1757000000,
              "system_fingerprint": "fp_abc",
              "prompt_filter_results": [ { "prompt_index": 0 } ],
              "choices": [ { "index": 0, "logprobs": null, "message": { "content": "ok" } } ]
            }
            """;

        Assert.Equal("ok", AiChatResponseParser.Parse(json).Content);
    }

    [Fact]
    public void TryGetErrorCode_ShouldReadErrorCode()
    {
        const string json = """{ "error": { "code": "context_length_exceeded", "message": "too long" } }""";

        Assert.True(AiChatResponseParser.TryGetErrorCode(json, out var code));
        Assert.Equal("context_length_exceeded", code);
    }

    [Theory]
    [InlineData("<html><body>502 Bad Gateway</body></html>")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ \"choices\": [] }")]
    public void TryGetErrorCode_ShouldReturnFalse_WhenNotAnError(string payload)
    {
        Assert.False(AiChatResponseParser.TryGetErrorCode(payload, out var code));
        Assert.Equal(string.Empty, code);
    }
}
