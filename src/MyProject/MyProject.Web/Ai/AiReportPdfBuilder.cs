using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;

namespace MyProject.Web.Ai;

/// <summary>PDF 報告的輸入。全部由日誌檢視頁在按下匯出時組好。</summary>
public sealed record AiReportPdfRequest
{
    public string SystemName { get; init; } = string.Empty;
    public string SystemVersion { get; init; } = string.Empty;
    public string OperatorAccount { get; init; } = string.Empty;
    public DateTime GeneratedAt { get; init; }
    public DateTime QueryStartTime { get; init; }
    public DateTime QueryEndTime { get; init; }

    /// <summary>最低等級。空字串代表不限。</summary>
    public string MinimumLevel { get; init; } = string.Empty;

    /// <summary>關鍵字。空字串代表沒有指定。</summary>
    public string Keyword { get; init; } = string.Empty;

    /// <summary>模型回傳的原始 Markdown。</summary>
    public string Markdown { get; init; } = string.Empty;

    public AiPromptBuildResult Prompt { get; init; } = new();
    public AiTokenUsage? Usage { get; init; }
    public string ModelName { get; init; } = string.Empty;
}

/// <summary>
/// 把 AI 分析結果排成 PDF 報告。
///
/// <para>
/// Markdown 轉 PDF 走的是手寫的極小渲染器：走訪 Markdig 語法樹並對應到 MigraDoc 物件，
/// 只處理 <see cref="AiPromptDefaults.SystemPrompt"/> 明確要求模型輸出的語法
/// （標題、段落、項目與編號清單、粗體、行內程式碼、程式碼區塊、引言、分隔線）。
/// 未列出的節點一律以純文字 fallback 輸出，<b>絕不靜默丟棄內容</b>。
/// </para>
/// <para>
/// ⚠️ 內嵌字型只有 Regular 一個字重（PDFsharp 沒有粗體模擬），所以所有樣式都不設
/// <c>Font.Bold</c>，層級改用字級、顏色與框線表達。詳見 <c>Fonts/README.md</c>。
/// </para>
/// </summary>
public static class AiReportPdfBuilder
{
    private const string CodeStyleName = "AiCode";
    private const string CodeInlineStyleName = "AiCodeInline";
    private const string MetaStyleName = "AiMeta";
    private const string StrongStyleName = "AiStrong";

    /// <summary>模型只會用 ## 與 ###，仍夾住上限以防輸出超出預期。</summary>
    private const int MaxHeadingLevel = 3;

    private const string Disclaimer = "本報告由 AI 依上述日誌生成，內容僅供參考，請以原始日誌為準。";

    private static readonly Color TextColor = Color.FromRgb(0x33, 0x41, 0x55);
    private static readonly Color HeadingColor = Color.FromRgb(0x1E, 0x29, 0x3B);
    private static readonly Color MutedColor = Color.FromRgb(0x64, 0x74, 0x8B);
    private static readonly Color BorderColor = Color.FromRgb(0xE2, 0xE8, 0xF0);
    private static readonly Color ShadingColor = Color.FromRgb(0xF8, 0xFA, 0xFC);
    private static readonly Color CodeColor = Color.FromRgb(0xB4, 0x22, 0x2A);

