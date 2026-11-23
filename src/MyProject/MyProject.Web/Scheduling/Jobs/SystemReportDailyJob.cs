using System.Text.Json;
using MyProject.Business.Services.DataAccess;
using MyProject.Models.Systems;
using MyProject.Web.SystemReports;

namespace MyProject.Web.Scheduling.Jobs;

/// <summary>報告頁「立即產生」帶給作業的參數（JSON，見 <see cref="ScheduledJobContext.Arguments"/>）。</summary>
/// <param name="Date">區間內的任一天（本地日期）：日報＝那一天；週報＝那一天所在的週；月報＝那一天所在的月份。</param>
/// <param name="SendToAdmins">產生後寄給全體管理員（不論燈號）。</param>
public sealed record SystemReportJobArguments(DateTime Date, bool SendToAdmins)
{
    public string ToJson() => JsonSerializer.Serialize(this);

    public static SystemReportJobArguments? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SystemReportJobArguments>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// 系統巡檢報告的排程作業（日報 0.10.39 起、週報與月報 0.10.41 起），三種只差在區間與「什麼時候寄」。
///
/// <list type="bullet">
/// <item>排程、補跑與「排程作業」頁的立即執行：產生最近一個完整的區間（日報＝昨天、週報＝上週一～週日、月報＝上個月）。
/// 日報有紅燈或橙燈才通知全體管理員並寄信；週報、月報一律寄。同一個區間已經有排程產生的報告就略過（補跑與兩台機器重疊時不重複寄）。</item>
/// <item>報告頁「立即產生」（帶參數）：產生指定日期所在的區間；勾了「寄給所有管理員」才寄，否則只用站內通知告訴產生者。</item>
/// <item>沒有設定 AI 也照常產生（燈號由程式判定，只是少了 AI 說明）。</item>
/// <item>存檔失敗、或需要通知卻通知失敗：回失敗，由排程框架另外通知管理員（JobFailed）。</item>
/// </list>
/// 開關在「排程作業」頁；時間在「系統參數」頁（<see cref="SystemReportScheduleProvider"/>），不能用設定檔的 cron 覆寫。
/// </summary>
public abstract class SystemReportJob : IScheduledJob
{
    private readonly SystemReportGenerator generator;
    private readonly SystemReportDelivery delivery;
    private readonly SystemReportService reportService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger logger;

