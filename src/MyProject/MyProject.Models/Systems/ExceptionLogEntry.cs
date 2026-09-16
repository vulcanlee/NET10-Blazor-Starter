namespace MyProject.Models.Systems;

/// <summary>
/// 例外記錄管線在「生產端」（ILoggerProvider）與「消費端」（背景寫入器 → 服務層）之間傳遞的資料。
///
/// 放在 MyProject.Models 是因為 Web（生產者）與 Business（消費者）都要看得到它，
/// 而 Models 不相依任何其他專案，符合分層規範。
/// </summary>
public sealed class ExceptionLogEntry
{
    /// <summary>例外型別全名。</summary>
    public string ExceptionType { get; init; } = string.Empty;

    /// <summary>ex.Message（已截斷）。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>ex.ToString() 全文，寫入堆疊檔用。不進資料庫。</summary>
    public string StackTrace { get; init; } = string.Empty;

    /// <summary>來源，見 <see cref="ExceptionSources"/>。</summary>
    public string Source { get; init; } = ExceptionSources.Unknown;

    public string? Page { get; init; }

    /// <summary>
    /// 操作＝日誌訊息「樣板」（{OriginalFormat}），例如
    /// <c>Failed to create category. Name={CategoryName}</c>。
    /// ⚠️ 絕不可放算好的訊息，否則每個參數值都會變成一個新簽章。
    /// </summary>
    public string? Operation { get; init; }

    public string? LoggerName { get; init; }

    public string? Account { get; init; }

    public int? UserId { get; init; }

    public DateTime OccurredAt { get; init; } = DateTime.Now;
}

/// <summary>
/// 例外的來源分類。刻意用字串常數而非 enum —— 值會直接寫進資料庫並顯示在畫面上，
/// 用 enum 還要多一層翻譯。
/// </summary>
public static class ExceptionSources
{
    /// <summary>Blazor 畫面互動，或非 /api 的 HTTP 請求。</summary>
    public const string Ui = "畫面";

    /// <summary>路徑以 /api 開頭的 Web API 請求。</summary>
    public const string WebApi = "WebAPI";

    /// <summary>系統啟動流程（migration、RBAC 回填等）。</summary>
    public const string Startup = "系統啟動";

    /// <summary>背景作業。目前專案只有例外寫入器本身，保留給日後使用。</summary>
    public const string Background = "背景作業";

    /// <summary>沒有任何情境資訊可用時的預設值。</summary>
    public const string Unknown = "未知";

    public static IReadOnlyList<string> All { get; } =
    [
        Ui,
        WebApi,
        Startup,
        Background,
        Unknown,
    ];
}
