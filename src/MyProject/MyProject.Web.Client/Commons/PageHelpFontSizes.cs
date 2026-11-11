namespace MyProject.Web.Components.Commons;

// 0.10.1 起放在 MyProject.Web.Client（WASM 遷移 M2）；命名空間維持不變。

/// <summary>說明窗的字體大小（0.9.121 起）。</summary>
public enum PageHelpFontSize
{
    Small,
    Medium,
    Large,
}

/// <summary>
/// 說明窗字體大小的選項、CSS class 與 localStorage 存值的對照。
/// 內文約 14／16／20px（0.9.124 起，預設「中」）；倍率寫在 ClientPageHelpDialog.razor.css 的 <c>--page-help-scale</c>。
/// </summary>
public static class PageHelpFontSizes
{
    /// <summary>存在 localStorage 的鍵（每個瀏覽器各自記，所有頁面的說明窗共用）。</summary>
    public const string StorageKey = "app.helpFontSize";

    public static IReadOnlyList<(PageHelpFontSize Size, string Label)> Options { get; } =
    [
        (PageHelpFontSize.Small, "小"),
        (PageHelpFontSize.Medium, "中"),
        (PageHelpFontSize.Large, "大"),
    ];

    public static string CssClass(PageHelpFontSize size) => size switch
    {
        PageHelpFontSize.Small => "page-help-font-small",
        PageHelpFontSize.Large => "page-help-font-large",
        _ => "page-help-font-medium",
    };

    public static string ToStored(PageHelpFontSize size) => size.ToString().ToLowerInvariant();

    /// <summary>沒存過、被竄改或讀不到時一律回「中」。</summary>
    public static PageHelpFontSize Parse(string? stored) => stored switch
    {
        "small" => PageHelpFontSize.Small,
        "large" => PageHelpFontSize.Large,
        _ => PageHelpFontSize.Medium,
    };
}
