using MyProject.Dtos.Admins;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;

namespace MyProject.Web.SystemReports;

/// <summary>
/// 把事實與發現組成報告內容（0.10.39 起，純函式）：整體燈號、一句話結論、需要擴充的磁碟、下期預測、容量表與統計數字。
/// 全部由程式產生；AI 說明另外放在 <see cref="SystemReportContentDto.Narrative"/>。
/// </summary>
public static class SystemReportSummarizer
{
    /// <summary>預估一年內用完的磁碟列入「需要準備擴充」。</summary>
    public const int ExpansionHorizonDays = 365;

    public static SystemReportContentDto Build(
        Guid reportId,
        SystemReportFacts facts,
        IReadOnlyList<SystemReportFindingDto> findings,
        string systemName,
        string systemVersion)
    {
        var level = findings.Aggregate(SystemReportLevels.Green, (current, x) => SystemReportLevels.Max(current, x.Level));
        return new SystemReportContentDto
        {
            ReportId = reportId,
            Frequency = facts.Period.Frequency,
            PeriodStart = facts.Period.Start,
            PeriodEnd = facts.Period.End,
            CreatedAt = facts.NowLocal,
            SystemName = systemName,
            SystemVersion = systemVersion,
            Level = level,
            Verdict = Verdict(level, findings),
            Findings = findings.ToList(),
            Expansions = Expansions(facts),
            Predictions = Predictions(facts, findings, level),
            Volumes = facts.Volumes.Select(x => new SystemReportVolumeDto
            {
                Name = x.Usage.Root,
                Purposes = string.Join("、", x.Usage.Purposes),
                TotalBytes = x.Usage.TotalBytes,
                FreeBytes = x.Usage.FreeBytes,
                FreePercent = Math.Round(x.FreePercent, 1),
                DailyGrowthBytes = x.Forecast.DailyGrowthBytes,
                DaysToFull = x.Forecast.DaysToFull,
                FullDate = x.Forecast.DaysToFull is { } d ? facts.NowLocal.Date.AddDays(d) : null,
                SampleDays = x.Forecast.SampleDays,
                Level = findings.Where(f => f.RuleId == "CAP-VOLUME" && f.Title.Contains(SystemReportRules.VolumeLabel(x.Usage), StringComparison.Ordinal))
                    .Select(f => f.Level).DefaultIfEmpty(SystemReportLevels.Green).First(),
            }).ToList(),
            Folders = (facts.Capacity?.Folders ?? []).Select(x => new SystemReportFolderDto
            {
                Name = x.Label,
                Volume = x.VolumeRoot ?? string.Empty,
                SizeBytes = x.Bytes,
                ItemCount = x.Files,
                Status = x.Status,
            }).ToList(),
            Database = facts.Capacity?.Database is { } db
                ? new SystemReportDatabaseDto { MainBytes = db.MainBytes, WalBytes = db.WalBytes, FreePageBytes = db.FreePageBytes }
                : null,
            Stats = Stats(facts),
            Limitations = facts.Limitations.Distinct().ToList(),
        };
    }

    public static string Verdict(string level, IReadOnlyList<SystemReportFindingDto> findings)
    {
        int Count(string l) => findings.Count(x => x.Level == l);
        return level switch
        {
            SystemReportLevels.Red => $"有 {Count(SystemReportLevels.Red)} 個已發生的問題需要馬上處理"
                + (Count(SystemReportLevels.Orange) > 0 ? $"，另有 {Count(SystemReportLevels.Orange)} 個即將發生的狀況。" : "。"),
            SystemReportLevels.Orange => $"目前沒有已發生的問題，但有 {Count(SystemReportLevels.Orange)} 個即將發生的狀況要在期限前處理。",
            SystemReportLevels.Yellow => $"系統正常運作，有 {Count(SystemReportLevels.Yellow)} 項需要注意。",
            _ => "系統正常，沒有需要處理的事項。",
        };
    }

