using System.Text.Json;
using Microsoft.Extensions.Options;
using MyProject.Business.Services.DataAccess;
using MyProject.Dtos.Admins;
using MyProject.Models.Systems;

namespace MyProject.Web.SystemReports;

/// <summary>產生一份報告的請求。</summary>
/// <param name="SkipNarrativeNote">有值時不請 AI 寫說明，報告的「AI 說明」改寫這句（0.10.42 起，系統管理助理：對話回覆就是說明）。</param>
public sealed record SystemReportRequest(SystemReportPeriod Period, string Trigger, string? TriggeredByAccount, int? JobRunId, string? SkipNarrativeNote = null);

/// <summary>產生的結果；存檔失敗時 <see cref="Summary"/> 為 null。</summary>
public sealed record SystemReportResult(SystemReportSummary? Summary, SystemReportContentDto Content);

/// <summary>
/// 產生一份系統巡檢報告（0.10.39 起）：收集事實 → 補今天的容量快照 → 規則判燈號 → 組內容 → AI 說明 → 存檔。
/// 不通知、不寄信（由呼叫端依觸發方式決定，見 <see cref="SystemReportDelivery"/>）。
/// </summary>
public sealed class SystemReportGenerator
{
    public static readonly JsonSerializerOptions ContentJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    private readonly SystemReportCollector collector;
    private readonly SystemReportNarrator narrator;
    private readonly SystemReportService reportService;
    private readonly CapacitySnapshotService snapshotService;
    private readonly IOptionsMonitor<SystemReportSettings> settings;
    private readonly IOptions<SystemSettings> systemSettings;
    private readonly ILogger<SystemReportGenerator> logger;

    public SystemReportGenerator(
        SystemReportCollector collector,
        SystemReportNarrator narrator,
        SystemReportService reportService,
        CapacitySnapshotService snapshotService,
        IOptionsMonitor<SystemReportSettings> settings,
        IOptions<SystemSettings> systemSettings,
        ILogger<SystemReportGenerator> logger)
    {
        this.collector = collector;
        this.narrator = narrator;
        this.reportService = reportService;
        this.snapshotService = snapshotService;
        this.settings = settings;
        this.systemSettings = systemSettings;
        this.logger = logger;
    }

    public async Task<SystemReportResult> GenerateAsync(SystemReportRequest request, CancellationToken cancellationToken)
    {
        var reportId = Guid.NewGuid();
        var facts = await collector.CollectAsync(request.Period, cancellationToken);
        await EnsureTodaySnapshotAsync(facts);

        var findings = SystemReportRules.Evaluate(facts, settings.CurrentValue);
        var info = systemSettings.Value.SystemInformation;
        var content = SystemReportSummarizer.Build(reportId, facts, findings, info.SystemName, info.SystemVersion);

        var narration = request.SkipNarrativeNote is { } note
            ? new SystemReportNarration(SystemReportNarrativeStatuses.Skipped, null, null, note)
            : await narrator.NarrateAsync(content, cancellationToken);
        content.Narrative = narration.Markdown;
        content.NarrativeStatus = narration.Status;
        content.NarrativeNote = narration.Note;

        var summary = await reportService.SaveAsync(new SystemReportDraft(
            reportId,
            request.Period.Frequency,
            request.Period.Start,
            request.Period.End,
            content.CreatedAt,
            content.Level,
            findings.Count(x => x.Level == SystemReportLevels.Red),
            findings.Count(x => x.Level == SystemReportLevels.Orange),
            findings.Count(x => x.Level == SystemReportLevels.Yellow),
            request.Trigger,
            request.TriggeredByAccount,
            request.JobRunId,
            narration.Status,
            narration.ModelName,
            SystemReportDeliveryStatuses.NotRequired,
            JsonSerializer.Serialize(content, ContentJsonOptions)));

        logger.LogInformation(
            "System report generated. ReportId={ReportId}, Frequency={Frequency}, Level={Level}, Findings={Findings}, Narrative={Narrative}",
            reportId, request.Period.Frequency, content.Level, findings.Count, narration.Status);
        return new SystemReportResult(summary, content);
    }

    /// <summary>今天還沒有容量快照時順便存一份（快照作業停用或還沒跑時，預估一樣有資料）。失敗只記警告。</summary>
    private async Task EnsureTodaySnapshotAsync(SystemReportFacts facts)
    {
        if (facts.Capacity is null)
        {
            return;
        }

        try
        {
            var today = facts.NowLocal.Date;
            if (!await snapshotService.HasDayAsync(today))
            {
                await snapshotService.SaveDayAsync(today, facts.NowLocal, facts.Capacity.ToSnapshotRows(today).ToList());
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save the capacity snapshot while generating a system report.");
        }
    }
}
