using System.Text;
using System.Text.RegularExpressions;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;

namespace MyProject.Web.Ai.Agents.AdminAssistant;

/// <summary>巡檢報告的一段。</summary>
/// <param name="Heading">段落標題（模型寫的，已去掉 Markdown 符號）。</param>
/// <param name="Status">「狀態：」那一行的值；沒有寫時為 null。</param>
/// <param name="KeyPoints">狀態行之後的重點（純文字）。</param>
public sealed record AdminPatrolSection(string Heading, string? Status, IReadOnlyList<string> KeyPoints)
{
    /// <summary>這一段要不要提醒管理員：注意、異常，或該寫狀態卻沒寫。</summary>
    public bool NeedsAttention => Status != AdminPatrolStatuses.Normal;

    /// <summary>存進資料表用的固定名稱：認得的段落用固定名稱，其他一律 <see cref="AdminPatrolSections.Other"/>。</summary>
    public string StoredName => AdminPatrolSections.Known.FirstOrDefault(name => Heading.Contains(name, StringComparison.Ordinal))
        ?? AdminPatrolSections.Other;
}

/// <summary>巡檢報告的判讀結果。</summary>
/// <param name="Overall">整體狀態（<see cref="AdminPatrolStatuses"/>）。</param>
/// <param name="Sections">會判斷狀態的段落（不含總結與建議處理）。</param>
/// <param name="Summary">「總結」段的純文字。</param>
/// <param name="Reason">整體狀態不是單純取各段最嚴重者時的說明（判讀不出來、回應被截斷）。</param>
public sealed record AdminPatrolAssessment(string Overall, IReadOnlyList<AdminPatrolSection> Sections, string? Summary, string? Reason)
{
    public bool NeedsAttention => AdminPatrolStatuses.NeedsAttention(Overall);

    /// <summary>需要注意的段落（固定名稱、不重複，以「、」串接）；可以安全存進資料表。</summary>
    public string AttentionSectionNames => string.Join(
        "、", Sections.Where(section => section.NeedsAttention).Select(section => section.StoredName).Distinct());
}

/// <summary>
/// 判讀巡檢報告（0.9.161 起，定時巡檢）。報告格式來自 <see cref="AdminAssistantAgent.DefaultCommonInstructions"/>：
/// 「## 段落」，除了總結與建議處理，每段第一行寫「狀態：正常／注意／異常」。只用 <c>##</c> 切段：
/// 模型常在開頭加 <c>#</c> 標題、段落內用 <c>###</c> 分小節，那些只是內容。
///
/// <para>
/// ⚠️ 提示詞可以被管理員在「AI 提示詞」頁改掉，所以判讀寧可保守：判讀不出來算「無法判讀」、固定段落該寫狀態卻沒寫或整段缺漏算「注意」、
/// 寫了「目前無法查詢」至少「注意」、回應被長度截斷至少「注意」—— 這些都會通知管理員，巡檢壞掉才不會沒人發現。
/// 不認得的段落沒寫狀態就略過（例如模型自己加的「補充說明」），寫了狀態才列入。
/// </para>
/// </summary>
public static partial class AdminPatrolReportParser
{
    private const string SummaryHeading = "總結";
    private const string SuggestionHeading = "建議處理";
    private const string QueryFailedMarker = "目前無法查詢";
    private const int SummaryMaxLength = 200;
    private const int PointMaxLength = 200;

    public const string UnreadableReason = "巡檢報告的格式判讀不出來（可能是提示詞被修改，或 AI 沒有照格式回答），請打開完整報告確認。";
    public const string TruncatedReason = "AI 的回應達到長度上限被截斷，報告可能不完整。";

    private static readonly string[] Statuses = [AdminPatrolStatuses.Normal, AdminPatrolStatuses.Attention, AdminPatrolStatuses.Abnormal];

