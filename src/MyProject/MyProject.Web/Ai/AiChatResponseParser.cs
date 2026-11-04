using System.Text;
using System.Text.Json;

namespace MyProject.Web.Ai;

/// <summary>
/// Chat Completions 與 Responses API（0.9.149 起，Agent 功能）回應的解析。純函式，可用假 JSON 完整測試。
/// 兩種格式由內容自動判斷：根物件有 <c>output</c> 陣列就是 Responses。
///
/// 刻意用 <see cref="JsonDocument"/> 而非 POCO 反序列化：<c>usage</c> 的子物件
/// （<c>prompt_tokens_details</c> / <c>completion_tokens_details</c>）在不同供應商、
/// 不同 api-version、不同模型上時有時無，<c>TryGetProperty</c> 直接表達「缺就是 null」，
/// 不用為每個可選欄位配一顆 nullable POCO。
/// </summary>
public static class AiChatResponseParser
{
    public static AiChatParseResult Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (IsResponsesObject(root))
        {
            return ParseResponsesObject(root);
        }

        var content = string.Empty;
        var finishReason = string.Empty;
        var filterCategory = string.Empty;

        // ⚠️ Azure 在 prompt 被內容過濾時會回 choices: []，所以不能直接取 [0]。
        if (root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];

            // ⚠️ Azure 在回應被內容過濾時，message.content 會是 JSON null 而不是缺欄位，
            // 所以必須檢查 ValueKind 而非只看欄位存在。
            if (choice.TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var contentElement)
                && contentElement.ValueKind == JsonValueKind.String)
            {
                content = contentElement.GetString() ?? string.Empty;
            }

            finishReason = ReadString(choice, "finish_reason");
            filterCategory = ReadFilterCategory(choice);
        }

        return new AiChatParseResult
        {
            Content = content,
            FinishReason = finishReason,
            ModelName = ReadString(root, "model"),
            Usage = ReadUsage(root),
            FilterCategory = filterCategory,
        };
    }

    /// <summary>
    /// 讀取錯誤細節。兩家的錯誤形狀相同：
    /// <c>{"error":{"code":"...","message":"...","param":"...","type":"..."}}</c>。
    ///
    /// <para>
    /// <c>message</c> 會截斷至 <see cref="AiUpstreamError.MaxMessageLength"/> 字元 ——
    /// 上游對某些錯誤的說明可能夾帶提示詞片段，而提示詞裡是上百筆日誌。
    /// </para>
    /// </summary>
    public static bool TryGetError(string json, out AiUpstreamError error)
    {
        error = new AiUpstreamError();
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.TryGetProperty("error", out var node) == false
                || node.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            error = new AiUpstreamError
            {
                Code = ReadString(node, "code"),
                Param = ReadString(node, "param"),
                Type = ReadString(node, "type"),
                Message = Truncate(ReadString(node, "message")),
            };

            return error.HasAny;
        }
        catch (JsonException)
        {
            // 上游可能回 HTML（例如閘道錯誤頁），當作無法解析。
            return false;
        }
    }

    private static string Truncate(string value)
        => value.Length <= AiUpstreamError.MaxMessageLength
            ? value
            : string.Concat(value.AsSpan(0, AiUpstreamError.MaxMessageLength), "…");

    /// <summary>
    /// 取出回應中 <c>usage</c> 子物件的原始 JSON 文字，供「Token 用量」頁保留完整明細
    /// （供應商日後新增欄位也不會漏接）。
    ///
    /// ⚠️ <b>只取 usage 這一段，絕不回傳整個回應 body</b> —— body 裡有模型產生的內文，
    /// 而本專案的提示詞就是日誌內容。存進任何可視範圍更廣的地方都等於繞過日誌頁的管理員限制。
    /// </summary>
    public static string? ExtractUsageJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object
                ? usage.GetRawText()
                : null;
        }
        catch (JsonException)
        {
            // 上游可能回 HTML（例如閘道錯誤頁），當作沒有 usage。
            return null;
        }
    }

    /// <summary>
    /// 解析串流回應（Server-Sent Events）的原文（0.9.141 起）。規則與 <see cref="Parse"/> 相同：
    /// 串接每個 chunk 的 <c>choices[0].delta.content</c>，<c>finish_reason</c>、<c>model</c>、過濾類別取最後一個有值的，
    /// usage 取自帶 usage 的那個 chunk（OpenAI／Azure 都放在最後、<c>choices</c> 為空）。
    ///
    /// ⚠️ 中途取消時原文只有前幾個 chunk，最後一行還可能不完整，所以逐行解析、壞掉的行略過，能讀多少算多少。
    /// </summary>
    public static AiChatParseResult ParseStream(string sse)
    {
        if (IsResponsesStream(sse))
        {
            return ParseResponsesStream(sse);
        }

        var content = new StringBuilder();
        var finishReason = string.Empty;
        var modelName = string.Empty;
        var filterCategory = string.Empty;
        AiTokenUsage? usage = null;

        foreach (var root in EnumerateStreamChunks(sse))
        {
            var model = ReadString(root, "model");
            if (model.Length > 0)
            {
                modelName = model;
            }

            usage = ReadUsage(root) ?? usage;

            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("delta", out var delta)
                    && delta.ValueKind == JsonValueKind.Object
                    && delta.TryGetProperty("content", out var piece)
                    && piece.ValueKind == JsonValueKind.String)
                {
                    content.Append(piece.GetString());
                }

                var finish = ReadString(choice, "finish_reason");
                if (finish.Length > 0)
                {
                    finishReason = finish;
                }

                var category = ReadFilterCategory(choice);
                if (category.Length > 0)
                {
                    filterCategory = category;
                }
            }
        }

        return new AiChatParseResult
        {
            Content = content.ToString(),
            FinishReason = finishReason,
            ModelName = modelName,
            Usage = usage,
            FilterCategory = filterCategory,
        };
    }

    /// <summary>
    /// 串流版的 <see cref="ExtractUsageJson"/>：只回帶 usage 那個 chunk 的 usage 子物件原文。
    /// ⚠️ 紅線同上：絕不回傳 chunk 或整段原文 —— 裡面有模型產生的內文。
    /// </summary>
    public static string? ExtractStreamUsageJson(string sse)
    {
        string? usageJson = null;
        foreach (var root in EnumerateStreamChunks(sse))
        {
            // Responses API 的 usage 在結束事件的 response 物件裡（0.9.149 起）。
            var holder = TryGetEventResponse(root, out var response) ? response : root;
            if (holder.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                usageJson = usage.GetRawText();
            }
        }

        return usageJson;
    }

    // ---------- Responses API（0.9.149 起，Agent 功能） ----------

    private static bool IsResponsesObject(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("output", out var output)
            && output.ValueKind == JsonValueKind.Array;

    /// <summary>Responses 串流的 data 行都帶 <c>"type":"response.…"</c>；Chat Completions 的 chunk 沒有。</summary>
    private static bool IsResponsesStream(string sse)
        => EnumerateStreamChunks(sse).Take(1).Any(root => ReadString(root, "type").StartsWith("response.", StringComparison.Ordinal));

    /// <summary>
    /// 一個完整的 Responses 物件（非串流回應，或串流結束事件裡的 <c>response</c>）。
    /// 文字是所有 message 項目裡 output_text 的串接；只要求呼叫工具時為空字串（由記帳層看 SDK 的工具呼叫判斷）。
    /// </summary>
    private static AiChatParseResult ParseResponsesObject(JsonElement response)
    {
        var content = new StringBuilder();
        foreach (var item in response.GetProperty("output").EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || ReadString(item, "type") != "message"
                || item.TryGetProperty("content", out var parts) == false
                || parts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in parts.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object && ReadString(part, "type") == "output_text")
                {
                    content.Append(ReadString(part, "text"));
                }
            }
        }

        var (finishReason, filterCategory) = ReadResponsesFinish(response);
        return new AiChatParseResult
        {
            Content = content.ToString(),
            FinishReason = finishReason,
            ModelName = ReadString(response, "model"),
            Usage = ReadResponsesUsage(response),
            FilterCategory = filterCategory,
        };
    }

    /// <summary>
    /// Responses 串流：有結束事件（completed／incomplete／failed）就以它帶的完整 response 為準；
    /// 中途取消沒有結束事件時，內容由已收到的 <c>response.output_text.delta</c> 串起來，用量為空。
    /// </summary>
    private static AiChatParseResult ParseResponsesStream(string sse)
    {
        var deltas = new StringBuilder();
        var modelName = string.Empty;
        AiChatParseResult? final = null;

        foreach (var root in EnumerateStreamChunks(sse))
        {
            if (ReadString(root, "type") == "response.output_text.delta")
            {
                deltas.Append(ReadString(root, "delta"));
                continue;
            }

            if (TryGetEventResponse(root, out var response))
            {
                var model = ReadString(response, "model");
                modelName = model.Length > 0 ? model : modelName;
                if (IsResponsesObject(response) && IsFinalEvent(ReadString(root, "type")))
                {
                    final = ParseResponsesObject(response);
                }
            }
        }

        if (final is null)
        {
            return new AiChatParseResult { Content = deltas.ToString(), ModelName = modelName };
        }

        return string.IsNullOrEmpty(final.Content) && deltas.Length > 0
            ? final with { Content = deltas.ToString() }
            : final;
    }

    private static bool IsFinalEvent(string type)
        => type is "response.completed" or "response.incomplete" or "response.failed";

    /// <summary>Responses 串流事件裡的 <c>response</c> 物件（created／in_progress／completed…才有）。</summary>
    private static bool TryGetEventResponse(JsonElement root, out JsonElement response)
    {
        response = default;
        return ReadString(root, "type").StartsWith("response.", StringComparison.Ordinal)
            && root.TryGetProperty("response", out response)
            && response.ValueKind == JsonValueKind.Object;
    }

    /// <summary>
    /// 把 Responses 的 status 對應成 Chat Completions 的 finish_reason，空回應訊息與「被截斷」判斷才能共用：
    /// completed→stop；incomplete＋max_output_tokens→length；incomplete＋content_filter→content_filter。
    /// Azure 另在 <c>content_filters[]</c> 標 <c>blocked</c>，取第一個被過濾的類別。
    /// </summary>
    private static (string FinishReason, string FilterCategory) ReadResponsesFinish(JsonElement response)
    {
        var status = ReadString(response, "status");
        var reason = response.TryGetProperty("incomplete_details", out var details) && details.ValueKind == JsonValueKind.Object
            ? ReadString(details, "reason")
            : string.Empty;

        var finishReason = status switch
        {
            "completed" => "stop",
            "incomplete" when reason == "max_output_tokens" => "length",
            "incomplete" when reason.Length > 0 => reason,
            _ => status,
        };

        var filterCategory = ReadAzureBlockedCategory(response);
        if (filterCategory.Length == 0 && finishReason == "content_filter")
        {
            filterCategory = "content_filter";
        }

        return (finishReason, filterCategory);
    }

    private static string ReadAzureBlockedCategory(JsonElement response)
    {
        if (response.TryGetProperty("content_filters", out var filters) == false || filters.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var filter in filters.EnumerateArray())
        {
            if (filter.ValueKind == JsonValueKind.Object
                && filter.TryGetProperty("blocked", out var blocked)
                && blocked.ValueKind == JsonValueKind.True)
            {
                var category = ReadFilterCategory(filter);
                if (category.Length > 0)
                {
                    return category;
                }
            }
        }

        return string.Empty;
    }

    private static AiTokenUsage? ReadResponsesUsage(JsonElement response)
    {
        if (response.TryGetProperty("usage", out var usage) == false || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new AiTokenUsage
        {
            InputCount = ReadInt(usage, "input_tokens"),
            OutputCount = ReadInt(usage, "output_tokens"),
            TotalCount = ReadInt(usage, "total_tokens"),
            CachedInputCount = ReadNestedInt(usage, "input_tokens_details", "cached_tokens"),
            ReasoningCount = ReadNestedInt(usage, "output_tokens_details", "reasoning_tokens"),
        };

        return result.HasAny ? result : null;
    }

    /// <summary>逐一取出 SSE 裡每個 <c>data:</c> 行的 JSON 物件；<c>[DONE]</c>、非 JSON、不完整的行一律略過。</summary>
    private static IEnumerable<JsonElement> EnumerateStreamChunks(string sse)
    {
        if (string.IsNullOrWhiteSpace(sse))
        {
            yield break;
        }

        foreach (var rawLine in sse.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("data:", StringComparison.Ordinal) == false)
            {
                continue;
            }

            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0 || payload == "[DONE]")
            {
                continue;
            }

            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(payload);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                yield return root;
            }
        }
    }

    private static AiTokenUsage? ReadUsage(JsonElement root)
    {
        if (root.TryGetProperty("usage", out var usage) == false || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new AiTokenUsage
        {
            InputCount = ReadInt(usage, "prompt_tokens"),
            OutputCount = ReadInt(usage, "completion_tokens"),
            TotalCount = ReadInt(usage, "total_tokens"),
            CachedInputCount = ReadNestedInt(usage, "prompt_tokens_details", "cached_tokens"),
            ReasoningCount = ReadNestedInt(usage, "completion_tokens_details", "reasoning_tokens"),
        };

        return result.HasAny ? result : null;
    }

    /// <summary>取 content_filter_results 裡第一個 filtered 為 true 的類別名稱。</summary>
    private static string ReadFilterCategory(JsonElement choice)
    {
        if (choice.TryGetProperty("content_filter_results", out var filters) == false
            || filters.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var category in filters.EnumerateObject())
        {
            if (category.Value.ValueKind == JsonValueKind.Object
                && category.Value.TryGetProperty("filtered", out var filtered)
                && filtered.ValueKind == JsonValueKind.True)
            {
                return category.Name;
            }
        }

        return string.Empty;
    }

    private static int? ReadInt(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
            ? number
            : null;

    private static int? ReadNestedInt(JsonElement parent, string objectName, string name)
        => parent.TryGetProperty(objectName, out var child) && child.ValueKind == JsonValueKind.Object
            ? ReadInt(child, name)
            : null;

    private static string ReadString(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
