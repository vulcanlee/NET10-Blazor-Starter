using System.Diagnostics.CodeAnalysis;

namespace MyProject.Share.Helpers;

/// <summary>
/// 文字截斷（0.10.44 起，整併自各處重複的寫法）。null 一律照樣回 null；空字串或沒超過長度的原樣回傳。
/// 各呼叫端對 null 的處理不同（空字串、「（無）」⋯），由呼叫端自己決定。
/// </summary>
public static class TextTruncate
{
    /// <summary>超過 <paramref name="maxLength"/> 個字元就截斷，不加任何符號。</summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Cut(string? text, int maxLength)
        => text is null || text.Length <= maxLength ? text : text[..maxLength];

    /// <summary>超過 <paramref name="maxLength"/> 個字元就截到 <paramref name="maxLength"/> 再加「…」（結果比上限多一個字）。</summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? WithEllipsis(string? text, int maxLength)
        => text is null || text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength), "…");
}
