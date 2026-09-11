using System.Text;
using MyProject.Web.Diagnostics;

namespace MyProject.Web.Ai;

/// <summary>
/// 把查詢結果組成要送給模型的使用者訊息。純函式，無 I/O、無 DI。
/// </summary>
public static class AiLogPromptBuilder
{
    /// <summary>每筆日誌之間的分隔行。</summary>
    public const string EntrySeparator = "---";

    /// <summary>單筆過長時接在尾端的標記。</summary>
    public const string TruncationMarker = "…（本筆過長已截斷）";

    /// <summary>單筆字元上限的下限值，避免設定被填成極小值時整批日誌只剩標記。</summary>
    private const int MinimumCharactersPerEntry = 200;

    /// <summary>
    /// 組出使用者訊息。
    ///
    /// ⚠️ 三道處理的順序固定，不可調換：
    ///
    /// <list type="number">
    /// <item>筆數上限：來源已是時間正序，跳過前面較舊的，取最新 <paramref name="maxEntries"/> 筆。</item>
    /// <item>單筆截斷：對已選中的每筆 Raw 截到 <paramref name="maxCharactersPerEntry"/>。</item>
    /// <item>總量上限：由新到舊逐筆累加「已截斷後」的長度，超出即停，最後翻回正序。</item>
    /// </list>
    ///
    /// 為什麼是這個順序：若先算總量再截斷，預算會以未截斷的長度計算，截斷後預算就白白
    /// 浪費掉，實際送出量遠低於設定值。反過來才能讓
    /// <see cref="AiPromptBuildResult.BodyCharacterCount"/> 不超過總上限成為可精確斷言的保證。
    /// </summary>
    public static AiPromptBuildResult Build(
        IReadOnlyList<LogEntry>? entriesAscending,
        int maxEntries,
        int maxCharactersPerEntry,
        int maxTotalCharacters)
    {
        var total = entriesAscending?.Count ?? 0;
        if (entriesAscending is null || total == 0)
        {
            return new AiPromptBuildResult();
        }

        // 設定值可能被人改成 0 或負數。這裡夾住而不是丟例外，避免整個功能因設定手誤全掛。
        var entryLimit = Math.Max(1, maxEntries);
        var perEntryLimit = Math.Max(MinimumCharactersPerEntry, maxCharactersPerEntry);
        var totalLimit = Math.Max(perEntryLimit, maxTotalCharacters);

        // 步驟 1：取最新 N 筆。
        var skip = Math.Max(0, total - entryLimit);
        var selected = entriesAscending.Skip(skip).ToList();
        var droppedByEntryLimit = skip > 0;

        // 步驟 2：單筆截斷。先統一換行為 LF，讓字元數與模型實際看到的內容一致。
        var truncatedCount = 0;
        var blocks = new List<string>(selected.Count);
        foreach (var entry in selected)
        {
            var raw = (entry.Raw ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            if (raw.Length > perEntryLimit)
            {
                raw = string.Concat(raw.AsSpan(0, perEntryLimit), TruncationMarker);
                truncatedCount++;
            }

            blocks.Add(raw);
        }

        // 步驟 3：總量上限，由新到舊納入。
        var separatorCost = EntrySeparator.Length + 2;
        var kept = new List<string>(blocks.Count);
        var used = 0;
        for (var index = blocks.Count - 1; index >= 0; index--)
        {
            var block = blocks[index];
            var cost = block.Length + separatorCost;

            if (kept.Count == 0)
            {
                // 連第一筆（最新的）都放不下時硬切，保證永遠不會「有資料卻送出 0 筆」。
                if (cost > totalLimit)
                {
                    var room = Math.Max(1, totalLimit - separatorCost);
                    block = string.Concat(block.AsSpan(0, Math.Min(room, block.Length)), TruncationMarker);
                    truncatedCount = Math.Max(truncatedCount, 1);
                }
            }
            else if (used + cost > totalLimit)
            {
                break;
            }

            kept.Add(block);
            used += block.Length + separatorCost;
        }

        // 翻回時間正序，模型才讀得出事件的先後關係。
        kept.Reverse();
        var droppedByTotalLimit = kept.Count < blocks.Count;

        // ⚠️ 一律用 '\n' 而不是 AppendLine：AppendLine 會用 Environment.NewLine，在 Windows 上
        // 是 CRLF，會讓訊息混用兩種行尾，也讓上面的字元預算（separatorCost）與實際長度不符。
        // 固定 LF 讓預算精確、輸出跨平台一致。
        var builder = new StringBuilder();
        builder.Append("以下是 ").Append(kept.Count).Append(" 筆應用程式日誌（時間正序，最舊在前），每筆以 ")
            .Append(EntrySeparator).Append(" 分隔。\n");
        if (droppedByEntryLimit || droppedByTotalLimit)
        {
            builder.Append("（原始查詢共 ").Append(total)
                .Append(" 筆，受上限限制僅送出最新 ").Append(kept.Count).Append(" 筆。）\n");
        }

        if (truncatedCount > 0)
        {
            builder.Append("（其中 ").Append(truncatedCount)
                .Append(" 筆單筆內容過長已截斷，截斷處標記為「").Append(TruncationMarker).Append("」。）\n");
        }

        foreach (var block in kept)
        {
            builder.Append(EntrySeparator).Append('\n').Append(block).Append('\n');
        }

        var message = builder.ToString();
        return new AiPromptBuildResult
        {
            UserMessage = message,
            TotalEntryCount = total,
            IncludedEntryCount = kept.Count,
            TruncatedEntryCount = truncatedCount,
            DroppedByEntryLimit = droppedByEntryLimit,
            DroppedByTotalLimit = droppedByTotalLimit,
            BodyCharacterCount = used,
            CharacterCount = message.Length,
        };
    }

    /// <summary>空白或 null 時退回程式碼內建的預設提示詞。</summary>
    public static string ResolveSystemPrompt(string? configured)
        => string.IsNullOrWhiteSpace(configured) ? AiPromptDefaults.SystemPrompt : configured;
}
