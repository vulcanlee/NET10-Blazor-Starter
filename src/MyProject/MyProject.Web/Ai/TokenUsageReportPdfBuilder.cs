using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;
using MyProject.Web.Diagnostics;

namespace MyProject.Web.Ai;

/// <summary>Token 用量 PDF 報表的輸入。全部由 Token 用量頁在按下匯出時組好。</summary>
public sealed record TokenUsageReportRequest
{
    public string SystemName { get; init; } = string.Empty;
    public string SystemVersion { get; init; } = string.Empty;
    public string OperatorAccount { get; init; } = string.Empty;
    public DateTime GeneratedAt { get; init; }

    public DateTime? StartDate { get; init; }
    public DateTime? EndDate { get; init; }
    public string Account { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public string CallKind { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;

    public TokenUsageSummary Summary { get; init; } = new();
    public IReadOnlyList<TokenUsageGroupRow> ByAccount { get; init; } = [];
    public IReadOnlyList<TokenUsageGroupRow> ByOperation { get; init; } = [];
    public IReadOnlyList<TokenUsageGroupRow> ByModel { get; init; } = [];
    public IReadOnlyList<TokenUsageGroupRow> ByCallKind { get; init; } = [];
    public IReadOnlyList<TokenUsageLogAdapterModel> Details { get; init; } = [];
}

/// <summary>
/// 把 Token 用量統計排成 PDF 報表。
///
/// ⚠️ 與 <see cref="AiReportPdfBuilder"/> 共用同一套內嵌中文字型機制
/// （<see cref="EmbeddedFontResolver"/>）。內嵌字型只有 Regular 一個字重
/// （PDFsharp 沒有粗體模擬），所以一律不設 <c>Font.Bold</c>，層級改用字級與顏色表達。
/// </summary>
public static class TokenUsageReportPdfBuilder
{
    /// <summary>明細最多列印的筆數。PDF 是拿來看趨勢的，逐筆對帳請改用 CSV。</summary>
    private const int MaxDetailRows = 200;

    private static readonly Color TextColor = Color.FromRgb(0x33, 0x41, 0x55);
    private static readonly Color HeadingColor = Color.FromRgb(0x1E, 0x29, 0x3B);
    private static readonly Color MutedColor = Color.FromRgb(0x64, 0x74, 0x8B);
    private static readonly Color BorderColor = Color.FromRgb(0xE2, 0xE8, 0xF0);
    private static readonly Color ShadingColor = Color.FromRgb(0xF1, 0xF5, 0xF9);

    /// <exception cref="InvalidOperationException">內嵌中文字型資源缺失。</exception>
    public static byte[] Build(TokenUsageReportRequest request)
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
        AddSummary(section, request.Summary);

        AddGroupTable(section, "依使用者", "使用者", request.ByAccount);
        AddGroupTable(section, "依作業", "作業", request.ByOperation);
        AddGroupTable(section, "依模型", "模型", request.ByModel);
        AddGroupTable(section, "依型別", "型別", request.ByCallKind);

        AddDetailTable(section, request.Details);

        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();

        using var stream = new MemoryStream();
        renderer.PdfDocument.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    private static Document CreateDocument(TokenUsageReportRequest request)
    {
        var document = new Document();
        document.Info.Title = "Token 用量報表";
        document.Info.Author = string.IsNullOrWhiteSpace(request.SystemName) ? "MyProject" : request.SystemName;

        // ⚠️ 字型一定要設在 Normal 樣式上，其他樣式才會繼承到內嵌的中文字型。
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = EmbeddedFontResolver.FamilyName;
        normal.Font.Size = Unit.FromPoint(10);
        normal.Font.Color = TextColor;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(5);
        normal.ParagraphFormat.LineSpacingRule = LineSpacingRule.Multiple;
        normal.ParagraphFormat.LineSpacing = 1.25;

        var heading = document.Styles[StyleNames.Heading2]!;
        heading.Font.Size = Unit.FromPoint(13);
        heading.Font.Color = HeadingColor;
        heading.ParagraphFormat.SpaceBefore = Unit.FromPoint(14);
        heading.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);

        var section = document.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.Orientation = Orientation.Landscape;
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.6);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1.6);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(1.6);
        section.PageSetup.RightMargin = Unit.FromCentimeter(1.6);

