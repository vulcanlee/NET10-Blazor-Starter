namespace MyProject.Business.Helpers;

/// <summary>
/// 名稱／代號等「唯一性欄位」的正規化。
///
/// 存在的理由：先前唯一性檢查會把輸入 Trim 之後才比對，寫入時卻是原值直接入庫，
/// 於是「技術文件 」（尾隨空白）存得進去，之後「技術文件」再也比不到它（SQLite 的
/// 字串比較對尾隨空白敏感），兩筆長得一模一樣的資料同時存在。
/// 因此檢查與寫入一律走這裡，確保比對的與儲存的是同一個字串。
/// </summary>
public static class NameNormalizer
{
    /// <summary>
    /// 必填名稱：去除前後空白。String.Trim() 會一併移除全形空白 U+3000 與 tab／換行。
    /// </summary>
    public static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    /// <summary>
    /// 選填代號：去除前後空白，空白一律歸成 null。
    ///
    /// 「未填」在資料庫中必須只有一種表示法：SQLite 的唯一索引視 NULL 互不相等（可以有多筆），
    /// 但空字串彼此相同（只能有一筆）。若放任 NULL／""／"   " 混雜，第二個「沒填代號」的
    /// 團隊就會撞到唯一索引。
    /// </summary>
    public static string? NormalizeOptional(string? value)
    {
        var trimmed = Normalize(value);
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// 只把 A-Z 轉成小寫（0.9.171 起）：和 SQLite 的 <c>lower()</c> 一樣。查詢寫成
    /// <c>x.Account.ToLower() == NameNormalizer.ToLowerAscii(input)</c>，資料庫端與程式端才是同一套規則。
    /// ⚠️ 不可用 C# 的 <c>ToLower()</c>：它會折疊所有字母（「É」→「é」），但 SQLite 不會，「Émile」連大小寫完全照打都比對不到。
    /// EF 會在程式端先算好不含欄位的運算式，所以 <c>input.ToLower()</c> 寫在 lambda 裡也一樣是 C# 的規則。
    /// </summary>
    public static string ToLowerAscii(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return string.Create(value.Length, value, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            }
        });
    }
}
