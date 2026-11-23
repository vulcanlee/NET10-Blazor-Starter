using System.Globalization;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Models.Systems;
using MyProject.Web.Auditing;

namespace MyProject.Web.SystemReports;

/// <summary>解析後的區間；不合法時 <see cref="Period"/> 為 null、<see cref="Error"/> 是給模型轉述的說明。</summary>
public sealed record SystemReportRangeResult(SystemReportPeriod? Period, string? Error);

/// <summary>助理產生的報告；存檔失敗時 <see cref="ReportId"/> 為 null。</summary>
public sealed record SystemReportAssistantResult(Guid? ReportId, SystemReportContentDto Content);

/// <summary>
/// 系統管理助理在對話中產生指定區間的系統巡檢報告（0.10.42 起，M4）。
///
/// <list type="bullet">
/// <item>區間由這裡解析：模型不知道今天幾號，使用者說「11/1～11/15」時年份由程式推（不晚於今天的最近那一年）。</item>
/// <item>種類 <c>Custom</c>、觸發 <c>Assistant</c>；不另外請 AI 寫說明（對話回覆就是說明）、不通知、不寄信。</item>
/// <item>在對話請求裡直接產生（串流有心跳、使用者可以停止），是速查表 §6.34「產生報告不在 HTTP 請求裡跑」的明文例外；
/// 也是「會寫資料的工具要核准」的明文例外（使用者 2026/11/20 決定）。產生後寫 <c>SystemReport.Generate</c> 稽核。</item>
/// </list>
/// </summary>
public sealed class SystemReportAssistantService
{
    /// <summary>一次最多涵蓋幾天。</summary>
    public const int MaxDays = 31;

    /// <summary>報告的「AI 說明」一節寫這句。</summary>
    public const string NarrativeNote = "由系統管理助理在對話中產生，說明請見該次對話。";

    private static readonly string[] FullFormats = ["yyyy-M-d", "yyyy/M/d", "yyyy.M.d", "yyyyMMdd"];

    private readonly SystemReportGenerator generator;
    private readonly SystemReportService reportService;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SystemReportAssistantService> logger;

    public SystemReportAssistantService(
        SystemReportGenerator generator,
        SystemReportService reportService,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        TimeProvider timeProvider,
        ILogger<SystemReportAssistantService> logger)
    {
        this.generator = generator;
        this.reportService = reportService;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>
    /// 解析使用者說的區間：兩個都沒給＝昨天～今天（今天到目前為止）；只給一個＝那一天；起大於迄自動對調。
    /// 迄日晚於今天、超過 <see cref="MaxDays"/> 天、或早於日報保留天數時回錯誤。
    /// </summary>
    public SystemReportRangeResult ResolveRange(string? startDate, string? endDate)
        => ResolveRange(startDate, endDate, timeProvider.GetLocalNow().Date, reportService.RetentionDays(SystemReportFrequencies.Custom));

    public static SystemReportRangeResult ResolveRange(string? startDate, string? endDate, DateTime today, int retentionDays)
    {
        today = today.Date;
        if (string.IsNullOrWhiteSpace(startDate) && string.IsNullOrWhiteSpace(endDate))
        {
            return new(SystemReportPeriod.Custom(today.AddDays(-1), today), null);
        }

        DateTime? start = null;
        DateTime? end = null;
        foreach (var (text, assign) in new (string?, Action<DateTime>)[] { (startDate, x => start = x), (endDate, x => end = x) })
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (ParseDate(text, today) is not { } date)
            {
                return new(null, $"看不懂日期「{text.Trim()}」，請用 2026-11-01 或 11/1 這樣的寫法。");
            }

            assign(date);
        }

        var from = start ?? end!.Value;
        var to = end ?? start!.Value;
        if (from > to)
        {
            (from, to) = (to, from);
        }

        if (to > today)
        {
            return new(null, $"迄日 {to:yyyy/MM/dd} 晚於今天（{today:yyyy/MM/dd}），只能產生今天以前（含今天到目前為止）的報告。");
        }

        if ((to - from).TotalDays + 1 > MaxDays)
        {
            return new(null, $"一次最多產生 {MaxDays} 天的報告（{from:yyyy/MM/dd}～{to:yyyy/MM/dd} 是 {(to - from).TotalDays + 1:N0} 天），請縮短區間或分次產生；整月請用「系統巡檢報告」頁的月報。");
        }

        if (from < today.AddDays(-retentionDays))
        {
            return new(null, $"只能產生最近 {retentionDays} 天內的報告（起日 {from:yyyy/MM/dd} 太早）。");
        }

        return new(SystemReportPeriod.Custom(from, to), null);
    }

    /// <summary>沒有年份時取不晚於今天的最近那一年（模型不知道今天幾號，原樣傳入使用者說的日期）。</summary>
    private static DateTime? ParseDate(string text, DateTime today)
    {
        var value = text.Trim();
        if (DateTime.TryParseExact(value, FullFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var full))
        {
            return full.Date;
        }

        // 只有月與日（11/1、11-01）：找不晚於今天的最近那一年（2/29 往前找到閏年）。
        var parts = value.Split('/', '-', '.');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var day)
            || month is < 1 or > 12 || day is < 1 or > 31)
        {
            return null;
        }

        for (var year = today.Year; year >= today.Year - 4; year--)
        {
            if (day <= DateTime.DaysInMonth(year, month) && new DateTime(year, month, day) is var candidate && candidate <= today)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>產生並存檔（不寫 AI 說明、不通知），寫 <c>SystemReport.Generate</c> 稽核。</summary>
    public async Task<SystemReportAssistantResult> GenerateAsync(SystemReportPeriod period, CancellationToken cancellationToken)
    {
        var account = currentUserService.CurrentUser.Account;
        var result = await generator.GenerateAsync(
            new SystemReportRequest(period, SystemReportTriggers.Assistant, account, null, NarrativeNote), cancellationToken);
        if (result.Summary is null)
        {
            return new(null, result.Content);
        }

        var reportId = result.Summary.ReportId;
        logger.LogInformation("System report generated by the admin assistant. ReportId={ReportId}, PeriodStart={PeriodStart}, Days={Days}", reportId, period.Start, period.Days);
        await ViewAudit.WriteAsync(auditLogService, currentUserService, AuditActions.SystemReport.Generate, "SystemReport", reportId.ToString("N"),
            $"frequency={period.Frequency}; start={period.Start:yyyy-MM-dd}; end={period.End.AddDays(-1):yyyy-MM-dd}; via=admin-assistant");
        return new(reportId, result.Content);
    }
}
