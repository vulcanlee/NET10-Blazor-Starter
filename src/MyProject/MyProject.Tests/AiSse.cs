using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MyProject.Tests;

/// <summary>
/// 組出 Chat Completions 串流回應（Server-Sent Events）的測試資料，形狀照 OpenAI／Azure 實際送出的格式：
/// 每個 chunk 一行 <c>data: {...}</c>、空行分隔，最後一個 chunk 帶 usage（<c>choices: []</c>），結尾 <c>data: [DONE]</c>。
/// </summary>
internal static class AiSse
{
    public const string Model = "gpt-4o-2024-11-20";

    public const string UsageJson = """{"prompt_tokens":10,"completion_tokens":2,"total_tokens":12}""";

    /// <summary>完整串流。<paramref name="azurePromptFilterFirst"/> 模擬 Azure 第一個只有 prompt_filter_results 的 chunk。</summary>
    public static string Build(
        IEnumerable<string> deltas,
        string finishReason = "stop",
        string? usageJson = UsageJson,
        bool azurePromptFilterFirst = false,
        string? filteredCategory = null)
    {
        var builder = new StringBuilder();
        if (azurePromptFilterFirst)
        {
            Append(builder, """{"id":"","object":"","created":0,"model":"","choices":[],"prompt_filter_results":[{"prompt_index":0,"content_filter_results":{}}]}""");
        }

        Append(builder, Chunk("""{"role":"assistant","content":""}""", null));
        foreach (var delta in deltas)
        {
            Append(builder, Chunk($$"""{"content":{{JsonSerializer.Serialize(delta, RelaxedJson)}}}""", null));
        }

        var filter = filteredCategory is null
            ? string.Empty
            : ",\"content_filter_results\":{\"" + filteredCategory + "\":{\"filtered\":true,\"severity\":\"high\"}}";
        Append(builder, $$"""{"id":"c1","object":"chat.completion.chunk","created":1,"model":"{{Model}}","choices":[{"index":0,"delta":{},"finish_reason":"{{finishReason}}"{{filter}}}]}""");

        if (usageJson is not null)
        {
            Append(builder, $$"""{"id":"c1","object":"chat.completion.chunk","created":1,"model":"{{Model}}","choices":[],"usage":{{usageJson}}}""");
        }

        builder.Append("data: [DONE]\n\n");
        return builder.ToString();
    }

    /// <summary>
    /// 模型要求呼叫工具的串流回應（0.9.142 起）：tool_calls 分兩段送（先名稱、再參數），finish_reason 為 tool_calls，沒有文字。
    /// </summary>
    public static string ToolCall(string callId, string name, string argumentsJson)
    {
        var builder = new StringBuilder();
        Append(builder, $$$"""{"id":"c2","object":"chat.completion.chunk","created":1,"model":"{{{Model}}}","choices":[{"index":0,"delta":{"role":"assistant","content":null,"tool_calls":[{"index":0,"id":"{{{callId}}}","type":"function","function":{"name":"{{{name}}}","arguments":""}}]},"finish_reason":null}]}""");
        Append(builder, $$$"""{"id":"c2","object":"chat.completion.chunk","created":1,"model":"{{{Model}}}","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":{{{JsonSerializer.Serialize(argumentsJson)}}}}}]},"finish_reason":null}]}""");
        Append(builder, $$"""{"id":"c2","object":"chat.completion.chunk","created":1,"model":"{{Model}}","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""");
        Append(builder, $$"""{"id":"c2","object":"chat.completion.chunk","created":1,"model":"{{Model}}","choices":[],"usage":{{UsageJson}}}""");
        builder.Append("data: [DONE]\n\n");
        return builder.ToString();
    }

    /// <summary>Responses API 的 usage（0.9.149 起，Agent 功能）。</summary>
    public const string ResponsesUsageJson = """{"input_tokens":10,"input_tokens_details":{"cached_tokens":4},"output_tokens":2,"output_tokens_details":{"reasoning_tokens":1},"total_tokens":12}""";

