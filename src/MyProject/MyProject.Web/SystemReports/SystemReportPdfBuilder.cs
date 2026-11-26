using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MyProject.Dtos.Admins;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Ai;

namespace MyProject.Web.SystemReports;

/// <summary>
/// 系統巡檢報告的 PDF（0.10.40 起）。完全由已存的報告內容（<see cref="SystemReportContentDto"/>）產生，不另存檔：
/// 同一份報告每次下載、每封信附的 PDF 內容都一樣。版面同報告頁：全面摘要在最前面，接著 AI 說明、每一項發現、容量與統計。
///
/// <list type="bullet">
/// <item>沿用 <see cref="AiMarkdownPdfRenderer"/> 的文件樣式（A4、內嵌中文字型、頁首頁尾）；中文一律經 <see cref="CjkLineBreak"/> 補斷行機會。</item>
/// <item>燈號以文字＋底色表示（字型沒有 emoji）。處理頁面寫中文頁名，不放網址。</item>
/// <item>只輸出報告內容本來就有的東西（沒有路徑、設定值、例外內文；帳號只在月報的 AI 費用與帳號清查表格，見速查表 §6.34）。</item>
/// </list>
/// 內嵌字型缺失時 <see cref="AiMarkdownPdfRenderer.CreateDocument"/> 丟 <see cref="InvalidOperationException"/>，由呼叫端處理。
/// </summary>
public static class SystemReportPdfBuilder
{
    public const string ContentType = Export.TabularExport.PdfContentType;

    private static readonly Color BorderColor = AiMarkdownPdfRenderer.BorderColor;
    private static readonly Color HeaderShading = Color.FromRgb(0xF1, 0xF5, 0xF9);
    private static readonly Color MutedColor = AiMarkdownPdfRenderer.MutedColor;

