using System.Globalization;
using System.Text;
using MyProject.Dtos.Shell;

namespace MyProject.Web.Diagnostics;

/// <summary>
/// 瀏覽器端回報的背景資訊（0.10.36 起）：把前端送來的結構化欄位（發生時間、前端指紋、瀏覽器、操作軌跡）整理成文字，
/// 附在 <see cref="BrowserScriptException"/> 的堆疊後面，堆疊檔與日誌檔都看得到。
///
/// 前端送來的一律不信任：去掉查詢字串與片段（可能含個資或 token）、去掉換行等控制字元（避免偽造日誌行）、截短長度、
/// 只留允許的 HTTP 方法與格式正確的追蹤碼。文字由伺服器組，前端不能送現成的段落。
/// </summary>
public static class BrowserReportContext
{
    public const int MaxBreadcrumbs = 20;
    internal const int MaxPathLength = 200;
    internal const int MaxUserAgentLength = 300;
    internal const int MaxAppBuildLength = 50;

    private static readonly HashSet<string> Methods = new(StringComparer.OrdinalIgnoreCase) { "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD" };

    /// <param name="item">前端送來的回報。</param>
    /// <param name="beforeSignIn">發生時還沒登入（或是另一位使用者登入時發生的）。</param>
    /// <param name="systemVersion">伺服器收到時的系統版本（與前端指紋對照，看得出使用者是否還在跑舊前端）。</param>
    /// <param name="localTimeZone">伺服器的時區；時間一律換成伺服器本地時間，與日誌檔一致。</param>
    public static string Format(ClientReportDto item, bool beforeSignIn, string systemVersion, TimeZoneInfo localTimeZone)
    {
        var text = new StringBuilder();
        text.AppendLine("Browser context:");
        if (TraceCode.IsValid(item.TraceCode))
        {
            text.Append("  TraceCode: ").AppendLine(item.TraceCode);
        }

        if (item.OccurredAt is { } occurredAt)
        {
            text.Append("  OccurredAt: ").Append(FormatTime(occurredAt, localTimeZone, "yyyy-MM-dd HH:mm:ss.fff")).AppendLine(" (browser clock)");
        }

        text.Append("  SignedIn: ").AppendLine(beforeSignIn ? "no (reported after sign-in)" : "yes");
        text.Append("  AppBuild: ").AppendLine(Clean(item.AppBuild, MaxAppBuildLength) is { Length: > 0 } build ? build : "(unknown)");
        text.Append("  SystemVersion: ").AppendLine(Clean(systemVersion, MaxAppBuildLength));
        if (Clean(item.UserAgent, MaxUserAgentLength) is { Length: > 0 } userAgent)
        {
            text.Append("  UserAgent: ").AppendLine(userAgent);
        }

        var breadcrumbs = (item.Breadcrumbs ?? []).TakeLast(MaxBreadcrumbs).ToList();
        if (breadcrumbs.Count > 0)
        {
            text.AppendLine("Breadcrumbs (oldest first):");
            foreach (var crumb in breadcrumbs)
            {
                if (FormatBreadcrumb(crumb, localTimeZone) is { } line)
                {
                    text.Append("  ").AppendLine(line);
                }
            }
        }

        return text.ToString().TrimEnd();
    }

    private static string? FormatBreadcrumb(ClientBreadcrumbDto crumb, TimeZoneInfo localTimeZone)
    {
        var time = FormatTime(crumb.At, localTimeZone, "HH:mm:ss.fff");
        var path = StripQuery(crumb.Path);
        switch (crumb.Kind)
        {
            case "nav":
                return $"{time} nav {path}";
            case "api":
                var method = crumb.Method is { } value && Methods.Contains(value) ? value.ToUpperInvariant() : "?";
                var status = crumb.Status is { } code ? code == 0 ? "unreachable" : code.ToString(CultureInfo.InvariantCulture) : "?";
                var elapsed = crumb.ElapsedMs is { } ms ? $" {ms.ToString(CultureInfo.InvariantCulture)}ms" : string.Empty;
                var trace = TraceCode.IsValid(crumb.TraceCode) ? $" {crumb.TraceCode}" : string.Empty;
                return $"{time} api {method} {path} {status}{elapsed}{trace}";
            default:
                return null;
        }
    }

    /// <summary>只留路徑：去掉查詢字串與片段、控制字元，並截短。</summary>
    public static string StripQuery(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var cut = path.IndexOfAny(['?', '#']);
        return Clean(cut >= 0 ? path[..cut] : path, MaxPathLength);
    }

    /// <summary>去掉換行等控制字元並截短；空值回空字串。</summary>
    public static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var cleaned = new string(value.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    private static string FormatTime(DateTimeOffset value, TimeZoneInfo localTimeZone, string format)
        => TimeZoneInfo.ConvertTime(value, localTimeZone).ToString(format, CultureInfo.InvariantCulture);
}
