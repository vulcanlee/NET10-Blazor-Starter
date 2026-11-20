using MimeKit;
using MyProject.Dtos.Admins;
using MyProject.Models.Systems;
using MyProject.Web.Configuration;
using MyProject.Web.Email;
using MyProject.Web.SystemReports;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace MyProject.Tests;

/// <summary>
/// 系統巡檢報告的 PDF（0.10.40 起）：由報告內容產生、四種燈號與有無 AI 說明都產得出來、內容多時分頁、
/// 檔名只用英數字；以及信件附件經 MIME 帶出去。
/// </summary>
public sealed class SystemReportPdfBuilderTests
{
    [Fact]
    public void Build_ProducesAPdf_ForEveryLevel_WithAndWithoutNarrative()
    {
        foreach (var level in SystemReportLevels.All)
        {
            var withNarrative = SystemReportPdfBuilder.Build(Content(level, narrative: "## 整體狀況\n**還好**。\n## 建議處理順序\n1. 到 `/backups` 備份\n## 下期預測\n維持。"));
            var withoutNarrative = SystemReportPdfBuilder.Build(Content(level, narrative: null));

            Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(withNarrative, 0, 5));
            Assert.True(CountPages(withNarrative) >= 1);
            Assert.True(CountPages(withoutNarrative) >= 1);
        }
    }

    [Fact]
    public void Build_ManyFindingsAndLongChinese_SpansPages()
    {
        var content = Content(SystemReportLevels.Red, narrative: null);
        for (var i = 0; i < 40; i++)
        {
            content.Findings.Add(new SystemReportFindingDto
            {
                RuleId = "X",
                Section = SystemReportSections.Capacity,
                Level = SystemReportLevels.Yellow,
                Title = $"第 {i} 項" + new string('長', 80),
                Detail = new string('字', 200),
                IfIgnored = "會怎樣。",
                HowToFix = "怎麼處理。",
                AfterFix = "處理完。",
                ActionRoute = "/data-retention",
            });
        }

        var bytes = SystemReportPdfBuilder.Build(content);

        Assert.True(CountPages(bytes) >= 3);
        Assert.True(bytes.Length > CountBytes(Content(SystemReportLevels.Red, narrative: null)));
    }

    [Fact]
    public void FileNameAndTitle()
    {
        var daily = Content(SystemReportLevels.Green, narrative: null);
        var custom = Content(SystemReportLevels.Green, narrative: null);
        custom.Frequency = SystemReportFrequencies.Custom;
        custom.PeriodEnd = custom.PeriodStart.AddDays(15);

        Assert.Equal("system-report-daily-20261119.pdf", SystemReportPdfBuilder.FileName(daily));
        Assert.Equal("系統巡檢日報 2026/11/19", SystemReportPdfBuilder.Title(daily));
        Assert.Equal("系統巡檢指定區間 2026/11/19～2026/12/03", SystemReportPdfBuilder.Title(custom));
        Assert.Matches("^[a-z0-9.-]+$", SystemReportPdfBuilder.FileName(custom));
    }

    [Fact]
    public void Mime_CarriesTheAttachment()
    {
        var pdf = SystemReportPdfBuilder.Build(Content(SystemReportLevels.Orange, narrative: null));
        var message = new EmailMessage("a@example.com", "主旨", "<p>內文</p>", "內文", EmailKinds.SystemReport,
            [new EmailAttachment("system-report-daily-20261119.pdf", SystemReportPdfBuilder.ContentType, pdf)]);

        using var mime = MimeMessageFactory.Create(message, new EmailSettings { FromAddress = "from@example.com" }, "測試系統");

        var attachment = Assert.IsType<MimePart>(Assert.Single(mime.Attachments));
        Assert.Equal("system-report-daily-20261119.pdf", attachment.FileName);
        Assert.Equal("application/pdf", attachment.ContentType.MimeType);
        using var stream = new MemoryStream();
        attachment.Content!.DecodeTo(stream);
        Assert.Equal(pdf, stream.ToArray());
        Assert.Equal("內文", mime.TextBody);
    }

    [Fact]
    public void Mime_WithoutAttachments_StaysAlternative()
    {
        using var mime = MimeMessageFactory.Create(
            new EmailMessage("a@example.com", "主旨", "<p>內文</p>", "內文", EmailKinds.Notification),
            new EmailSettings { FromAddress = "from@example.com" }, "測試系統");

        Assert.Empty(mime.Attachments);
        Assert.IsType<MultipartAlternative>(mime.Body);
    }

    private static int CountPages(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        return document.PageCount;
    }

    private static int CountBytes(SystemReportContentDto content) => SystemReportPdfBuilder.Build(content).Length;

    internal static SystemReportContentDto Content(string level, string? narrative) => new()
    {
        ReportId = Guid.NewGuid(),
        Frequency = SystemReportFrequencies.Daily,
        PeriodStart = new DateTime(2026, 11, 19),
        PeriodEnd = new DateTime(2026, 11, 20),
        CreatedAt = new DateTime(2026, 11, 20, 7, 30, 0),
        SystemName = "測試系統",
        SystemVersion = "0.10.40",
        Level = level,
        Verdict = "結論。",
        Findings = level == SystemReportLevels.Green ? [] :
        [
            new()
            {
                RuleId = "BKP-SPACE", Section = SystemReportSections.Backup, Level = level, Title = "備份磁碟放不下下一份備份",
                Detail = "剩 1 GB。", IfIgnored = "下一次備份會失敗。", HowToFix = "清出空間。", AfterFix = "空間足夠。",
                ActionRoute = "/backups", Deadline = level == SystemReportLevels.Orange ? new DateTime(2026, 11, 21) : null,
            },
        ],
        Expansions = [new() { Volume = "D:\\（備份）", FullDate = new DateTime(2027, 3, 1), SuggestedBytes = 50L * 1024 * 1024 * 1024 }],
        Predictions = ["照目前的狀況，下一期應該維持正常。"],
        Volumes =
        [
            new() { Name = "C:\\", Purposes = "程式、資料庫", TotalBytes = 500L * 1024 * 1024 * 1024, FreeBytes = 100L * 1024 * 1024 * 1024, FreePercent = 20, SampleDays = 3, Level = SystemReportLevels.Green },
            new() { Name = "D:\\", Purposes = "備份", TotalBytes = 100L * 1024 * 1024 * 1024, FreeBytes = 1L * 1024 * 1024 * 1024, FreePercent = 1, DailyGrowthBytes = 1024, DaysToFull = 5, FullDate = new DateTime(2026, 11, 25), SampleDays = 30, Level = level },
        ],
        Folders =
        [
            new() { Name = "專案附件", Volume = "C:\\", SizeBytes = 2048, ItemCount = 3, Status = CapacitySnapshotKinds.StatusOk },
            new() { Name = "例外堆疊檔", Volume = "C:\\", SizeBytes = 4096, ItemCount = 9, Status = CapacitySnapshotKinds.StatusPartial },
            new() { Name = "上傳暫存", Status = CapacitySnapshotKinds.StatusMissing },
        ],
        Database = new() { MainBytes = 4096, WalBytes = 1024 },
        Stats = [new() { Section = SystemReportSections.Errors, Label = "Error 以上的日誌", Value = "3 筆" }],
        Limitations = ["日誌管線與告警的計數從網站上次啟動起算，重新啟動後歸零。"],
        Narrative = narrative,
        NarrativeStatus = narrative is null ? SystemReportNarrativeStatuses.Skipped : SystemReportNarrativeStatuses.Written,
        NarrativeNote = narrative is null ? "日報全部正常，不需要 AI 說明。" : null,
    };
}