        return document;
    }

    private static void AddTitle(Section section, TokenUsageReportRequest request)
    {
        var title = section.AddParagraph("Token 用量報表");
        title.Format.Font.Size = Unit.FromPoint(18);
        title.Format.Font.Color = HeadingColor;
        title.Format.SpaceAfter = Unit.FromPoint(4);

        var subtitle = section.AddParagraph(
            $"{request.SystemName}　版本 {request.SystemVersion}");
        subtitle.Format.Font.Size = Unit.FromPoint(9.5);
        subtitle.Format.Font.Color = MutedColor;
        subtitle.Format.SpaceAfter = Unit.FromPoint(10);
    }

    private static void AddMetadata(Section section, TokenUsageReportRequest request)
    {
        var lines = new List<string>
        {
            $"產生時間：{request.GeneratedAt:yyyy-MM-dd HH:mm:ss}",
            $"操作者：{request.OperatorAccount}",
            $"查詢區間：{FormatDate(request.StartDate)} ～ {FormatDate(request.EndDate)}",
        };

        var filters = new List<string>();
        AddFilter(filters, "帳號", request.Account);
        AddFilter(filters, "作業", request.Operation);
        AddFilter(filters, "型別", request.CallKind);
        AddFilter(filters, "模型", request.Model);
        lines.Add($"篩選條件：{(filters.Count == 0 ? "（不限）" : string.Join("、", filters))}");

        foreach (var line in lines)
        {
            var paragraph = section.AddParagraph(line);
            paragraph.Format.Font.Size = Unit.FromPoint(9.5);
            paragraph.Format.Font.Color = MutedColor;
            paragraph.Format.SpaceAfter = Unit.FromPoint(2);
        }

        static void AddFilter(List<string> target, string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value) == false)
            {
                target.Add($"{label} {value}");
            }
        }

        static string FormatDate(DateTime? value) => value?.ToString("yyyy-MM-dd") ?? "（不限）";
    }

    private static void AddSummary(Section section, TokenUsageSummary summary)
    {
        section.AddParagraph("合計", StyleNames.Heading2);

        var paragraph = section.AddParagraph(
            $"輸入(送出) {TokenUsageFormat.Compact(summary.InputCount)}　"
            + $"輸出(接收) {TokenUsageFormat.Compact(summary.OutputCount)}　"
            + $"其中快取 {TokenUsageFormat.Compact(summary.CachedInputCount)}　"
            + $"其中推理 {TokenUsageFormat.Compact(summary.ReasoningCount)}　"
            + $"合計 {TokenUsageFormat.Compact(summary.TotalCount)}　"
            + $"呼叫次數 {summary.CallCount:N0}　"
            + $"花費 NT$ {TokenUsageFormat.CostTwdTotal(summary.CostTwd)}"
            + $"（US$ {TokenUsageFormat.CostUsdTotal(summary.CostUsd)}）");
        paragraph.Format.Font.Size = Unit.FromPoint(11);

        var note = section.AddParagraph(
            "註：快取是輸入的折扣子集、推理計入輸出，兩者皆不另計入合計。"
            + "費用為呼叫當下以設定單價與匯率算好的快照，事後調整設定不影響已記錄的帳；"
            + "美金與台幣各自為逐列加總，區間橫跨匯率調整時兩者彼此推不出來。");
        note.Format.Font.Size = Unit.FromPoint(9);
        note.Format.Font.Color = MutedColor;

        if (summary.UnpricedCount > 0)
        {
            var unpriced = section.AddParagraph(
                $"　其中 {summary.UnpricedCount:N0} 筆未定價（找不到該模型的費率設定）未計入上列金額。");
            unpriced.Format.Font.Size = Unit.FromPoint(9);
            unpriced.Format.Font.Color = MutedColor;
        }
    }

    private static void AddGroupTable(
        Section section, string heading, string keyTitle, IReadOnlyList<TokenUsageGroupRow> rows)
    {
        section.AddParagraph(heading, StyleNames.Heading2);

        if (rows.Count == 0)
        {
            section.AddParagraph("（無資料）");
            return;
        }

        // 欄寬：7 + 7×2.6 = 25.2 cm，A4 橫式可用寬約 26.5 cm（29.7 減左右各 1.6）。
        var table = CreateTable(section);
        table.AddColumn(Unit.FromCentimeter(7));
        for (var index = 0; index < 7; index++)
        {
            table.AddColumn(Unit.FromCentimeter(2.6));
        }

        AddHeaderRow(table, [keyTitle, "輸入", "輸出", "快取", "推理", "合計", "費用(TWD)", "次數"]);

        foreach (var row in rows)
        {
            var cells = table.AddRow();
            SetCell(cells, 0, row.Key, left: true);
            SetCell(cells, 1, TokenUsageFormat.Compact(row.InputCount));
            SetCell(cells, 2, TokenUsageFormat.Compact(row.OutputCount));
            SetCell(cells, 3, TokenUsageFormat.Compact(row.CachedInputCount));
            SetCell(cells, 4, TokenUsageFormat.Compact(row.ReasoningCount));
            SetCell(cells, 5, TokenUsageFormat.Compact(row.TotalCount));
            SetCell(cells, 6, TokenUsageFormat.CostTwdTotal(row.CostTwd));
            SetCell(cells, 7, row.CallCount.ToString("N0"));
        }
    }

    private static void AddDetailTable(Section section, IReadOnlyList<TokenUsageLogAdapterModel> details)
    {
        section.AddParagraph("明細", StyleNames.Heading2);

        if (details.Count == 0)
        {
            section.AddParagraph("（無資料）");
            return;
        }

        var table = CreateTable(section);
        table.AddColumn(Unit.FromCentimeter(3.4));
        table.AddColumn(Unit.FromCentimeter(2.4));
        table.AddColumn(Unit.FromCentimeter(3.4));
        table.AddColumn(Unit.FromCentimeter(2.2));
        table.AddColumn(Unit.FromCentimeter(3.6));
        for (var index = 0; index < 4; index++)
        {
            table.AddColumn(Unit.FromCentimeter(2.0));
        }

        // 費用欄。欄寬合計 3.4+2.4+3.4+2.2+3.6+4×2.0+2.2 = 25.2 cm，A4 橫式放得下。
        table.AddColumn(Unit.FromCentimeter(2.2));

        AddHeaderRow(table, ["時間", "使用者", "作業", "型別", "模型", "輸入", "輸出", "合計", "費用(TWD)", "結果"]);

        foreach (var item in details.Take(MaxDetailRows))
        {
            var cells = table.AddRow();
            SetCell(cells, 0, item.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss"), left: true);
            SetCell(cells, 1, item.Account ?? "（系統自動）", left: true);
            SetCell(cells, 2, item.Operation, left: true);
            SetCell(cells, 3, item.CallKind, left: true);
            SetCell(cells, 4, item.Model, left: true);
            SetCell(cells, 5, TokenUsageFormat.Cell(item.InputCount));
            SetCell(cells, 6, TokenUsageFormat.Cell(item.OutputCount));
            SetCell(cells, 7, item.IsDurationBilled
                ? $"時長 {item.DurationSeconds:N0} 秒"
                : TokenUsageFormat.Cell(item.TotalCount));
            SetCell(cells, 8, item.IsUnpriced ? "未定價" : TokenUsageFormat.CostTwdCell(item.CostTwd));
            SetCell(cells, 9, item.Success ? "成功" : item.FailureReason ?? "失敗");
        }

        if (details.Count > MaxDetailRows)
        {
            var note = section.AddParagraph(
                $"明細僅列出前 {MaxDetailRows} 筆（共 {details.Count:N0} 筆）。完整資料請改用 CSV 匯出。");
            note.Format.Font.Size = Unit.FromPoint(9);
            note.Format.Font.Color = MutedColor;
        }
    }

    private static Table CreateTable(Section section)
    {
        var table = section.AddTable();
        table.Borders.Color = BorderColor;
        table.Borders.Width = 0.5;
        table.Format.Font.Size = Unit.FromPoint(8.5);
        table.Format.SpaceAfter = 0;
        table.TopPadding = Unit.FromPoint(2);
        table.BottomPadding = Unit.FromPoint(2);
        table.LeftPadding = Unit.FromPoint(3);
        table.RightPadding = Unit.FromPoint(3);
        return table;
    }

    private static void AddHeaderRow(Table table, string[] titles)
    {
        var header = table.AddRow();
        header.Shading.Color = ShadingColor;
        header.HeadingFormat = true;

        for (var index = 0; index < titles.Length; index++)
        {
            SetCell(header, index, titles[index], left: index == 0);
            header.Cells[index].Format.Font.Color = HeadingColor;
        }
    }

    private static void SetCell(Row row, int index, string text, bool left = false)
    {
        var cell = row.Cells[index];
        cell.AddParagraph(text ?? string.Empty);
        cell.Format.Alignment = left ? ParagraphAlignment.Left : ParagraphAlignment.Right;
        cell.VerticalAlignment = VerticalAlignment.Center;
    }
}
