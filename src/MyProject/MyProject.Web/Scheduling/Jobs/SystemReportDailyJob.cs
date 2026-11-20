using System.Text.Json;
using MyProject.Business.Services.DataAccess;
using MyProject.Models.Systems;
using MyProject.Web.SystemReports;

namespace MyProject.Web.Scheduling.Jobs;

/// <summary>報告頁「立即產生」帶給作業的參數（JSON，見 <see cref="ScheduledJobContext.Arguments"/>）。</summary>
/// <param name="Date">要產生哪一天的日報（本地日期）。</param>
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
/// 系統巡檢日報（0.10.39 起，取代「系統管理助理巡檢」）：每天在系統參數「日報產生時間」產生前一天 00:00～24:00 的報告。
///
/// <list type="bullet">
/// <item>排程、補跑與「排程作業」頁的立即執行：有紅燈或橙燈才通知全體管理員並寄信；同一天已經有排程產生的日報就略過（補跑與兩台機器重疊時不重複寄）。</item>
/// <item>報告頁「立即產生」（帶參數）：產生指定那一天的日報；勾了「寄給所有管理員」才寄，否則只用站內通知告訴產生者。</item>
/// <item>沒有設定 AI 也照常產生（燈號由程式判定，只是少了 AI 說明）。</item>
/// <item>存檔失敗、或需要通知卻通知失敗：回失敗，由排程框架另外通知管理員（JobFailed）。</item>
/// </list>
/// 開關在「排程作業」頁；時間在「系統參數」頁（<see cref="SystemReportScheduleProvider"/>），不能用設定檔的 cron 覆寫。
/// </summary>
public sealed class SystemReportDailyJob : IScheduledJob
{
    public const string JobName = "SystemReportDaily";

    private readonly SystemReportGenerator generator;
    private readonly SystemReportDelivery delivery;
    private readonly SystemReportService reportService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<SystemReportDailyJob> logger;

    public SystemReportDailyJob(
        SystemReportGenerator generator,
        SystemReportDelivery delivery,
        SystemReportService reportService,
        TimeProvider timeProvider,
        ILogger<SystemReportDailyJob> logger)
    {
        this.generator = generator;
        this.delivery = delivery;
        this.reportService = reportService;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<ScheduledJobResult> ExecuteAsync(ScheduledJobContext context, CancellationToken cancellationToken)
    {
        var arguments = SystemReportJobArguments.TryParse(context.Arguments);
        var period = arguments is not null
            ? DailyPeriod(arguments.Date.Date)
            : DailyPeriod(SlotLocalDate(context).AddDays(-1));
        var isScheduled = context.Trigger is JobRunTriggers.Schedule or JobRunTriggers.CatchUp;

        if (isScheduled && await reportService.ScheduledReportExistsAsync(period.Frequency, period.Start))
        {
            logger.LogInformation("System report skipped because it already exists. Frequency={Frequency}, PeriodStart={PeriodStart}", period.Frequency, period.Start);
            return ScheduledJobResult.Success($"{period.Display} 的日報已經產生過，這次略過。");
        }

        var result = await generator.GenerateAsync(
            new SystemReportRequest(period, context.Trigger, context.TriggeredByAccount, context.RunId), cancellationToken);
        if (result.Summary is null)
        {
            return ScheduledJobResult.Failure("系統巡檢日報存檔失敗，詳細內容請看「系統例外紀錄」。");
        }

        var content = result.Content;
        var levelText = $"{period.Display} 的日報：{SystemReportLevels.ShortName(content.Level)}（{content.Verdict}）";
        var send = arguments?.SendToAdmins ?? SystemReportLevels.Rank(content.Level) >= SystemReportLevels.Rank(SystemReportLevels.Orange);
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

    /// <summary>某一天的日報區間：當天 00:00～隔天 00:00（本地時間）。</summary>
    public static SystemReportPeriod DailyPeriod(DateTime date)
        => new(SystemReportFrequencies.Daily, date.Date, date.Date.AddDays(1));

    /// <summary>
    /// 這次執行對應的日期：排程與補跑用「排程時段」（補跑在隔天中午才跑，仍然產生正確的那一天），手動用現在。
    /// </summary>
    private DateTime SlotLocalDate(ScheduledJobContext context)
        => context.ScheduledForUtc is { } slot
            ? TimeZoneInfo.ConvertTimeFromUtc(ScheduleCalculator.AsUtc(slot), timeProvider.LocalTimeZone).Date
            : timeProvider.GetLocalNow().Date;
}
