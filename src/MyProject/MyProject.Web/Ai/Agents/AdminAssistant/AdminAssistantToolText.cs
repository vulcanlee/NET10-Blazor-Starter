using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MyProject.Web.Auditing;

namespace MyProject.Web.Ai.Agents.AdminAssistant;

/// <summary>
/// 系統管理助理工具的共用小工具（0.9.155 起）：模型給的時間字串、筆數夾範圍、截斷、JSON 輸出。
/// 原則：參數不合法時不丟例外，改用預設值並在回覆中說明實際套用了什麼。
/// </summary>
public static class AdminAssistantToolText
{
    /// <summary>給工具稽核 detail 的來源註記（條件名稱 <c>via</c>，0.9.157 起；0.9.155～0.9.156 的舊紀錄是 <c>source=</c>）。</summary>
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

    /// <summary>夾住範圍；有被夾住時在 <paramref name="notes"/> 加一句說明（0.9.157 起，讓模型照實告訴使用者實際查了幾筆）。</summary>
    public static int ClampWithNote(int value, int min, int max, string name, List<string> notes)
    {
        var clamped = Clamp(value, min, max);
        if (clamped != value)
        {
            notes.Add($"{name} {value} 超出範圍 {min}～{max}，改用 {clamped}");
        }

        return clamped;
    }

    /// <summary>
    /// 原頁面是下拉選單的條件（動作代碼、作業名稱），在工具裡是模型給的文字（0.9.157 起）：
    /// 在已知清單內就照頁面用 <see cref="AuditSearchCondition.Of"/> 記值；不在清單內改用 <see cref="AuditSearchCondition.Text"/>
    /// （稽核 detail 只記有沒有填，不讓任意文字進資料庫），並說明可能查不到資料。空白照常用 Of。
    /// </summary>
    public static AuditSearchCondition ChoiceCondition(string name, string value, IEnumerable<string> known, List<string> notes)
    {
        if (string.IsNullOrEmpty(value) || known.Contains(value, StringComparer.Ordinal))
        {
            return AuditSearchCondition.Of(name, value);
        }

        notes.Add($"{name}「{value}」不在系統已知的清單裡，可能查不到資料；請確認名稱是否正確");
        return AuditSearchCondition.Text(name, value);
    }

    /// <summary>把多段說明合成一句；沒有時為 null（JSON 不輸出）。</summary>
    public static string? JoinNotes(IEnumerable<string?> notes)
    {
        var list = notes.Where(note => string.IsNullOrWhiteSpace(note) == false).Select(note => note!.TrimEnd('。')).ToList();
        return list.Count == 0 ? null : string.Join("；", list) + "。";
    }

    /// <summary>超過 <paramref name="max"/> 字就截斷並加「…」；null 回空字串。</summary>
    public static string Truncate(string? text, int max)
        => text is null ? string.Empty : text.Length <= max ? text : string.Concat(text.AsSpan(0, max), "…");

    /// <summary>回給模型的時間格式（本地時間）。</summary>
    public static string Format(DateTime time) => time.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string? Format(DateTime? time) => time is { } value ? Format(value) : null;

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, Json);

    /// <summary>
    /// 異動工具用的時間（0.9.158 起）：不填回 (null, null)；只給日期時視為當天 00:00，<paramref name="endOfDay"/> 時視為當天 23:59:59
    /// （「公告到 11/10」指的是那一整天）；解析不了回錯誤說明（不像查詢區間會改用預設 —— 寫入資料時猜錯比拒絕更糟）。
    /// </summary>
    public static (DateTime? Value, string? Error) ParseTime(string? text, string name, bool endOfDay = false)
    {
        var notes = new List<string>();
        var value = Parse(text, name, notes, out var isDateOnly);
        if (notes.Count > 0)
        {
            return (null, $"{name}「{text}」無法解析（格式：{TimeFormatHint}）。");
        }

        return (endOfDay && isDateOnly && value is { } date ? date.Date.AddDays(1).AddSeconds(-1) : value, null);
    }

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