    private static List<SystemReportExpansionDto> Expansions(SystemReportFacts facts)
        => facts.Volumes
            .Where(x => x.Forecast.DaysToFull is <= ExpansionHorizonDays && x.Forecast.DailyGrowthBytes is > 0)
            .OrderBy(x => x.Forecast.DaysToFull)
            .Select(x => new SystemReportExpansionDto
            {
                Volume = SystemReportRules.VolumeLabel(x.Usage),
                FullDate = facts.NowLocal.Date.AddDays(x.Forecast.DaysToFull!.Value),
                SuggestedBytes = (long)(x.Forecast.DailyGrowthBytes!.Value * 365 * 1.15),
            })
            .ToList();

    private static List<string> Predictions(SystemReportFacts facts, IReadOnlyList<SystemReportFindingDto> findings, string level)
    {
        var predictions = new List<string>();
        foreach (var volume in facts.Volumes.Where(x => x.Forecast.HasEnoughData))
        {
            var label = SystemReportRules.VolumeLabel(volume.Usage);
            var growth = volume.Forecast.DailyGrowthBytes!.Value;
            predictions.Add(volume.Forecast.DaysToFull is { } days
                ? $"{label}每天約增加 {SizeFormatHelper.FormatBytes(growth)}，照這個速度約 {facts.NowLocal.Date.AddDays(days):yyyy/MM/dd} 用完。"
                : $"{label}沒有持續成長（近 {volume.Forecast.SampleDays} 天），容量不會是問題。");
        }

        foreach (var finding in findings.Where(x => x.Level == SystemReportLevels.Orange && x.Deadline is not null && x.RuleId != "CAP-VOLUME"))
        {
            predictions.Add($"{finding.Title}：期限 {finding.Deadline:yyyy/MM/dd}，{finding.IfIgnored}");
        }

        if (predictions.Count == 0 || level == SystemReportLevels.Green)
        {
            predictions.Add(level == SystemReportLevels.Green
                ? "照目前的狀況，下一期應該維持正常。"
                : "處理完上面的項目後，下一期應該恢復正常。");
        }

        return predictions;
    }

    private static List<SystemReportStatDto> Stats(SystemReportFacts facts)
    {
        var stats = new List<SystemReportStatDto>();
        void Add(string section, string label, string value) => stats.Add(new SystemReportStatDto { Section = section, Label = label, Value = value });

        if (facts.ErrorLogCount is { } errors)
        {
            Add(SystemReportSections.Errors, "Error 以上的日誌", $"{errors:N0}{(facts.ErrorLogCapped ? " 以上" : string.Empty)} 筆");
        }

        if (facts.Metrics is { } m)
        {
            Add(SystemReportSections.Errors, "新出現的例外種類", $"{m.NewExceptionSignatures:N0} 種");
            Add(SystemReportSections.Errors, "還在發生的例外種類", $"{m.ActiveExceptionSignaturesBySource.Values.Sum():N0} 種");
            Add(SystemReportSections.Jobs, "排程失敗次數", $"{m.FailedJobRuns.Values.Sum():N0} 次");
            Add(SystemReportSections.Email, "寄出的信", $"{m.EmailsCreated:N0} 封（失敗 {m.EmailsFailed:N0}）");
            Add(SystemReportSections.Ai, "AI 呼叫", $"{m.AiCalls:N0} 次（失敗 {m.AiFailedCalls:N0}）");
            Add(SystemReportSections.Ai, "AI 費用", $"NT$ {m.AiCostTwd:N2}");
            Add(SystemReportSections.Security, "登入失敗", $"{m.SecurityEvents.Where(x => x.Key.StartsWith("Login.", StringComparison.Ordinal)).Sum(x => x.Value):N0} 次");
            Add(SystemReportSections.Security, "權限被拒", $"{m.SecurityEvents.GetValueOrDefault(Business.Helpers.AuditActions.Permission.Denied):N0} 次");
            Add(SystemReportSections.Security, "鎖定中的帳號", $"{m.LockedAccounts:N0} 個");
        }

        if (facts.Backups is { } backups)
        {
            var newest = backups.MaxBy(x => x.CreatedAtLocal);
            Add(SystemReportSections.Backup, "最新的備份", newest is null ? "沒有" : $"{newest.CreatedAtLocal:yyyy/MM/dd HH:mm}（{SizeFormatHelper.FormatBytes(newest.SizeBytes)}）");
            Add(SystemReportSections.Backup, "保留的備份", $"{backups.Count} 份");
        }

        return stats;
    }
}