    /// <summary>產生 PDF 位元組。</summary>
    /// <exception cref="InvalidOperationException">內嵌中文字型資源缺失。</exception>
    public static byte[] Build(AiReportPdfRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        EmbeddedFontResolver.EnsureRegistered();
        if (EmbeddedFontResolver.IsFontAvailable() == false)
        {
            throw new InvalidOperationException("Embedded PDF font resource is missing.");
        }

        var document = CreateDocument(request);
        var section = document.LastSection;

        AddTitle(section, request);
        AddMetadata(section, request);
        AddAnalysisBody(section, request);
        AddAppendix(section, request);

        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();

        using var stream = new MemoryStream();
        renderer.PdfDocument.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    /// <summary>
    /// 報告的中介資訊行。抽成純函式，讓測試可以直接斷言內容，
    /// 不必去解析產出的 PDF（方案內沒有輕量的 PDF 文字抽取相依）。
    /// 「有值才加」的規則也集中在這裡。
    /// </summary>
    public static List<KeyValuePair<string, string>> BuildMetadataLines(AiReportPdfRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var lines = new List<KeyValuePair<string, string>>();

        if (string.IsNullOrWhiteSpace(request.SystemName) == false)
        {
            var system = string.IsNullOrWhiteSpace(request.SystemVersion)
                ? request.SystemName
                : $"{request.SystemName}　版本 {request.SystemVersion}";
            lines.Add(new KeyValuePair<string, string>("系統", system));
        }

        lines.Add(new KeyValuePair<string, string>("產生時間", $"{request.GeneratedAt:yyyy-MM-dd HH:mm:ss}"));

        if (string.IsNullOrWhiteSpace(request.OperatorAccount) == false)
        {
            lines.Add(new KeyValuePair<string, string>("操作者", request.OperatorAccount));
        }

        lines.Add(new KeyValuePair<string, string>(
            "查詢區間",
            $"{request.QueryStartTime:yyyy-MM-dd HH:mm:ss} ～ {request.QueryEndTime:yyyy-MM-dd HH:mm:ss}"));

        lines.Add(new KeyValuePair<string, string>(
            "最低等級",
            string.IsNullOrWhiteSpace(request.MinimumLevel) ? "不限" : request.MinimumLevel));

        if (string.IsNullOrWhiteSpace(request.Keyword) == false)
        {
            lines.Add(new KeyValuePair<string, string>("關鍵字", request.Keyword));
        }

        lines.Add(new KeyValuePair<string, string>("送出範圍", BuildScopeText(request.Prompt)));

        if (string.IsNullOrWhiteSpace(request.ModelName) == false)
        {
            lines.Add(new KeyValuePair<string, string>("使用模型", request.ModelName));
        }

        var usage = BuildUsageText(request.Usage);
        if (string.IsNullOrEmpty(usage) == false)
        {
            lines.Add(new KeyValuePair<string, string>("用量", usage));
        }

        return lines;
    }

    private static string BuildScopeText(AiPromptBuildResult prompt)
    {
        var text = $"送出 {prompt.IncludedEntryCount} 筆／查詢 {prompt.TotalEntryCount} 筆";
        if (prompt.DroppedByEntryLimit)
        {
            text += "（已依筆數上限取最新資料）";
        }

        return text;
    }

    /// <summary>只列出 API 真的有回的欄位。全缺時回空字串，呼叫端就不會加這一行。</summary>
    private static string BuildUsageText(AiTokenUsage? usage)
    {
        if (usage is null || usage.HasAny == false)
        {
            return string.Empty;
        }

        var parts = new List<string>(5);
        Append(parts, "輸入", usage.InputCount);
        Append(parts, "輸出", usage.OutputCount);
        Append(parts, "合計", usage.TotalCount);
        Append(parts, "快取輸入", usage.CachedInputCount);
        Append(parts, "推論", usage.ReasoningCount);
        return string.Join("　", parts);

        static void Append(List<string> parts, string label, int? value)
        {
            if (value.HasValue)
            {
                parts.Add($"{label} {value.Value:N0}");
            }
        }
    }

    private static Document CreateDocument(AiReportPdfRequest request)
    {
        var document = new Document();
        document.Info.Title = "日誌 AI 分析報告";
        document.Info.Subject =
            $"{request.QueryStartTime:yyyy-MM-dd HH:mm} ～ {request.QueryEndTime:yyyy-MM-dd HH:mm}";
        document.Info.Author = string.IsNullOrWhiteSpace(request.SystemName) ? "MyProject" : request.SystemName;

        // ⚠️ 字型一定要設在 Normal 樣式上，其他樣式才會繼承到內嵌的中文字型。
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = EmbeddedFontResolver.FamilyName;
        normal.Font.Size = Unit.FromPoint(10.5);
        normal.Font.Color = TextColor;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);
        normal.ParagraphFormat.LineSpacingRule = LineSpacingRule.Multiple;
        normal.ParagraphFormat.LineSpacing = 1.3;

        ConfigureHeading(document, StyleNames.Heading1, 16, 14, underline: true);
        ConfigureHeading(document, StyleNames.Heading2, 13.5, 12, underline: true);
        ConfigureHeading(document, StyleNames.Heading3, 12, 10, underline: false);

        // 程式碼區塊：沒有中文等寬字型，改用底色與框線做區隔。
        var code = document.Styles.AddStyle(CodeStyleName, StyleNames.Normal);
        code.Font.Size = Unit.FromPoint(9);
        code.ParagraphFormat.LineSpacing = 1.2;
        code.ParagraphFormat.Shading.Color = ShadingColor;
        code.ParagraphFormat.Borders.Color = BorderColor;
        code.ParagraphFormat.Borders.Width = 0.5;
        code.ParagraphFormat.LeftIndent = Unit.FromPoint(8);
        code.ParagraphFormat.RightIndent = Unit.FromPoint(8);
        code.ParagraphFormat.SpaceBefore = Unit.FromPoint(6);

        var codeInline = document.Styles.AddStyle(CodeInlineStyleName, StyleNames.Normal);
        codeInline.Font.Size = Unit.FromPoint(9.5);
        codeInline.Font.Color = CodeColor;

        // 粗體的替代樣式：只改顏色，不設 Bold（沒有粗體字面）。
        var strong = document.Styles.AddStyle(StrongStyleName, StyleNames.Normal);
        strong.Font.Color = HeadingColor;

        var meta = document.Styles.AddStyle(MetaStyleName, StyleNames.Normal);
        meta.Font.Size = Unit.FromPoint(9);
        meta.Font.Color = MutedColor;
        meta.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);
        meta.ParagraphFormat.LineSpacing = 1.25;