    public static AdminPatrolAssessment Parse(string? markdown, bool isTruncated)
    {
        var sections = new List<AdminPatrolSection>();
        string? summary = null;

        foreach (var (heading, lines) in SplitSections(markdown ?? string.Empty))
        {
            if (heading.Contains(SummaryHeading, StringComparison.Ordinal))
            {
                var text = string.Join(' ', lines.Select(line => ToPlainLine(line, SummaryMaxLength)).Where(line => line.Length > 0));
                summary = text.Length == 0 ? null : Truncate(text, SummaryMaxLength);
                continue;
            }

            if (heading.Contains(SuggestionHeading, StringComparison.Ordinal))
            {
                continue;
            }

            var section = ParseSection(heading, lines);
            if (section.Status is not null || IsKnown(section))
            {
                sections.Add(section);
            }
        }

        if (sections.All(section => section.Status is null))
        {
            return new AdminPatrolAssessment(AdminPatrolStatuses.Unreadable, sections, summary, UnreadableReason);
        }

        var overall = sections.Select(section => section.Status ?? AdminPatrolStatuses.Attention).MaxBy(Severity)!;
        var reasons = new List<string>();
        var missing = AdminPatrolSections.Known.Except(sections.Where(IsKnown).Select(section => section.StoredName)).ToList();
        if (missing.Count > 0)
        {
            reasons.Add($"報告缺少段落：{string.Join("、", missing)}。");
        }

        if (isTruncated)
        {
            reasons.Add(TruncatedReason);
        }

        if (reasons.Count > 0 && Severity(overall) < Severity(AdminPatrolStatuses.Attention))
        {
            overall = AdminPatrolStatuses.Attention;
        }

        return new AdminPatrolAssessment(overall, sections, summary, reasons.Count == 0 ? null : string.Join(' ', reasons));
    }

