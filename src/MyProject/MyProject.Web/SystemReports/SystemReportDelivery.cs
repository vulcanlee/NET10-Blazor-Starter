using System.Text;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Models.Systems;

namespace MyProject.Web.SystemReports;

/// <summary>
/// 系統巡檢報告的通知（0.10.39 起）：站內通知＋Email 給全體啟用中的管理員，或只通知產生者。
///
/// 通知與信件的內文<b>只用程式產生的文字</b>（整體結論、紅燈與橙燈的標題與期限），不放 AI 說明 ——
/// AI 文字只留在報告頁（速查表 §6.7、§6.34）。同一份報告以 SourceKey 去重，不會重複寄。
/// </summary>
public sealed class SystemReportDelivery
{
    /// <summary>通知內文上限（站內通知存 1000 字）。</summary>
    public const int MaxBodyLength = 1000;

    private readonly INotificationSender notificationSender;
    private readonly SystemReportService reportService;

    public SystemReportDelivery(INotificationSender notificationSender, SystemReportService reportService)
    {
        this.notificationSender = notificationSender;
        this.reportService = reportService;
    }

    /// <summary>站內通知＋Email 給全體啟用中的管理員。回傳是否成功（並寫回報告的通知狀態）。</summary>
    public async Task<bool> SendToAdminsAsync(SystemReportContentDto content, CancellationToken cancellationToken)
    {
        var result = await notificationSender.SendAsync(new NotificationRequest(
            NotificationCategories.SystemReport,
            Title(content),
            Body(content),
            Link(content.ReportId),
            NotificationTarget.AllAdmins(),
            AlsoEmail: true,
            SourceKey: $"SystemReport:{content.ReportId:N}"), cancellationToken);
        var status = result.Failed ? SystemReportDeliveryStatuses.Failed : SystemReportDeliveryStatuses.Sent;
        await reportService.UpdateDeliveryStatusAsync(content.ReportId, status);
        return !result.Failed;
    }

    /// <summary>只通知產生者（報告頁「立即產生」完成時，不寄信）。失敗不影響報告。</summary>
    public async Task NotifyRequesterAsync(SystemReportContentDto content, int userId, CancellationToken cancellationToken)
        => await notificationSender.SendAsync(new NotificationRequest(
            NotificationCategories.SystemReport,
            "報告已產生：" + Title(content),
            content.Verdict,
            Link(content.ReportId),
            NotificationTarget.Users(userId)), cancellationToken);

    public static string Link(Guid reportId) => $"/system-reports?report={reportId:N}";

    /// <summary>例：「系統巡檢日報 2026/11/19：紅燈」。</summary>
    public static string Title(SystemReportContentDto content)
    {
        var period = content.PeriodEnd.AddDays(-1).Date == content.PeriodStart.Date
            ? $"{content.PeriodStart:yyyy/MM/dd}"
            : $"{content.PeriodStart:yyyy/MM/dd}～{content.PeriodEnd.AddDays(-1):yyyy/MM/dd}";
        return $"系統巡檢{SystemReportFrequencies.DisplayName(content.Frequency)} {period}：{SystemReportLevels.ShortName(content.Level)}";
    }

    /// <summary>純文字摘要：結論＋紅燈＋橙燈（附期限）＋黃燈數量。超過上限截斷。</summary>
    public static string Body(SystemReportContentDto content)
    {
        var builder = new StringBuilder();
        builder.AppendLine(content.Verdict);
        AppendLevel(builder, content, SystemReportLevels.Red, "已發生問題（要馬上處理）");
        AppendLevel(builder, content, SystemReportLevels.Orange, "即將發生（有期限）");
        var yellow = content.Findings.Count(x => x.Level == SystemReportLevels.Yellow);
        if (yellow > 0)
        {
            builder.AppendLine($"另有 {yellow} 項需要注意，詳見報告。");
        }

        foreach (var expansion in content.Expansions.Take(3))
        {
            builder.AppendLine($"需要準備擴充：{expansion.Volume}，預估 {expansion.FullDate:yyyy/MM/dd} 用完。");
        }

        var text = builder.ToString().TrimEnd();
        return text.Length <= MaxBodyLength ? text : text[..(MaxBodyLength - 1)] + "…";
    }

    private static void AppendLevel(StringBuilder builder, SystemReportContentDto content, string level, string heading)
    {
        var items = content.Findings.Where(x => x.Level == level).ToList();
        if (items.Count == 0)
        {
            return;
        }

        builder.AppendLine($"【{SystemReportLevels.ShortName(level)}】{heading}");
        foreach (var item in items.Take(5))
        {
            builder.AppendLine(item.Deadline is { } deadline ? $"- {item.Title}（期限 {deadline:yyyy/MM/dd}）" : $"- {item.Title}");
        }

        if (items.Count > 5)
        {
            builder.AppendLine($"- 等 {items.Count} 項");
        }
    }
}