        var section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = Unit.FromCentimeter(2.2);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(2);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(2);
        section.PageSetup.RightMargin = Unit.FromCentimeter(2);

        AddHeaderAndFooter(section, request);
        return document;
    }

    private static void ConfigureHeading(
        Document document, string styleName, double size, double spaceBefore, bool underline)
    {
        var style = document.Styles[styleName]!;
        style.Font.Name = EmbeddedFontResolver.FamilyName;
        style.Font.Size = Unit.FromPoint(size);
        style.Font.Color = HeadingColor;
        style.ParagraphFormat.SpaceBefore = Unit.FromPoint(spaceBefore);
        style.ParagraphFormat.SpaceAfter = Unit.FromPoint(4);

        // 避免標題單獨落在頁尾。
        style.ParagraphFormat.KeepWithNext = true;

        if (underline)
        {
            style.ParagraphFormat.Borders.Bottom.Color = BorderColor;
            style.ParagraphFormat.Borders.Bottom.Width = 0.5;
            style.ParagraphFormat.Borders.Distance = Unit.FromPoint(3);
        }
    }

    private static void AddHeaderAndFooter(Section section, AiReportPdfRequest request)
    {
        var header = section.Headers.Primary.AddParagraph();
        var systemName = string.IsNullOrWhiteSpace(request.SystemName) ? "MyProject" : request.SystemName;
        header.AddText($"{systemName}｜日誌 AI 分析報告");
        header.Format.Font.Size = Unit.FromPoint(8);
        header.Format.Font.Color = MutedColor;
        header.Format.Borders.Bottom.Color = BorderColor;
        header.Format.Borders.Bottom.Width = 0.5;
        header.Format.Borders.Distance = Unit.FromPoint(3);
        header.Format.SpaceAfter = Unit.FromPoint(4);

        var footer = section.Footers.Primary.AddParagraph();
        footer.AddText($"產生時間 {request.GeneratedAt:yyyy-MM-dd HH:mm:ss}");
        if (string.IsNullOrWhiteSpace(request.OperatorAccount) == false)
        {
            footer.AddText($"　操作者 {request.OperatorAccount}");
        }

        footer.AddText("　第 ");
        footer.AddPageField();
        footer.AddText(" / ");
        footer.AddNumPagesField();
        footer.AddText(" 頁");
        footer.Format.Font.Size = Unit.FromPoint(8);
        footer.Format.Font.Color = MutedColor;
        footer.Format.Alignment = ParagraphAlignment.Center;
    }

    private static void AddTitle(Section section, AiReportPdfRequest request)
    {
        var title = section.AddParagraph("日誌 AI 分析報告");
        title.Format.Font.Size = Unit.FromPoint(19);
        title.Format.Font.Color = HeadingColor;
        title.Format.SpaceAfter = Unit.FromPoint(10);
    }

    private static void AddMetadata(Section section, AiReportPdfRequest request)
    {
        foreach (var line in BuildMetadataLines(request))
        {
            var paragraph = section.AddParagraph();
            paragraph.Style = MetaStyleName;
            paragraph.AddText($"{line.Key}：{line.Value}");
        }

        var disclaimer = section.AddParagraph(Disclaimer);
        disclaimer.Style = MetaStyleName;
        disclaimer.Format.SpaceBefore = Unit.FromPoint(6);
        disclaimer.Format.SpaceAfter = Unit.FromPoint(12);
        disclaimer.Format.Borders.Top.Color = BorderColor;
        disclaimer.Format.Borders.Top.Width = 0.5;
        disclaimer.Format.Borders.Distance = Unit.FromPoint(4);
    }

    private static void AddAnalysisBody(Section section, AiReportPdfRequest request)
    {
        // 共用 AiMarkdownRenderer 的消毒結果，消毒規則才不會在畫面與 PDF 兩邊漂移。
        var document = AiMarkdownRenderer.ParseSanitized(request.Markdown);
        RenderBlocks(section, document, listDepth: 0, quoteDepth: 0);
    }

    private static void AddAppendix(Section section, AiReportPdfRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt.UserMessage))
        {
            return;
        }

        var heading = section.AddParagraph("附錄：實際送出的原始日誌");
        heading.Style = StyleNames.Heading2;
        heading.Format.PageBreakBefore = true;

        AddCodeParagraph(section, request.Prompt.UserMessage);
    }

    private static void RenderBlocks(Section section, ContainerBlock container, int listDepth, int quoteDepth)
    {
        foreach (var block in container)
        {
            RenderBlock(section, block, listDepth, quoteDepth);
        }
    }

    private static void RenderBlock(Section section, Block block, int listDepth, int quoteDepth)
    {
        switch (block)
        {
            case HeadingBlock heading:
                {
                    var paragraph = section.AddParagraph();
                    paragraph.Style = heading.Level switch
                    {
                        <= 1 => StyleNames.Heading1,
                        2 => StyleNames.Heading2,
                        _ => StyleNames.Heading3,
                    };
                    ApplyQuoteIndent(paragraph, quoteDepth);
                    if (heading.Inline is not null)
                    {
                        RenderInlines(paragraph.Elements, heading.Inline);
                    }

                    break;
                }

            case ParagraphBlock paragraphBlock:
                {
                    var paragraph = section.AddParagraph();
                    ApplyIndent(paragraph, listDepth, quoteDepth);
                    if (paragraphBlock.Inline is not null)
                    {
                        RenderInlines(paragraph.Elements, paragraphBlock.Inline);
                    }

                    break;
                }

            case ListBlock list:
                RenderList(section, list, listDepth, quoteDepth);
                break;

            case QuoteBlock quote:
                RenderBlocks(section, quote, listDepth, quoteDepth + 1);
                break;

            case CodeBlock code:
                AddCodeParagraph(section, ReadLines(code), listDepth, quoteDepth);
                break;

            case ThematicBreakBlock:
                {
                    var rule = section.AddParagraph();
                    rule.Format.SpaceBefore = Unit.FromPoint(6);
                    rule.Format.SpaceAfter = Unit.FromPoint(6);
                    rule.Format.Borders.Bottom.Color = BorderColor;
                    rule.Format.Borders.Bottom.Width = 0.5;
                    break;
                }

            case ContainerBlock nested:
                RenderBlocks(section, nested, listDepth, quoteDepth);
                break;

            case LeafBlock leaf:
                {
                    // fallback：未特別處理的節點也要出現在報告裡，絕不靜默丟棄。
                    var text = ReadLines(leaf);
                    if (string.IsNullOrWhiteSpace(text) == false)
                    {
                        var paragraph = section.AddParagraph();
                        ApplyIndent(paragraph, listDepth, quoteDepth);
                        paragraph.AddText(text);
                    }

                    break;
                }
        }
    }

    /// <summary>
    /// 清單。刻意<b>不用</b> MigraDoc 的 <c>ListInfo</c>：它的編號延續與巢狀在跨頁時很難
    /// 控制，手寫前綴完全符合「極小渲染器」的範圍，行為也可預測。
    /// </summary>
    private static void RenderList(Section section, ListBlock list, int listDepth, int quoteDepth)
    {
        var number = list.OrderedStart is null || int.TryParse(list.OrderedStart, out var start) == false
            ? 1
            : start;

        foreach (var item in list)
        {
            if (item is not ListItemBlock listItem)
            {
                continue;
            }

            var prefix = list.IsOrdered ? $"{number}{list.OrderedDelimiter} " : "• ";
            var isFirstBlock = true;

            foreach (var child in listItem)
            {
                if (isFirstBlock && child is ParagraphBlock firstParagraph)
                {
                    var paragraph = section.AddParagraph();
                    ApplyListIndent(paragraph, listDepth, quoteDepth);
                    paragraph.AddText(prefix);
                    if (firstParagraph.Inline is not null)
                    {
                        RenderInlines(paragraph.Elements, firstParagraph.Inline);
                    }
                }
                else
                {
                    RenderBlock(section, child, listDepth + 1, quoteDepth);
                }

                isFirstBlock = false;
            }

            number++;
        }
    }

    /// <summary>
    /// 走訪行內節點。Paragraph、FormattedText 與 Hyperlink 的 <c>Elements</c> 都是同一個
    /// <see cref="ParagraphElements"/> 型別，所以遞迴只需要這一份，不必為每種容器各寫一次。
    /// </summary>
    private static void RenderInlines(ParagraphElements elements, ContainerInline container)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    elements.AddText(literal.Content.ToString());
                    break;

                case EmphasisInline emphasis:
                    if (emphasis.DelimiterCount >= 2)
                    {
                        // 沒有粗體字面，改用 AiStrong 樣式（只換顏色）表達強調。
                        var strong = elements.AddFormattedText(string.Empty, StrongStyleName);
                        RenderInlines(strong.Elements, emphasis);
                    }
                    else
                    {
                        // 沒有斜體字面，單層強調直接以本文輸出。
                        RenderInlines(elements, emphasis);
                    }

                    break;

                case CodeInline code:
                    elements.AddFormattedText(code.Content ?? string.Empty, CodeInlineStyleName);
                    break;

                case LineBreakInline lineBreak:
                    // 中文的 soft break 若補空白會看到多餘空格，所以只處理 hard break。
                    if (lineBreak.IsHard)
                    {
                        elements.AddLineBreak();
                    }

                    break;

                case LinkInline link:
                    {
                        // 經過 ParseSanitized 之後圖片已降級、危險 scheme 的 URL 已清空。
                        if (string.IsNullOrEmpty(link.Url))
                        {
                            RenderInlines(elements, link);
                        }
                        else
                        {
                            var hyperlink = elements.AddHyperlink(link.Url, HyperlinkType.Web);
                            RenderInlines(hyperlink.Elements, link);
                        }

                        break;
                    }

                case HtmlEntityInline entity:
                    // ⚠️ DisableHtml() 不會移除 entity 解析器，&amp; 之類仍會走到這裡，漏掉就掉字。
                    elements.AddText(entity.Transcoded.ToString());
                    break;

                case HtmlInline html:
                    // 理論上已被 DisableHtml() 擋掉，保留分支當保險，以純文字輸出。
                    elements.AddText(html.Tag ?? string.Empty);
                    break;

                case ContainerInline nested:
                    RenderInlines(elements, nested);
                    break;

                default:
                    elements.AddText(inline.ToString() ?? string.Empty);
                    break;
            }
        }
    }

    private static void AddCodeParagraph(Section section, string text, int listDepth = 0, int quoteDepth = 0)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var paragraph = section.AddParagraph();
        paragraph.Style = CodeStyleName;
        paragraph.Format.LeftIndent = Unit.FromPoint(8 + (14 * (listDepth + quoteDepth)));

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0)
            {
                paragraph.AddLineBreak();
            }

            paragraph.AddText(lines[index]);
        }
    }

    /// <summary>取出 leaf block 的原始文字。<c>Lines.Count</c> 才是有效行數，不可用陣列長度。</summary>
    private static string ReadLines(LeafBlock leaf)
    {
        var lines = leaf.Lines.Lines;
        if (lines is null || leaf.Lines.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("\n", lines.Take(leaf.Lines.Count).Select(line => line.Slice.ToString()));
    }

    private static void ApplyIndent(Paragraph paragraph, int listDepth, int quoteDepth)
    {
        var indent = (14 * listDepth) + (14 * quoteDepth);
        if (indent > 0)
        {
            paragraph.Format.LeftIndent = Unit.FromPoint(indent);
        }

        ApplyQuoteBorder(paragraph, quoteDepth);
    }

    private static void ApplyListIndent(Paragraph paragraph, int listDepth, int quoteDepth)
    {
        paragraph.Format.LeftIndent = Unit.FromPoint((14 * (listDepth + 1)) + (14 * quoteDepth));
        paragraph.Format.FirstLineIndent = Unit.FromPoint(-10);
        paragraph.Format.SpaceAfter = Unit.FromPoint(2);
        ApplyQuoteBorder(paragraph, quoteDepth);
    }

    private static void ApplyQuoteIndent(Paragraph paragraph, int quoteDepth)
    {
        if (quoteDepth > 0)
        {
            paragraph.Format.LeftIndent = Unit.FromPoint(14 * quoteDepth);
        }

        ApplyQuoteBorder(paragraph, quoteDepth);
    }

    private static void ApplyQuoteBorder(Paragraph paragraph, int quoteDepth)
    {
        if (quoteDepth <= 0)
        {
            return;
        }

        paragraph.Format.Borders.Left.Color = BorderColor;
        paragraph.Format.Borders.Left.Width = 2;
        paragraph.Format.Borders.Distance = Unit.FromPoint(6);
    }
}
