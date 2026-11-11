using System.Text;
using System.Text.RegularExpressions;
using MyProject.Models.Systems;

namespace MyProject.Web.Ai.Agents.AdminAssistant;

/// <summary>系統管理助理的領域（0.9.155 起）。順序就是畫面標籤的順序。</summary>
public enum AdminAssistantDomain
{
    /// <summary>帳號與權限。</summary>
    Accounts,

    /// <summary>監控與診斷。</summary>
    Monitoring,

    /// <summary>AI 管理。</summary>
    Ai,

    /// <summary>系統設定與維運。</summary>
    Ops,

    /// <summary>巡檢（只用唯讀工具，產出固定格式的巡檢報告）。</summary>
    Patrol,
}

/// <summary>
/// 分派（0.9.155 起）：解析分派模型的回覆、組畫面標籤、組送給分派模型的輸入。
/// 分派模型只回英文代碼（逗號分隔）；讀不出任何代碼時回空集合，由呼叫端退回「全部領域」。
/// </summary>
public static partial class AdminAssistantRouting
{
    /// <summary>沒有分出領域（或分派失敗）時的標籤。</summary>
    public const string DefaultLabel = "系統管理助理";

    /// <summary>送給分派模型的對話紀錄則數上限（只算使用者訊息與 AI 回覆）。</summary>
    public const int RecentEntryCount = 6;

    /// <summary>每則對話紀錄的字數上限。</summary>
    public const int EntryMaxLength = 300;

    private static readonly Dictionary<string, AdminAssistantDomain> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["accounts"] = AdminAssistantDomain.Accounts,
        ["monitoring"] = AdminAssistantDomain.Monitoring,
        ["ai"] = AdminAssistantDomain.Ai,
        ["ops"] = AdminAssistantDomain.Ops,
        ["patrol"] = AdminAssistantDomain.Patrol,
    };

    /// <summary>領域的中文名稱（畫面標籤、提示詞段落標題）。</summary>
    public static string NameOf(AdminAssistantDomain domain) => domain switch
    {
        AdminAssistantDomain.Accounts => "帳號與權限",
        AdminAssistantDomain.Monitoring => "監控與診斷",
        AdminAssistantDomain.Ai => "AI 管理",
        AdminAssistantDomain.Ops => "系統設定與維運",
        AdminAssistantDomain.Patrol => "巡檢",
        _ => throw new ArgumentOutOfRangeException(nameof(domain), domain, null),
    };

    /// <summary>從分派模型的回覆讀出領域代碼（整字、不分大小寫）；讀不到回空集合。</summary>
    public static IReadOnlySet<AdminAssistantDomain> Parse(string? text)
    {
        var domains = new HashSet<AdminAssistantDomain>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return domains;
        }

        foreach (Match match in CodePattern().Matches(text))
        {
            domains.Add(Codes[match.Value]);
        }

        return domains;
    }

    /// <summary>畫面上的「由誰回答」：依領域順序以頓號連接；空集合用 <see cref="DefaultLabel"/>。</summary>
    public static string LabelOf(IReadOnlySet<AdminAssistantDomain> domains)
        => domains.Count == 0
            ? DefaultLabel
            : string.Join("、", Enum.GetValues<AdminAssistantDomain>().Where(domains.Contains).Select(NameOf));

    /// <summary>
    /// 領域組合的代碼（0.10.7 起，存進對話的 TurnKey）：依領域順序以逗號連接英文代碼，空集合是空字串；<see cref="Parse"/> 讀得回同一組。
    /// </summary>
    public static string KeyOf(IReadOnlySet<AdminAssistantDomain> domains)
        => string.Join(",", Enum.GetValues<AdminAssistantDomain>().Where(domains.Contains).Select(domain => Codes.Single(pair => pair.Value == domain).Key));

    /// <summary>送給分派模型的使用者訊息：最近幾則對話（讓接續句也分得對）＋這一則。</summary>
    public static string BuildRouterInput(string message, IReadOnlyList<AiAgentTranscriptEntry> transcript)
    {
        var recent = transcript
            .Where(entry => entry.Kind is AiAgentTranscriptKinds.User or AiAgentTranscriptKinds.Assistant)
            .TakeLast(RecentEntryCount)
            .ToList();

        var builder = new StringBuilder();
        if (recent.Count > 0)
        {
            builder.AppendLine("最近的對話：");
            foreach (var entry in recent)
            {
                var who = entry.Kind == AiAgentTranscriptKinds.User ? "使用者" : "助理";
                var text = entry.Text.Length <= EntryMaxLength ? entry.Text : string.Concat(entry.Text.AsSpan(0, EntryMaxLength), "…");
                builder.Append(who).Append('：').AppendLine(text);
            }

            builder.AppendLine();
        }

        builder.Append("使用者這次的訊息：").Append(message);
        return builder.ToString();
    }

    [GeneratedRegex(@"\b(accounts|monitoring|ai|ops|patrol)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
