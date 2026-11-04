using MyProject.Models.Systems;
using MyProject.Web.Ai;

namespace MyProject.Tests;

/// <summary>
/// 0.9.149 起 Agent 功能走 Responses API（Azure gpt-6.1-sol 在 Chat Completions 上不能帶工具）。
/// 記帳與 AI 對話紀錄都從 HTTP 原文解析，所以解析器要同時看得懂兩種格式 ——
/// 看不懂的話，每一次最終回答都會被判成「空回應」、用量全空。
/// 測試資料的形狀照 2026/11/04 對 Azure 實測的回應。
/// </summary>
public sealed class AiResponsesFormatTests
{
    private const string CompletedResponse = """
        {
          "id": "resp_1", "object": "response", "created_at": 1, "status": "completed",
          "error": null, "incomplete_details": null, "model": "gpt-6.1-sol",
          "output": [
            { "id": "rs_1", "type": "reasoning", "summary": [] },
            { "id": "msg_1", "type": "message", "status": "completed", "role": "assistant",
              "content": [ { "type": "output_text", "annotations": [], "text": "第一段" },
                           { "type": "output_text", "annotations": [], "text": "第二段" } ] }
          ],
          "store": false,
          "usage": { "input_tokens": 105, "input_tokens_details": { "cache_write_tokens": 0, "cached_tokens": 64 },
                     "output_tokens": 21, "output_tokens_details": { "reasoning_tokens": 7 }, "total_tokens": 126 }
        }
        """;

    [Fact]
    public void Parse_ShouldReadResponsesTextModelAndUsage()
    {
        var parsed = AiChatResponseParser.Parse(CompletedResponse);

        Assert.Equal("第一段第二段", parsed.Content);
        Assert.Equal("stop", parsed.FinishReason);
        Assert.Equal("gpt-6.1-sol", parsed.ModelName);
        Assert.Equal(105, parsed.Usage?.InputCount);
        Assert.Equal(21, parsed.Usage?.OutputCount);
        Assert.Equal(126, parsed.Usage?.TotalCount);
        Assert.Equal(64, parsed.Usage?.CachedInputCount);
        Assert.Equal(7, parsed.Usage?.ReasoningCount);
        Assert.Equal(string.Empty, parsed.FilterCategory);
    }

    [Fact]
    public void ExtractUsageJson_ShouldReturnResponsesUsageOnly()
    {
        var usage = AiChatResponseParser.ExtractUsageJson(CompletedResponse);

        Assert.NotNull(usage);
        Assert.StartsWith("{", usage);
        Assert.Contains("\"input_tokens\"", usage, StringComparison.Ordinal);
        Assert.DoesNotContain("第一段", usage, StringComparison.Ordinal);
    }

    /// <summary>只要求呼叫工具時沒有文字：Content 為空（由記帳層看 SDK 的工具呼叫判斷不是空回應）。</summary>
    [Fact]
    public void Parse_FunctionCallOnly_ShouldHaveEmptyContent()
    {
        const string json = """
            { "object": "response", "status": "completed", "model": "gpt-6.1-sol",
              "output": [ { "type": "function_call", "call_id": "call_1", "name": "search", "arguments": "{}" } ],
              "usage": { "input_tokens": 59, "output_tokens": 19, "total_tokens": 78 } }
            """;

        var parsed = AiChatResponseParser.Parse(json);

        Assert.Equal(string.Empty, parsed.Content);
        Assert.Equal(59, parsed.Usage?.InputCount);
    }

    /// <summary>
    /// ⚠️ 長度上限：Responses 以 status=incomplete、reason=max_output_tokens 表示，要對應成 length，
    /// 「空回應」訊息才會指向 MaxOutputTokens（推論 token 也算進額度）。
    /// </summary>
    [Theory]
    [InlineData("max_output_tokens", "length", "")]
    [InlineData("content_filter", "content_filter", "content_filter")]
    public void Parse_Incomplete_ShouldMapFinishReason(string reason, string expectedFinish, string expectedFilter)
    {
        var json = $$"""
            { "object": "response", "status": "incomplete", "model": "gpt-6.1-sol",
              "incomplete_details": { "reason": "{{reason}}" }, "output": [] }
            """;

        var parsed = AiChatResponseParser.Parse(json);

        Assert.Equal(expectedFinish, parsed.FinishReason);
        Assert.Equal(expectedFilter, parsed.FilterCategory);
    }