    protected SystemReportJob(
        SystemReportGenerator generator,
        SystemReportDelivery delivery,
        SystemReportService reportService,
        TimeProvider timeProvider,
        ILogger logger)
    {
        this.generator = generator;
        this.delivery = delivery;
        this.reportService = reportService;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>Daily、Weekly、Monthly。</summary>
    protected abstract string Frequency { get; }

    public async Task<ScheduledJobResult> ExecuteAsync(ScheduledJobContext context, CancellationToken cancellationToken)
    {
        var arguments = SystemReportJobArguments.TryParse(context.Arguments);
        var period = arguments is not null
            ? SystemReportPeriod.Containing(Frequency, arguments.Date)
            : SystemReportPeriod.LastComplete(Frequency, SlotLocalDate(context));
        var name = SystemReportFrequencies.DisplayName(Frequency);
        var isScheduled = context.Trigger is JobRunTriggers.Schedule or JobRunTriggers.CatchUp;

        if (isScheduled && await reportService.ScheduledReportExistsAsync(period.Frequency, period.Start))
        {
            logger.LogInformation("System report skipped because it already exists. Frequency={Frequency}, PeriodStart={PeriodStart}", period.Frequency, period.Start);
            return ScheduledJobResult.Success($"{period.Display} 的{name}已經產生過，這次略過。");
        }

        var result = await generator.GenerateAsync(
            new SystemReportRequest(period, context.Trigger, context.TriggeredByAccount, context.RunId), cancellationToken);
        if (result.Summary is null)
        {
            return ScheduledJobResult.Failure($"系統巡檢{name}存檔失敗，詳細內容請看「系統例外紀錄」。");
        }

        var content = result.Content;
        var levelText = $"{period.Display} 的{name}：{SystemReportLevels.ShortName(content.Level)}（{content.Verdict}）";
        var send = arguments?.SendToAdmins ?? ShouldSend(content.Level);
        if (send)
        {
            // 需要通知卻沒通知到人，就回失敗讓排程框架另外通知管理員（JobFailed）。
            if (!await delivery.SendToAdminsAsync(content, cancellationToken))
            {
                return ScheduledJobResult.Failure($"{levelText}；通知發送失敗，報告已存在「系統巡檢報告」頁。");
            }

            return ScheduledJobResult.Success($"{levelText}；已通知管理員並寄信。");
        }

        if (arguments is not null && context.TriggeredByUserId is { } userId)
        {
            await delivery.NotifyRequesterAsync(content, userId, cancellationToken);
        }

        return ScheduledJobResult.Success(levelText + "。");
    }

    /// <summary>排程產生的報告要不要通知全體管理員：日報橙燈以上，週報、月報一律（使用者 2026/11/20 決定）。</summary>
    public static bool ShouldSendScheduled(string frequency, string level)
        => frequency != SystemReportFrequencies.Daily
            || SystemReportLevels.Rank(level) >= SystemReportLevels.Rank(SystemReportLevels.Orange);

    private bool ShouldSend(string level) => ShouldSendScheduled(Frequency, level);

    /// <summary>
    /// 這次執行對應的日期：排程與補跑用「排程時段」（補跑晚了才跑，仍然產生正確的區間），手動用現在。
    /// </summary>
    private DateTime SlotLocalDate(ScheduledJobContext context)
        => context.ScheduledForUtc is { } slot
            ? TimeZoneInfo.ConvertTimeFromUtc(ScheduleCalculator.AsUtc(slot), timeProvider.LocalTimeZone).Date
            : timeProvider.GetLocalNow().Date;

    /// <summary>報告種類 → 作業名稱（報告頁「立即產生」放進哪一個作業的佇列）。</summary>
    public static string JobNameFor(string frequency) => frequency switch
    {
        SystemReportFrequencies.Weekly => SystemReportWeeklyJob.JobName,
        SystemReportFrequencies.Monthly => SystemReportMonthlyJob.JobName,
        _ => SystemReportDailyJob.JobName,
    };
}

/// <summary>系統巡檢日報（0.10.39 起，取代「系統管理助理巡檢」）：產生前一天 00:00～24:00 的報告，紅燈或橙燈才寄。</summary>
public sealed class SystemReportDailyJob : SystemReportJob
{
    public const string JobName = "SystemReportDaily";

    public SystemReportDailyJob(
        SystemReportGenerator generator, SystemReportDelivery delivery, SystemReportService reportService, TimeProvider timeProvider, ILogger<SystemReportDailyJob> logger)
        : base(generator, delivery, reportService, timeProvider, logger)
    {
    }

    protected override string Frequency => SystemReportFrequencies.Daily;
}

/// <summary>系統巡檢週報（0.10.41 起）：產生上週一～週日的報告（加上趨勢、容量成長、備份可還原驗證），一律寄給全體管理員。</summary>
public sealed class SystemReportWeeklyJob : SystemReportJob
{
    public const string JobName = "SystemReportWeekly";

    public SystemReportWeeklyJob(
        SystemReportGenerator generator, SystemReportDelivery delivery, SystemReportService reportService, TimeProvider timeProvider, ILogger<SystemReportWeeklyJob> logger)
        : base(generator, delivery, reportService, timeProvider, logger)
    {
    }

    protected override string Frequency => SystemReportFrequencies.Weekly;
}

/// <summary>
/// 系統巡檢月報（0.10.41 起）：產生上個月的報告（週報的內容再加 AI 費用；1／4／7／10 月產生的那份再加帳號與權限清查），一律寄給全體管理員。
/// </summary>
public sealed class SystemReportMonthlyJob : SystemReportJob
{
    public const string JobName = "SystemReportMonthly";

    public SystemReportMonthlyJob(
        SystemReportGenerator generator, SystemReportDelivery delivery, SystemReportService reportService, TimeProvider timeProvider, ILogger<SystemReportMonthlyJob> logger)
        : base(generator, delivery, reportService, timeProvider, logger)
    {
    }

    protected override string Frequency => SystemReportFrequencies.Monthly;
}