    /// <summary>
    /// 一行 Markdown → 純文字（通知與 Email 用）：去掉標題、引用、清單符號與粗體／程式碼標記，連結只留文字，
    /// 網址一律換成「（連結已移除）」（模型產生的網址不寄出去），最後截到 <paramref name="maxLength"/> 字。
    /// </summary>
    internal static string ToPlainLine(string line, int maxLength)
    {
        var text = MarkdownLinkPattern().Replace(line, "$1");
        text = UrlPattern().Replace(text, "（連結已移除）");
        text = BareDomainPattern().Replace(text, "（連結已移除）");
        text = text.Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal);
        text = LeadingMarkerPattern().Replace(text, string.Empty);
        text = WhitespacePattern().Replace(text, " ").Trim();
        return Truncate(text, maxLength);
    }

    private static AdminPatrolSection ParseSection(string heading, IReadOnlyList<string> lines)
    {
        string? status = null;
        var points = new List<string>();
        foreach (var line in lines)
        {
            var plain = ToPlainLine(line, PointMaxLength);
            if (plain.Length == 0)
            {
                continue;
            }

            if (status is null && TryReadStatus(plain, out var value))
            {
                status = value;
                continue;
            }

            points.Add(plain);
        }

        // 寫了「目前無法查詢」卻標正常（或沒寫狀態）：至少算注意。
        if (lines.Any(line => line.Contains(QueryFailedMarker, StringComparison.Ordinal))
            && (status is null || Severity(status) < Severity(AdminPatrolStatuses.Attention)))
        {
            status = AdminPatrolStatuses.Attention;
        }

        return new AdminPatrolSection(heading, status, points);
    }

    private static bool IsKnown(AdminPatrolSection section) => section.StoredName != AdminPatrolSections.Other;

    /// <summary>
    /// 「狀態：異常」「狀態：注意（2 個帳號被鎖定）」→ 狀態字必須在最前面：「不正常」「無異常」不採用（只用「包含」會判反）；
    /// 狀態字後面緊接「／」的（模型照抄了「正常／注意／異常」）也不採用。
    /// </summary>
    private static bool TryReadStatus(string plain, out string status)
    {
        status = string.Empty;
        var match = StatusLinePattern().Match(plain);
        if (match.Success == false)
        {
            return false;
        }

        var value = match.Groups[1].Value.Trim();
        foreach (var candidate in Statuses)
        {
            if (value.StartsWith(candidate, StringComparison.Ordinal)
                && (value.Length == candidate.Length || value[candidate.Length] is not ('／' or '/')))
            {
                status = candidate;
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<(string Heading, List<string> Lines)> SplitSections(string markdown)
    {
        string? heading = null;
        var lines = new List<string>();
        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var match = HeadingPattern().Match(line);
            if (match.Success)
            {
                if (heading is not null)
                {
                    yield return (heading, lines);
                }

                heading = ToPlainLine(match.Groups[1].Value, 50);
                lines = [];
                continue;
            }

            lines.Add(line);
        }

        if (heading is not null)
        {
            yield return (heading, lines);
        }
    }

    private static int Severity(string status) => status switch
    {
        AdminPatrolStatuses.Abnormal => 3,
        AdminPatrolStatuses.Attention => 2,
        AdminPatrolStatuses.Normal => 1,
        _ => 0,
    };

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength - 1), "…");

    // 只認 ##（不是 # 也不是 ###）。
    [GeneratedRegex(@"^\s{0,3}##(?!#)\s*(.+?)\s*#*\s*$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^狀態\s*[：:]\s*(.*)$")]
    private static partial Regex StatusLinePattern();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownLinkPattern();

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    // 沒有 http 開頭的網域（郵件軟體大多會自動變成連結）：常見頂級網域才算，「BackendDB.db」「12.4 MB」這類不受影響；Email 地址不動。
    [GeneratedRegex(@"(?<![\w.@/-])(?:[A-Za-z0-9-]+\.)+(?:com|net|org|io|tw|cn|jp|hk|uk|us|info|biz|xyz|top|app|dev|co|me|ly|link|site|online|shop)\b(?:/[^\s，。、）)]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex BareDomainPattern();

    // 數字清單（「1. 」）要後接空白，「3.5 秒」這類數字才不會被吃掉。
    [GeneratedRegex(@"^\s*(?:(?:[#>*+\-]|\d+[.)](?=\s))\s*)+")]
    private static partial Regex LeadingMarkerPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}

/// <summary>
/// 定時巡檢的通知（0.9.161 起）：報告需要注意時，發給全體啟用中的管理員（站內＋Email），內容是摘要＋連結。
///
/// <para>
/// ⚠️ 內文含模型產生的摘錄，而且 Email 會寄出系統外 —— 這是速查表 §6.7「模型內文只進 AI 對話紀錄」的明文例外（使用者 2026/11/07 決定）。
/// 為此只寄純文字、拿掉網址、每段最多 <see cref="PointsPerSection"/> 點、全文不超過 <see cref="BodyMaxLength"/> 字。
/// 必須自己截斷：<see cref="NotificationSender"/> 只截存進資料庫的那份，寄出的信用的是原文。
/// </para>
/// </summary>
public static class AdminPatrolNotification
{
    /// <summary>同 <see cref="NotificationSender"/> 的內文上限。</summary>
    public const int BodyMaxLength = 1000;

    public const int PointsPerSection = 3;

    public const int PointMaxLength = 120;

    /// <summary>內文最後一行（固定保留）：沒設定 PublicBaseUrl 時信裡沒有連結，靠這行指路。</summary>
    public const string Footer = "請到系統管理助理頁的「巡檢報告」查看完整報告。";

    /// <summary>直接打開某份報告的站內連結。</summary>
    public static string LinkOf(Guid reportId) => $"/admin-assistant?patrolReport={reportId:D}";

    public static NotificationRequest Build(Guid reportId, DateTime createdAt, AdminPatrolAssessment assessment)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(assessment.Summary) == false)
        {
            lines.Add(assessment.Summary);
        }

        if (string.IsNullOrEmpty(assessment.Reason) == false)
        {
            lines.Add(assessment.Reason);
        }

        foreach (var section in assessment.Sections.Where(section => section.NeedsAttention))
        {
            lines.Add(string.Empty);
            lines.Add($"【{section.Heading}】{section.Status ?? "沒有標示狀態"}");
            lines.AddRange(section.KeyPoints.Take(PointsPerSection).Select(point => "- " + AdminPatrolReportParser.ToPlainLine(point, PointMaxLength)));
        }

        var content = string.Join('\n', lines).Trim();
        var room = BodyMaxLength - Footer.Length - 2;
        if (content.Length > room)
        {
            content = string.Concat(content.AsSpan(0, room - 1), "…");
        }

        var body = new StringBuilder(content);
        if (body.Length > 0)
        {
            body.Append("\n\n");
        }

        body.Append(Footer);

        return new NotificationRequest(
            NotificationCategories.AdminPatrol,
            $"系統巡檢：{assessment.Overall}（{createdAt:yyyy/MM/dd}）",
            body.ToString(),
            LinkOf(reportId),
            NotificationTarget.AllAdmins(),
            AlsoEmail: true,
            SourceKey: $"AdminPatrol:{reportId:N}");
    }
}
