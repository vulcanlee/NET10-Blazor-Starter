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

    /// <summary>中文不跳脫，和 OpenAI 實際送出的一樣，測試也才找得到原文片段。</summary>
    private static readonly JsonSerializerOptions RelaxedJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static HttpResponseMessage Response(string sse)
        => new(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };

    private static string Chunk(string delta, string? finishReason)
        => $$"""{"id":"c1","object":"chat.completion.chunk","created":1,"model":"{{Model}}","choices":[{"index":0,"delta":{{delta}},"finish_reason":{{(finishReason is null ? "null" : $"\"{finishReason}\"")}}}]}""";

    private static void Append(StringBuilder builder, string json) => builder.Append("data: ").Append(json).Append("\n\n");
}
