using System.Text.Json;
using MyProject.Models.AdapterModel;
using MyProject.Share.Helpers;

namespace MyProject.Models.Systems;

/// <summary>
/// 一次 AI 呼叫的完整紀錄：既是 <c>IAiCallLogRecorder.RecordAsync</c> 的輸入，也是內容檔的 JSON 結構。
///
/// ⚠️ <b>屬性名稱就是檔案格式</b>：改名會讓舊檔讀不回來。結構有不相容變動時請遞增 <see cref="SchemaVersion"/>。
///
/// ⚠️ 絕不可放入 HTTP 標頭：API 金鑰只在單次請求的標頭上。<see cref="Endpoint"/> 只是請求 URI，不含金鑰。
/// </summary>
public sealed class AiCallLogEntry
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>本次呼叫的識別碼，與 TokenUsageLog.CallId 相同，用來雙向關聯。</summary>
    public Guid CallId { get; init; }

    /// <summary>送出時間（本地時間，與 TokenUsageLog 一致）。</summary>
    public DateTime OccurredAt { get; init; }

    public string Operation { get; init; } = string.Empty;

    public string Provider { get; init; } = string.Empty;

    /// <summary>設定上要求的模型。</summary>
    public string RequestedModel { get; init; } = string.Empty;

    /// <summary>實際使用的模型（優先取回應的 model，空的才退回設定值）。</summary>
    public string Model { get; init; } = string.Empty;

    public string? Account { get; init; }

    public int? UserId { get; init; }

    public string Endpoint { get; init; } = string.Empty;

    /// <summary>可讀的關聯說明，例如「例外紀錄 #42（NullReferenceException）」、「日誌 …～…，送出 n／N 筆」。</summary>
    public string? RelatedInfo { get; init; }

    /// <summary>同一段對話的識別碼（例外分析與其追問共用）；其他作業為 null。</summary>
    public Guid? ConversationId { get; init; }

    /// <summary>
    /// 同一次執行的識別碼（0.9.142 起）：Agent 一次執行裡的工具呼叫迴圈會呼叫模型很多次，這幾筆共用同一個 RunId。
    /// 0.9.141 之前的紀錄為 null。
    /// </summary>
    public Guid? RunId { get; init; }

    /// <summary>原始請求 JSON（逐字）。</summary>
    public string RequestBody { get; init; } = string.Empty;

    /// <summary>HTTP 狀態碼；沒拿到回應（逾時、連線失敗、取消）時為 null。</summary>
    public int? HttpStatus { get; init; }

    /// <summary>原始回應 body（含錯誤 body）；沒拿到回應時為 null。</summary>
    public string? ResponseBody { get; init; }

    /// <summary>解析後的回應文字；沒拿到或解析不出時為空字串。</summary>
    public string ResponseText { get; init; } = string.Empty;

    public string? FinishReason { get; init; }

    public bool Success { get; init; }

    public string? FailureReason { get; init; }

    /// <summary>例外型別名稱（逾時、連線失敗、非預期錯誤時）。</summary>
    public string? ExceptionType { get; init; }

    public long ElapsedMilliseconds { get; init; }
}

/// <summary>請求中的一則訊息。</summary>
public sealed record AiCallLogMessage(string Role, string Content);