    /// <summary>
    /// Responses API 的文字串流（0.9.149 起，Agent 功能走 Responses），事件順序照 2026/11/04 對 Azure gpt-6.1-sol 實測：
    /// created → in_progress → output_item.added → content_part.added → output_text.delta… → output_text.done →
    /// content_part.done → output_item.done → completed（帶完整 response 與 usage）。
    /// </summary>
    public static string Responses(IEnumerable<string> deltas, string? usageJson = ResponsesUsageJson)
    {
        var parts = deltas.ToList();
        var text = string.Concat(parts);
        var builder = new StringBuilder();
        var sequence = 0;
        AppendEvent(builder, "response.created", $$"""{"type":"response.created","sequence_number":{{sequence++}},"response":{{ResponseObject("in_progress", "[]", null)}}}""");
        AppendEvent(builder, "response.in_progress", $$"""{"type":"response.in_progress","sequence_number":{{sequence++}},"response":{{ResponseObject("in_progress", "[]", null)}}}""");
        AppendEvent(builder, "response.output_item.added", $$$"""{"type":"response.output_item.added","output_index":0,"sequence_number":{{{sequence++}}},"item":{"id":"msg_1","type":"message","status":"in_progress","content":[],"role":"assistant"}}""");
        AppendEvent(builder, "response.content_part.added", $$$"""{"type":"response.content_part.added","item_id":"msg_1","output_index":0,"content_index":0,"sequence_number":{{{sequence++}}},"part":{"type":"output_text","annotations":[],"text":""}}""");
        foreach (var delta in parts)
        {
            AppendEvent(builder, "response.output_text.delta", $$"""{"type":"response.output_text.delta","item_id":"msg_1","output_index":0,"content_index":0,"sequence_number":{{sequence++}},"delta":{{Json(delta)}}}""");
        }

        AppendEvent(builder, "response.output_text.done", $$"""{"type":"response.output_text.done","item_id":"msg_1","output_index":0,"content_index":0,"sequence_number":{{sequence++}},"text":{{Json(text)}}}""");
        AppendEvent(builder, "response.content_part.done", $$$"""{"type":"response.content_part.done","item_id":"msg_1","output_index":0,"content_index":0,"sequence_number":{{{sequence++}}},"part":{"type":"output_text","annotations":[],"text":{{{Json(text)}}}}}""");
        var item = $$"""{"id":"msg_1","type":"message","status":"completed","content":[{"type":"output_text","annotations":[],"text":{{Json(text)}}}],"role":"assistant"}""";
        AppendEvent(builder, "response.output_item.done", $$"""{"type":"response.output_item.done","output_index":0,"sequence_number":{{sequence++}},"item":{{item}}}""");
        AppendEvent(builder, "response.completed", $$"""{"type":"response.completed","sequence_number":{{sequence}},"response":{{ResponseObject("completed", $"[{item}]", usageJson)}}}""");
        return builder.ToString();
    }

    /// <summary>Responses API 要求呼叫工具的串流（0.9.149 起）：一個 function_call 項目、參數一段送完，沒有文字。</summary>
    public static string ResponsesToolCall(string callId, string name, string argumentsJson)
    {
        var builder = new StringBuilder();
        var added = $$"""{"id":"fc_1","type":"function_call","status":"in_progress","arguments":"","call_id":"{{callId}}","name":"{{name}}"}""";
        var done = $$"""{"id":"fc_1","type":"function_call","status":"completed","arguments":{{Json(argumentsJson)}},"call_id":"{{callId}}","name":"{{name}}"}""";
        AppendEvent(builder, "response.created", $$"""{"type":"response.created","sequence_number":0,"response":{{ResponseObject("in_progress", "[]", null)}}}""");
        AppendEvent(builder, "response.output_item.added", $$"""{"type":"response.output_item.added","output_index":0,"sequence_number":1,"item":{{added}}}""");
        AppendEvent(builder, "response.function_call_arguments.delta", $$"""{"type":"response.function_call_arguments.delta","item_id":"fc_1","output_index":0,"sequence_number":2,"delta":{{Json(argumentsJson)}}}""");
        AppendEvent(builder, "response.function_call_arguments.done", $$"""{"type":"response.function_call_arguments.done","item_id":"fc_1","output_index":0,"sequence_number":3,"arguments":{{Json(argumentsJson)}}}""");
        AppendEvent(builder, "response.output_item.done", $$"""{"type":"response.output_item.done","output_index":0,"sequence_number":4,"item":{{done}}}""");
        AppendEvent(builder, "response.completed", $$"""{"type":"response.completed","sequence_number":5,"response":{{ResponseObject("completed", $"[{done}]", ResponsesUsageJson)}}}""");
        return builder.ToString();
    }

    private static string ResponseObject(string status, string outputJson, string? usageJson)
        => $$"""{"id":"resp_1","object":"response","created_at":1,"status":"{{status}}","error":null,"incomplete_details":null,"model":"{{Model}}","output":{{outputJson}},"parallel_tool_calls":true,"store":false,"usage":{{usageJson ?? "null"}}}""";

    private static string Json(string value) => JsonSerializer.Serialize(value, RelaxedJson);

    private static void AppendEvent(StringBuilder builder, string name, string json)
        => builder.Append("event: ").Append(name).Append('\n').Append("data: ").Append(json).Append("\n\n");

    /// <summary>中文不跳脫，和 OpenAI 實際送出的一樣，測試也才找得到原文片段。</summary>
    private static readonly JsonSerializerOptions RelaxedJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static HttpResponseMessage Response(string sse)
        => new(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };

    private static string Chunk(string delta, string? finishReason)
        => $$"""{"id":"c1","object":"chat.completion.chunk","created":1,"model":"{{Model}}","choices":[{"index":0,"delta":{{delta}},"finish_reason":{{(finishReason is null ? "null" : $"\"{finishReason}\"")}}}]}""";

    private static void Append(StringBuilder builder, string json) => builder.Append("data: ").Append(json).Append("\n\n");
}
