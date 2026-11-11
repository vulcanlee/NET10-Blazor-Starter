namespace MyProject.Web.Ai;

/// <summary>
/// 報表要輸出哪些區塊。<see cref="All"/> 之外的每一個值都對應畫面上的一個頁籤 ——
/// 「匯出目前頁籤」就是把目前選中的頁籤鍵轉成這裡的一個值。
///
/// ⚠️ 標題、條件區與合計<b>不受本列舉影響，一律輸出</b>：
/// 一份看不出是在什麼條件下產生的統計沒有意義。
///
/// 0.10.5 起（WASM 遷移 M6）與頁籤鍵的對照一起搬到 Client：WASM 頁面送頁籤鍵、伺服器產生 PDF，兩邊共用同一份對照。
/// </summary>
public enum TokenUsageReportScope
{
    All,
    Trend,
    Daily,
    Account,
    Operation,
    Model,
    CallKind,
    Detail,
}

/// <summary>Token 用量頁的頁籤鍵、報表範圍與頁籤名稱的對照（WASM 頁面與伺服器的 PDF 匯出共用）。</summary>
public static class TokenUsageReportScopes
{
    public const string TabByAccount = "account";
    public const string TabByOperation = "operation";
    public const string TabByModel = "model";
    public const string TabByCallKind = "callkind";
    public const string TabDetail = "detail";
    public const string TabDaily = "daily";
    public const string TabTrend = "trend";

    /// <summary>頁籤鍵對應的報表範圍；空白代表整份（<see cref="TokenUsageReportScope.All"/>），不認得的鍵回 null。</summary>
    public static TokenUsageReportScope? FromTab(string? tab) => tab switch
    {
        null or "" => TokenUsageReportScope.All,
        TabTrend => TokenUsageReportScope.Trend,
        TabDaily => TokenUsageReportScope.Daily,
        TabByAccount => TokenUsageReportScope.Account,
        TabByOperation => TokenUsageReportScope.Operation,
        TabByModel => TokenUsageReportScope.Model,
        TabByCallKind => TokenUsageReportScope.CallKind,
        TabDetail => TokenUsageReportScope.Detail,
        _ => null,
    };

    /// <summary>按鈕提示與報表標題共用的頁籤名稱。</summary>
    public static string Describe(TokenUsageReportScope scope) => scope switch
    {
        TokenUsageReportScope.Trend => "趨勢圖",
        TokenUsageReportScope.Daily => "每日趨勢",
        TokenUsageReportScope.Account => "依使用者",
        TokenUsageReportScope.Operation => "依作業",
        TokenUsageReportScope.Model => "依模型",
        TokenUsageReportScope.CallKind => "依型別",
        TokenUsageReportScope.Detail => "明細",
        _ => "全部頁籤",
    };
}