    /// <summary>處理頁面的中文名稱（報告內容存的是站內路徑）。</summary>
    private static readonly Dictionary<string, string> PageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/system-health"] = "系統健康監控",
        ["/logs"] = "日誌檢視",
        ["/system-exceptions"] = "系統例外紀錄",
        ["/scheduled-jobs"] = "排程作業",
        ["/backups"] = "系統備份",
        ["/data-retention"] = "資料保留與清理",
        ["/database-usage"] = "資料庫用量",
        ["/email-logs"] = "寄信紀錄",
        ["/ai-call-logs"] = "AI 對話紀錄",
        ["/token-usage"] = "Token 用量",
        ["/system-parameters"] = "系統參數",
        ["/audit-logs"] = "稽核紀錄",
        ["/myusers"] = "使用者管理",
    };

    /// <summary>處理頁面的中文名稱；不認得的路徑回 null（AI 說明與 PDF 都寫頁名，不寫網址）。</summary>
    public static string? PageName(string? route)
        => route is not null && PageNames.TryGetValue(route, out var name) ? name : null;

    /// <summary>下載與附件的檔名（只用英數字，避免部分郵件軟體把中文檔名變亂碼）：<c>system-report-daily-20261119.pdf</c>。</summary>
    public static string FileName(SystemReportContentDto content)
        => $"system-report-{content.Frequency.ToLowerInvariant()}-{content.PeriodStart:yyyyMMdd}.pdf";

    /// <summary>標題：例「系統巡檢日報 2026/11/19」或「系統巡檢週報 2026/11/09～2026/11/15」。</summary>
    public static string Title(SystemReportContentDto content)
    {
        var last = content.PeriodEnd.AddDays(-1);
        var period = last.Date == content.PeriodStart.Date ? $"{content.PeriodStart:yyyy/MM/dd}" : $"{content.PeriodStart:yyyy/MM/dd}～{last:yyyy/MM/dd}";
        return $"系統巡檢{SystemReportFrequencies.DisplayName(content.Frequency)} {period}";
    }

    public static byte[] Build(SystemReportContentDto content)
    {
        var title = Title(content);
        var document = AiMarkdownPdfRenderer.CreateDocument(
            title, "系統巡檢報告", content.SystemName, $"{content.SystemName}　{title}", content.CreatedAt, string.Empty);
        var section = document.LastSection;

        AiMarkdownPdfRenderer.AddTitle(section, title);
        AiMarkdownPdfRenderer.AddMetadata(section,
        [
            new("系統", $"{content.SystemName} {content.SystemVersion}".Trim()),
            new("涵蓋區間", $"{content.PeriodStart:yyyy/MM/dd HH:mm}～{content.PeriodEnd:yyyy/MM/dd HH:mm}（不含結束時間）"),
            new("產生時間", $"{content.CreatedAt:yyyy/MM/dd HH:mm}"),
            new("整體燈號", $"{SystemReportLevels.ShortName(content.Level)}（{SystemReportLevels.Meaning(content.Level)}）"),
        ], "燈號與數字由系統依固定規則判定；「AI 說明」一節由 AI 依這些結果撰寫，僅供參考，內容以規則判定為準。");

        AddSummary(section, content);
        AddNarrative(section, content);
        AddFindings(section, content);
        AddSections(section, content);
        AddVolumes(section, content);
        AddFolders(section, content);
        AddStats(section, content);
        AddAppendix(section, content);

        return AiMarkdownPdfRenderer.Render(document);
    }

    // ───────────── 全面摘要 ─────────────

    private static void AddSummary(Section section, SystemReportContentDto content)
    {
        section.AddParagraph("全面摘要", StyleNames.Heading1);

        var verdict = section.AddTable();
        verdict.Borders.Color = BorderColor;
        verdict.Borders.Width = 0.5;
        verdict.TopPadding = Unit.FromPoint(6);
        verdict.BottomPadding = Unit.FromPoint(6);
        verdict.LeftPadding = Unit.FromPoint(6);
        verdict.RightPadding = Unit.FromPoint(6);
        verdict.AddColumn(Unit.FromCentimeter(3.4));
        verdict.AddColumn(Unit.FromCentimeter(13.6));
        var row = verdict.AddRow();
        LevelCell(row.Cells[0], content.Level, $"{SystemReportLevels.ShortName(content.Level)}\n{SystemReportLevels.Meaning(content.Level)}");
        Text(row.Cells[1].AddParagraph(), content.Verdict);
        row.Cells[1].VerticalAlignment = VerticalAlignment.Center;
        section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(2);

        var red = content.Findings.Where(x => x.Level == SystemReportLevels.Red).ToList();
        var orange = content.Findings.Where(x => x.Level == SystemReportLevels.Orange).ToList();
        var yellow = content.Findings.Where(x => x.Level == SystemReportLevels.Yellow).ToList();

        if (red.Count > 0)
        {
            section.AddParagraph("已發生問題（要馬上處理）", StyleNames.Heading3);
            foreach (var item in red)
            {
                Bullet(section, $"{item.Title}：{item.HowToFix}");
            }
        }

        if (orange.Count > 0)
        {
            section.AddParagraph("即將發生（有期限）", StyleNames.Heading3);
            foreach (var item in orange)
            {
                Bullet(section, item.Deadline is { } deadline ? $"{item.Title}（期限 {deadline:yyyy/MM/dd}）：{item.HowToFix}" : $"{item.Title}：{item.HowToFix}");
            }
        }

        if (yellow.Count > 0)
        {
            section.AddParagraph("注意即可", StyleNames.Heading3);
            foreach (var item in yellow.Take(10))
            {
                Bullet(section, item.Title);
            }

            if (yellow.Count > 10)
            {
                Bullet(section, $"另有 {yellow.Count - 10} 項，見「每一項發現」。");
            }
        }

        if (content.Expansions.Count > 0)
        {
            section.AddParagraph("需要準備擴充容量", StyleNames.Heading3);
            foreach (var item in content.Expansions)
            {
                Bullet(section, $"{item.Volume}：預估 {item.FullDate:yyyy/MM/dd} 用完，建議擴充約 {SizeFormatHelper.FormatBytes(item.SuggestedBytes)}（一年的成長量再加 15%）。");
            }
        }

        if (content.Sections.Any(x => x.Highlights.Count > 0))
        {
            section.AddParagraph("本期重點", StyleNames.Heading3);
            foreach (var item in content.Sections)
            {
                foreach (var highlight in item.Highlights)
                {
                    Bullet(section, $"{item.Title}：{highlight}");
                }
            }
        }

        section.AddParagraph("下期預測", StyleNames.Heading3);
        foreach (var prediction in content.Predictions)
        {
            Bullet(section, prediction);
        }
    }

    // ───────────── 週報、月報的章節（0.10.41 起）─────────────

    /// <summary>趨勢比較、容量成長、備份可還原驗證、AI 費用、帳號清查：重點＋表格（欄寬平均分配）。</summary>
    private static void AddSections(Section section, SystemReportContentDto content)
    {
        foreach (var item in content.Sections)
        {
            section.AddParagraph(item.Title, StyleNames.Heading1);
            foreach (var highlight in item.Highlights)
            {
                Bullet(section, highlight);
            }

            foreach (var data in item.Tables)
            {
                section.AddParagraph(data.Caption, StyleNames.Heading3);
                if (data.Rows.Count == 0)
                {
                    Muted(section, data.EmptyText);
                    continue;
                }

                var table = CreateTable(section);
                var width = 17.0 / Math.Max(1, data.Columns.Count);
                foreach (var _ in data.Columns)
                {
                    table.AddColumn(Unit.FromCentimeter(width));
                }

                Header(table, data.Columns.ToArray());
                foreach (var values in data.Rows)
                {
                    var row = table.AddRow();
                    for (var index = 0; index < data.Columns.Count; index++)
                    {
                        Cell(row, index, index < values.Count ? values[index] : string.Empty);
                    }
                }

                section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(2);
            }

            foreach (var note in item.Notes)
            {
                Muted(section, note);
            }
        }
    }

    private static void AddNarrative(Section section, SystemReportContentDto content)
    {
        section.AddParagraph("AI 說明", StyleNames.Heading1);
        if (string.IsNullOrWhiteSpace(content.Narrative))
        {
            Muted(section, content.NarrativeNote ?? "沒有 AI 說明。");
            return;
        }

        AiMarkdownPdfRenderer.RenderMarkdown(section, content.Narrative);
    }

    // ───────────── 每一項發現 ─────────────

    private static void AddFindings(Section section, SystemReportContentDto content)
    {
        section.AddParagraph("每一項發現", StyleNames.Heading1);
        if (content.Findings.Count == 0)
        {
            Muted(section, "沒有需要注意的事項。");
            return;
        }

        foreach (var finding in content.Findings)
        {
            var table = CreateTable(section);
            table.AddColumn(Unit.FromCentimeter(3.4));
            table.AddColumn(Unit.FromCentimeter(13.6));
            table.KeepTogether = true;

            var head = table.AddRow();
            LevelCell(head.Cells[0], finding.Level, SystemReportLevels.ShortName(finding.Level));
            Text(head.Cells[1].AddParagraph(), $"{finding.Section}｜{finding.Title}");
            head.Cells[1].Format.Font.Color = AiMarkdownPdfRenderer.HeadingColor;
            head.Cells[1].Shading.Color = HeaderShading;

            Line(table, "現況", finding.Detail);
            Line(table, "不處理會怎樣", finding.IfIgnored);
            Line(table, "怎麼處理", PageName(finding.ActionRoute) is { } page
                ? $"{finding.HowToFix}（處理頁面：{page}）"
                : finding.HowToFix);
            Line(table, "處理完會怎樣", finding.AfterFix);
            if (finding.Deadline is { } deadline)
            {
                Line(table, "期限", $"{deadline:yyyy/MM/dd}");
            }

            section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(2);
        }
    }

    // ───────────── 容量與統計 ─────────────

    private static void AddVolumes(Section section, SystemReportContentDto content)
    {
        if (content.Volumes.Count == 0)
        {
            return;
        }

        section.AddParagraph("磁碟容量", StyleNames.Heading1);
        var table = CreateTable(section);
        foreach (var width in new[] { 2.2, 4.0, 2.2, 3.0, 2.6, 3.0 })
        {
            table.AddColumn(Unit.FromCentimeter(width));
        }

        Header(table, ["磁碟", "用途", "總容量", "可用", "每天成長", "預估用完"]);
        foreach (var volume in content.Volumes)
        {
            var row = table.AddRow();
            Cell(row, 0, volume.Name);
            Cell(row, 1, volume.Purposes);
            Cell(row, 2, SizeFormatHelper.FormatBytes(volume.TotalBytes));
            Cell(row, 3, $"{SizeFormatHelper.FormatBytes(volume.FreeBytes)}（{volume.FreePercent:0.#}%）");
            Cell(row, 4, volume.DailyGrowthBytes is { } growth ? SizeFormatHelper.FormatBytes(growth) : $"資料累積中（{volume.SampleDays} 天）");
            Cell(row, 5, volume.FullDate?.ToString("yyyy/MM/dd") ?? "—");
            if (volume.Level is SystemReportLevels.Red or SystemReportLevels.Orange or SystemReportLevels.Yellow)
            {
                row.Cells[3].Shading.Color = LevelColor(volume.Level);
            }
        }
    }

    private static void AddFolders(Section section, SystemReportContentDto content)
    {
        if (content.Folders.Count == 0 && content.Database is null)
        {
            return;
        }

        section.AddParagraph("資料目錄", StyleNames.Heading1);
        var table = CreateTable(section);
        foreach (var width in new[] { 5.0, 2.6, 6.0, 3.4 })
        {
            table.AddColumn(Unit.FromCentimeter(width));
        }

        Header(table, ["項目", "磁碟", "大小", "檔案數"]);
        if (content.Database is { } db)
        {
            var row = table.AddRow();
            Cell(row, 0, "資料庫");
            Cell(row, 1, "—");
            Cell(row, 2, $"{SizeFormatHelper.FormatBytes(db.MainBytes)}（暫存寫入檔 {SizeFormatHelper.FormatBytes(db.WalBytes)}）");
            Cell(row, 3, "—");
        }

        foreach (var folder in content.Folders)
        {
            var row = table.AddRow();
            Cell(row, 0, folder.Name);
            Cell(row, 1, string.IsNullOrEmpty(folder.Volume) ? "—" : folder.Volume);
            Cell(row, 2, folder.Status switch
            {
                CapacitySnapshotKinds.StatusMissing => "沒有設定或不存在",
                CapacitySnapshotKinds.StatusPartial => $"至少 {SizeFormatHelper.FormatBytes(folder.SizeBytes ?? 0)}（量測逾時）",
                _ => SizeFormatHelper.FormatBytes(folder.SizeBytes ?? 0),
            });
            Cell(row, 3, folder.ItemCount?.ToString("N0") ?? "—");
        }
    }

    private static void AddStats(Section section, SystemReportContentDto content)
    {
        if (content.Stats.Count == 0)
        {
            return;
        }

        section.AddParagraph("統計", StyleNames.Heading1);
        var table = CreateTable(section);
        foreach (var width in new[] { 3.6, 7.0, 6.4 })
        {
            table.AddColumn(Unit.FromCentimeter(width));
        }

        Header(table, ["區塊", "項目", "數值"]);
        foreach (var stat in content.Stats)
        {
            var row = table.AddRow();
            Cell(row, 0, stat.Section);
            Cell(row, 1, stat.Label);
            Cell(row, 2, stat.Value);
        }
    }

    private static void AddAppendix(Section section, SystemReportContentDto content)
    {
        section.AddParagraph("附錄：燈號的意思與量測限制", StyleNames.Heading1);
        Bullet(section, "紅燈：已發生問題，要馬上處理。");
        Bullet(section, "橙燈：即將發生，有期限要處理（報告寫出期限日期）。");
        Bullet(section, "黃燈：注意即可，不急。");
        Bullet(section, "綠燈：正常。整份報告的燈號是所有發現裡最嚴重的那一個。");
        foreach (var note in content.Limitations)
        {
            Bullet(section, note);
        }
    }

    // ───────────── 共用 ─────────────

    private static Table CreateTable(Section section) => AiMarkdownPdfRenderer.CreateTable(section, fontSize: 9, verticalPadding: 3, horizontalPadding: 4);

    private static void Header(Table table, string[] titles)
    {
        var header = table.AddRow();
        header.Shading.Color = HeaderShading;
        header.HeadingFormat = true;
        for (var index = 0; index < titles.Length; index++)
        {
            Cell(header, index, titles[index]);
            header.Cells[index].Format.Font.Color = AiMarkdownPdfRenderer.HeadingColor;
        }
    }

    private static void Line(Table table, string label, string text)
    {
        var row = table.AddRow();
        Cell(row, 0, label);
        row.Cells[0].Format.Font.Color = MutedColor;
        Cell(row, 1, text);
    }

    private static void Cell(Row row, int index, string text)
    {
        var cell = row.Cells[index];
        Text(cell.AddParagraph(), text);
        cell.VerticalAlignment = VerticalAlignment.Center;
    }

    private static void LevelCell(Cell cell, string level, string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var paragraph = cell.AddParagraph(line);
            paragraph.Format.Alignment = ParagraphAlignment.Center;
        }

        cell.Shading.Color = LevelColor(level);
        cell.Format.Font.Color = AiMarkdownPdfRenderer.HeadingColor;
        cell.VerticalAlignment = VerticalAlignment.Center;
    }

    /// <summary>淡色底，深色字仍清楚（列印成黑白也讀得到文字）。</summary>
    private static Color LevelColor(string level) => level switch
    {
        SystemReportLevels.Red => Color.FromRgb(0xFE, 0xCA, 0xCA),
        SystemReportLevels.Orange => Color.FromRgb(0xFE, 0xD7, 0xAA),
        SystemReportLevels.Yellow => Color.FromRgb(0xFE, 0xF0, 0x8A),
        _ => Color.FromRgb(0xBB, 0xF7, 0xD0),
    };

    private static void Bullet(Section section, string text)
    {
        var paragraph = section.AddParagraph();
        paragraph.Format.LeftIndent = Unit.FromPoint(12);
        paragraph.Format.FirstLineIndent = Unit.FromPoint(-9);
        paragraph.Format.SpaceAfter = Unit.FromPoint(3);
        Text(paragraph, "・" + text);
    }

    private static void Muted(Section section, string text)
    {
        var paragraph = section.AddParagraph();
        paragraph.Format.Font.Color = MutedColor;
        Text(paragraph, text);
    }

    private static void Text(Paragraph paragraph, string? text) => CjkLineBreak.AddTo(paragraph.Elements, text);
}
