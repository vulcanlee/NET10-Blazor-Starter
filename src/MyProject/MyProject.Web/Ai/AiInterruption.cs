namespace MyProject.Web.Ai;

/// <summary>
/// 「產生途中被使用者停止」的標示（0.9.141 起）。畫面、複製與 PDF 都用同一段文字，
/// 一份不完整的報告才不會在被複製或下載之後看起來像完整的。
///
/// ⚠️ 只加在給人看的地方。送回給 AI 的對話脈絡保留模型原本的輸出，不加這段註記。
/// </summary>
public static class AiInterruption
{
    public const string Notice = "⚠️ 本內容於產生途中由使用者停止，可能不完整。";

    /// <summary>在 Markdown 開頭加上引用格式的註記。</summary>
    public static string Annotate(string markdown) => $"> {Notice}\n\n{markdown}";
}
