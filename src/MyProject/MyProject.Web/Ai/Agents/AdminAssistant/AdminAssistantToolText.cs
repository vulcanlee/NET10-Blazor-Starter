using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyProject.Web.Ai.Agents.AdminAssistant;

/// <summary>
/// 系統管理助理工具的共用小工具（0.9.155 起）：模型給的時間字串、筆數夾範圍、截斷、JSON 輸出。
/// 原則：參數不合法時不丟例外，改用預設值並在回覆中說明實際套用了什麼。
/// </summary>
public static class AdminAssistantToolText
{
    /// <summary>給工具稽核 detail 的來源註記。</summary>
    public const string AuditSource = "admin-assistant";

    /// <summary>模型可用的時間格式（伺服器本地時間）。</summary>
    public const string TimeFormatHint = "yyyy-MM-dd HH:mm 或 yyyy-MM-dd（伺服器本地時間）";

    private static readonly string[] DateTimeFormats = ["yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy/MM/dd HH:mm", "yyyy/MM/dd HH:mm:ss"];

    private static readonly string[] DateOnlyFormats = ["yyyy-MM-dd", "yyyy/MM/dd"];

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// 解析查詢區間。沒給用 <paramref name="defaultSpan"/>（結束＝現在）；解析不了的同樣用預設並在 Note 說明；
    /// 起訖顛倒時改用預設；超過 <paramref name="maxSpan"/> 時夾住起點。沒有調整時 Note 為 null。
    /// </summary>
    public static (DateTime Start, DateTime End, string? Note) ParseRange(
        string? start, string? end, DateTime now, TimeSpan defaultSpan, TimeSpan maxSpan)
    {
        var notes = new List<string>();
        var parsedEnd = Parse(end, "結束時間", notes, out var endIsDateOnly) ?? now;
        if (endIsDateOnly)
        {
            // 只給日期的結束時間指的是「那一天」：當成當天結束，否則整天都會被漏掉。
            parsedEnd = parsedEnd.Date.AddDays(1).AddTicks(-1);
            notes.Add($"結束時間只有日期，視為當天 23:59:59（{Format(parsedEnd)}）");
        }

        var parsedStart = Parse(start, "起始時間", notes, out _) ?? parsedEnd - defaultSpan;

        if (parsedStart > parsedEnd)
        {
            notes.Add("起始時間晚於結束時間，改用結束時間往前的預設區間");
            parsedStart = parsedEnd - defaultSpan;
        }

        if (parsedEnd - parsedStart > maxSpan)
        {
            notes.Add($"區間超過上限 {maxSpan.TotalDays:0.#} 天，起始時間改為 {Format(parsedEnd - maxSpan)}");
            parsedStart = parsedEnd - maxSpan;
        }

        return (parsedStart, parsedEnd, notes.Count == 0 ? null : string.Join("；", notes) + "。");
    }

    public static int Clamp(int value, int min, int max) => Math.Min(Math.Max(value, min), max);

    /// <summary>超過 <paramref name="max"/> 字就截斷並加「…」；null 回空字串。</summary>
    public static string Truncate(string? text, int max)
        => text is null ? string.Empty : text.Length <= max ? text : string.Concat(text.AsSpan(0, max), "…");

    /// <summary>回給模型的時間格式（本地時間）。</summary>
    public static string Format(DateTime time) => time.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string? Format(DateTime? time) => time is { } value ? Format(value) : null;

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, Json);

    private static DateTime? Parse(string? text, string name, List<string> notes, out bool isDateOnly)
    {
        isDateOnly = false;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (DateTime.TryParseExact(trimmed, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
        {
            return value;
        }

        if (DateTime.TryParseExact(trimmed, DateOnlyFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
        {
            isDateOnly = true;
            return value;
        }

        notes.Add($"{name}「{text}」無法解析（格式：{TimeFormatHint}），改用預設");
        return null;
    }
}