/// <summary>從原始請求 JSON 解析出 messages（讀取時才解析，不另外存一份）。</summary>
public static class AiCallLogMessages
{
    /// <summary>
    /// 解析 Chat Completions 的 <c>messages[].role/content</c>，或 Responses API（0.9.149 起，Agent 功能）的
    /// <c>instructions</c>＋<c>input</c>。JSON 無效或兩種都不是時回 null（不丟例外）。
    /// </summary>
    public static IReadOnlyList<AiCallLogMessage>? Parse(string? requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(requestBody);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (document.RootElement.TryGetProperty("messages", out var messages) == false
                || messages.ValueKind != JsonValueKind.Array)
            {
                return ParseResponsesRequest(document.RootElement);
            }

            var result = new List<AiCallLogMessage>();
            foreach (var message in messages.EnumerateArray())
            {
                var role = message.TryGetProperty("role", out var roleElement) && roleElement.ValueKind == JsonValueKind.String
                    ? roleElement.GetString() ?? string.Empty
                    : string.Empty;

                var content = string.Empty;
                if (message.TryGetProperty("content", out var contentElement))
                {
                    content = contentElement.ValueKind == JsonValueKind.String
                        ? contentElement.GetString() ?? string.Empty
                        : contentElement.GetRawText();
                }

                result.Add(new AiCallLogMessage(role, content));
            }

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Responses API 的請求：<c>instructions</c> → system；<c>input</c> 為字串 → user；
    /// 陣列裡有 role 的 message 照原角色（developer 視為 system），<c>function_call</c> → assistant「呼叫工具 名稱：參數」，
    /// <c>function_call_output</c> → tool，<c>reasoning</c> 等其他項目略過。
    /// </summary>
    private static List<AiCallLogMessage>? ParseResponsesRequest(JsonElement root)
    {
        var hasInstructions = root.TryGetProperty("instructions", out var instructions) && instructions.ValueKind == JsonValueKind.String;
        var hasInput = root.TryGetProperty("input", out var input)
            && input.ValueKind is JsonValueKind.String or JsonValueKind.Array;
        if (hasInput == false)
        {
            return null;
        }

        var result = new List<AiCallLogMessage>();
        if (hasInstructions)
        {
            result.Add(new AiCallLogMessage("system", instructions.GetString() ?? string.Empty));
        }

        if (input.ValueKind == JsonValueKind.String)
        {
            result.Add(new AiCallLogMessage("user", input.GetString() ?? string.Empty));
            return result;
        }

        foreach (var item in input.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = ReadString(item, "type");
            var role = ReadString(item, "role");
            if (type == "function_call")
            {
                result.Add(new AiCallLogMessage("assistant", $"呼叫工具 {ReadString(item, "name")}：{ReadString(item, "arguments")}"));
            }
            else if (type == "function_call_output")
            {
                result.Add(new AiCallLogMessage("tool", ReadText(item, "output")));
            }
            else if (role.Length > 0 && (type.Length == 0 || type == "message"))
            {
                result.Add(new AiCallLogMessage(role == "developer" ? "system" : role, ReadText(item, "content")));
            }
        }

        return result;
    }

    /// <summary>字串直接取；parts 陣列串接每段的 <c>text</c>（input_text／output_text）；其他形狀保留原始 JSON。</summary>
    private static string ReadText(JsonElement parent, string name)
    {
        if (parent.TryGetProperty(name, out var value) == false)
        {
            return string.Empty;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var texts = value.EnumerateArray()
                .Where(part => part.ValueKind == JsonValueKind.Object
                    && part.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String)
                .Select(part => part.GetProperty("text").GetString())
                .ToList();
            if (texts.Count > 0)
            {
                return string.Concat(texts);
            }
        }

        return value.GetRawText();
    }

    private static string ReadString(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

/// <summary>AI 對話紀錄的查詢條件。比照 <see cref="TokenUsageQuery"/>，不污染共用的 DataRequest。</summary>
public class AiCallLogQuery
{
    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    public string Operation { get; set; } = string.Empty;

    /// <summary>帳號，部分比對。</summary>
    public string Account { get; set; } = string.Empty;

    /// <summary>成敗篩選：true 只看成功、false 只看失敗、null 不限。</summary>
    public bool? Success { get; set; }

    public string Model { get; set; } = string.Empty;

    /// <summary>關聯說明關鍵字，部分比對。</summary>
    public string Keyword { get; set; } = string.Empty;

    /// <summary>
    /// 內文關鍵字（0.9.169 起），只給 <c>AiCallLogService.FindContentMatchesAsync</c> 用：逐檔掃描，不分大小寫。
    /// 一般查詢（<c>GetAsync</c>）不看這個欄位，改用掃描結果的 <see cref="Ids"/>。
    /// </summary>
    public string ContentKeyword { get; set; } = string.Empty;

    /// <summary>只取這些 Id（0.9.169 起，內文搜尋的結果）；null 代表不限，空集合代表一筆都沒有。</summary>
    public IReadOnlyCollection<int>? Ids { get; set; }

    public int CurrentPage { get; set; } = 1;

    public int PageSize { get; set; } = MagicObjectHelper.PageSize;

    public string SortField { get; set; } = string.Empty;

    public bool? SortDescending { get; set; }
}

/// <summary>下拉的可選值，只列出資料庫中實際出現過的。</summary>
public class AiCallLogFilterOptions
{
    public List<string> Operations { get; set; } = new();

    public List<string> Models { get; set; } = new();
}

/// <summary>內文搜尋的結果（0.9.169 起）。</summary>
public sealed class AiCallLogContentSearchResult
{
    /// <summary>內文符合的紀錄 Id，依送出時間由新到舊。</summary>
    public IReadOnlyList<int> Ids { get; init; } = [];

    /// <summary>掃描了幾筆（篩選結果中最新的幾筆；內容檔不存在的也算，只是不會符合）。</summary>
    public int Scanned { get; init; }

    /// <summary>篩選結果超過掃描上限，較舊的沒有掃到。</summary>
    public bool Truncated { get; init; }
}

/// <summary>合併匯出 PDF 的一筆（0.9.169 起）。</summary>
public sealed class AiCallLogExportItem
{
    public AiCallLogAdapterModel Item { get; init; } = new();

    /// <summary>由原始請求解析出的訊息；內容檔不存在時為空。</summary>
    public IReadOnlyList<AiCallLogMessage> Messages { get; init; } = [];

    /// <summary>取得的回應文字；沒有時為空字串。</summary>
    public string ResponseText { get; init; } = string.Empty;

    public TokenUsageLogAdapterModel? Usage { get; init; }

    /// <summary>內容檔不存在或無法讀取（已清除、還原的備份沒含內容檔，或檔案損毀）。</summary>
    public bool ContentMissing { get; init; }
}

/// <summary>明細視窗需要的全部資料。</summary>
public sealed class AiCallLogDetail
{
    public AiCallLogAdapterModel Item { get; init; } = new();

    /// <summary>內容檔；檔案不存在或無法解析時為 null。</summary>
    public AiCallLogEntry? Content { get; init; }

    /// <summary>由原始請求解析出的訊息；無法解析時為 null。</summary>
    public IReadOnlyList<AiCallLogMessage>? Messages { get; init; }

    /// <summary>對應的 Token 用量紀錄（以 CallId 關聯）；沒有時為 null。</summary>
    public TokenUsageLogAdapterModel? Usage { get; init; }

    /// <summary>同一對話的所有紀錄（依時間由舊到新）；不屬於任何對話時為空。</summary>
    public IReadOnlyList<AiCallLogAdapterModel> Conversation { get; init; } = [];

    /// <summary>同一次執行的所有紀錄（0.9.142 起，依時間由舊到新）；沒有 RunId 時為空。</summary>
    public IReadOnlyList<AiCallLogAdapterModel> Run { get; init; } = [];
}