    /// <summary>Azure 的 content_filters 有 blocked 時，取第一個 filtered 的類別。</summary>
    [Fact]
    public void Parse_AzureBlockedContentFilter_ShouldReadCategory()
    {
        const string json = """
            { "object": "response", "status": "completed", "model": "gpt-6.1-sol", "output": [],
              "content_filters": [
                { "blocked": false, "source_type": "prompt", "content_filter_results": { "hate": { "filtered": false } } },
                { "blocked": true, "source_type": "completion", "content_filter_results": { "violence": { "filtered": true, "severity": "high" } } } ] }
            """;

        Assert.Equal("violence", AiChatResponseParser.Parse(json).FilterCategory);
    }

    [Fact]
    public void ParseStream_ShouldReadResponsesEvents()
    {
        var sse = AiSse.Responses(["你", "好"]);

        var parsed = AiChatResponseParser.ParseStream(sse);

        Assert.Equal("你好", parsed.Content);
        Assert.Equal("stop", parsed.FinishReason);
        Assert.Equal(AiSse.Model, parsed.ModelName);
        Assert.Equal(10, parsed.Usage?.InputCount);
        Assert.Equal(4, parsed.Usage?.CachedInputCount);
        Assert.Equal(1, parsed.Usage?.ReasoningCount);
        Assert.Equal(AiSse.ResponsesUsageJson, AiChatResponseParser.ExtractStreamUsageJson(sse));
    }

    /// <summary>中途取消：還沒收到 response.completed，內容由已收到的 delta 串起來，用量為空。</summary>
    [Fact]
    public void ParseStream_Interrupted_ShouldConcatenateReceivedDeltas()
    {
        var sse = AiSse.Responses(["第一段", "第二段"]);
        var cut = sse[..sse.IndexOf("event: response.output_text.done", StringComparison.Ordinal)];

        var parsed = AiChatResponseParser.ParseStream(cut);

        Assert.Equal("第一段第二段", parsed.Content);
        Assert.Null(parsed.Usage);
        Assert.Null(AiChatResponseParser.ExtractStreamUsageJson(cut));
    }

    [Fact]
    public void ParseStream_ToolCall_ShouldHaveEmptyContentAndUsage()
    {
        var parsed = AiChatResponseParser.ParseStream(AiSse.ResponsesToolCall("call_1", "search", """{"keyword":"A"}"""));

        Assert.Equal(string.Empty, parsed.Content);
        Assert.Equal(12, parsed.Usage?.TotalCount);
    }

    /// <summary>AI 對話紀錄頁與 PDF：Responses 請求要讀得出角色與內容，工具呼叫與工具結果也要看得到。</summary>
    [Fact]
    public void CallLogMessages_ShouldParseResponsesRequest()
    {
        const string body = """
            {
              "model": "gpt-6.1-sol",
              "instructions": "你是專案小幫手。",
              "input": [
                { "type": "message", "role": "user", "content": [ { "type": "input_text", "text": "找專案 A" } ] },
                { "type": "reasoning", "id": "rs_1", "summary": [] },
                { "type": "function_call", "call_id": "call_1", "name": "search_projects", "arguments": "{\"keyword\":\"A\"}" },
                { "type": "function_call_output", "call_id": "call_1", "output": "[{\"title\":\"A\"}]" },
                { "type": "message", "role": "assistant", "content": [ { "type": "output_text", "text": "找到 1 筆。" } ] },
                { "role": "developer", "content": "補充規則" },
                { "role": "user", "content": "再找 B" }
              ],
              "store": false
            }
            """;

        var messages = AiCallLogMessages.Parse(body);

        Assert.NotNull(messages);
        Assert.Equal(["system", "user", "assistant", "tool", "assistant", "system", "user"], messages!.Select(message => message.Role).ToList());
        Assert.Equal("你是專案小幫手。", messages[0].Content);
        Assert.Equal("找專案 A", messages[1].Content);
        Assert.Equal("呼叫工具 search_projects：{\"keyword\":\"A\"}", messages[2].Content);
        Assert.Equal("[{\"title\":\"A\"}]", messages[3].Content);
        Assert.Equal("找到 1 筆。", messages[4].Content);
        Assert.Equal("補充規則", messages[5].Content);
        Assert.Equal("再找 B", messages[6].Content);
    }

    [Fact]
    public void CallLogMessages_ShouldParseResponsesStringInput()
    {
        var messages = AiCallLogMessages.Parse("""{ "model": "m", "input": "你好" }""");

        Assert.Equal([new AiCallLogMessage("user", "你好")], messages);
    }

    /// <summary>既有規則不變：看不懂的格式回 null，不丟例外。</summary>
    [Theory]
    [InlineData("""{ "model": "m" }""")]
    [InlineData("""{ "input": 42 }""")]
    [InlineData("not json")]
    public void CallLogMessages_UnknownFormat_ShouldReturnNull(string body)
        => Assert.Null(AiCallLogMessages.Parse(body));
}
