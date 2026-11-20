using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Health;
using MyProject.Web.Scheduling.Jobs;

namespace MyProject.Web.SystemReports;

/// <summary>
/// 系統巡檢報告的規則（0.10.39 起，純函式）：把 <see cref="SystemReportFacts"/> 變成紅／橙／黃燈的發現，
/// 每一項都寫「現況、不處理會怎樣、怎麼處理（指名頁面）、處理完會怎樣」。燈號只由這裡決定，AI 不能改。
///
/// 新增會影響系統健康的功能時，在這裡加一條規則（並補 SystemReportRulesTests 的門檻邊界）。
/// 文字只能放固定用語與數字：不放路徑、設定值、例外訊息、帳號（報告會寄出系統外，速查表 §6.34）。
/// </summary>
public static class SystemReportRules
{
    private const long Gb = 1024L * 1024 * 1024;
    private const long Mb = 1024L * 1024;

    /// <summary>「排程作業」頁停用了也不提醒的作業：系統巡檢報告自己（管理員本來就可能只開其中幾種）與備份（另有規則）。</summary>
    private static bool IgnoredWhenDisabled(string jobName)
        => jobName.StartsWith("SystemReport", StringComparison.Ordinal) || jobName == SystemBackupJob.JobName;

    public static List<SystemReportFindingDto> Evaluate(SystemReportFacts facts, SystemReportSettings settings)
    {
        var findings = new List<SystemReportFindingDto>();
        Health(facts, findings);
        Errors(facts, findings);
        Jobs(facts, findings);
        Backup(facts, findings);
        Capacity(facts, settings, findings);
        Email(facts, findings);
        Ai(facts, findings);
        Security(facts, findings);
        foreach (var failure in facts.Failures)
        {
            findings.Add(Finding("SYS-SOURCE", failure.Section, SystemReportLevels.Yellow,
                $"無法查詢：{failure.Source}",
                $"產生報告時讀不到「{failure.Source}」，這一部分沒有判斷。",
                "這一部分如果有問題，報告不會發現。",
                "到「系統例外紀錄」或「日誌檢視」找產生報告那段時間的錯誤；通常是資料庫忙碌或檔案權限。",
                "下一份報告會恢復這一部分的判斷。",
                "/system-exceptions"));
        }

        return findings
            .OrderByDescending(x => SystemReportLevels.Rank(x.Level))
            .ThenBy(x => SectionOrder(x.Section))
            .ToList();
    }

    private static int SectionOrder(string section)
    {
        var index = SystemReportSections.All.ToList().IndexOf(section);
        return index < 0 ? int.MaxValue : index;
    }

    // ───────────── 系統健康 ─────────────

    private sealed record HealthText(string Title, string IfIgnored, string HowToFix, string AfterFix, string? Route, bool YellowOnly = false);

    /// <summary>
    /// 健康檢查的項目 → 文字。Host（磁碟）與 ScheduledJobs 不在這裡：容量與排程有自己更細的規則。
    /// 寄信與 LLM API 在不花錢模式不檢查。
    /// </summary>
    private static readonly Dictionary<string, HealthText> HealthTexts = new(StringComparer.Ordinal)
    {
        ["Application"] = new("網站的版本資訊不完整", "「關於」與報告上的版本號不正確，出問題時難以對照版本。",
            "部署時確認 appsettings.json 的 SystemVersion 有值。", "健康檢查的「網站 / 應用程式」恢復綠燈。", "/system-health"),
        ["API"] = new("Web API 沒有註冊任何端點", "所有畫面都讀不到資料。",
            "重新部署網站；問題持續請交給開發人員檢查啟動程序。", "健康檢查的「API」恢復綠燈。", "/system-health"),
        ["Database"] = new("資料庫異常", "資料庫連不上時所有功能都無法使用；有待套用的更新時，新版本的欄位不存在，部分功能會出錯。",
            "連不上：檢查資料庫檔案所在磁碟與權限，再看「系統例外紀錄」。有待套用的更新：重新啟動網站，啟動時會自動套用。", "健康檢查的「資料庫」恢復綠燈。", "/system-health"),
        ["Logging"] = new("日誌檔沒有正常寫入", "出問題時沒有日誌可以查，錯誤追蹤碼也找不到對應的紀錄。",
            "確認日誌目錄存在、網站帳號有寫入權限、磁碟還有空間。", "「日誌檢視」查得到今天的紀錄。", "/logs"),
        ["Authentication"] = new("登入驗證的設定有問題", "使用者可能無法登入，或 Token 的安全性不足。",
            "檢查設定檔的 JwtSettings（正式環境要換掉開發用的簽章金鑰，長度至少 32 字元）。", "健康檢查的「身分驗證」恢復綠燈。", "/system-health"),
        ["FileSystem"] = new("有資料目錄不存在或無法寫入", "附件、備份、例外堆疊等檔案會寫入失敗。",
            "到「系統健康監控」看是哪個目錄，建立目錄並給網站帳號寫入權限。", "健康檢查的「檔案系統」恢復綠燈。", "/system-health"),
        ["Security"] = new("正式環境開啟了開發用的功能", "Swagger 或錯誤細節對外公開，會洩漏系統內部資訊。",
            "在 appsettings.Production.json 關閉 Swagger 與錯誤細節。", "健康檢查的「安全設定」恢復綠燈。", "/system-health", YellowOnly: true),
        ["Cache"] = new("快取服務異常", "登入狀態、流量限制等功能可能不正常，畫面變慢。",
            "檢查快取服務（Redis）是否在執行、連線設定是否正確。", "健康檢查的「快取服務」恢復綠燈。", "/system-health"),
        ["AiPricing"] = new("有 AI 模型沒有設定價格", "這些模型的費用記成 0，Token 用量與 AI 用量上限都會低估。",
            "依「AI 模型計費更新指南」在 AiPricingSettings 補上價格。", "健康檢查的「AI 計費表」恢復綠燈，之後的呼叫有正確費用。", "/system-health", YellowOnly: true),
        ["LoggingPipeline"] = new("日誌與例外紀錄的寫入有異常", "例外與日誌可能沒有被完整記下來，事後查不到原因。",
            "到「系統健康監控」看日誌管線的明細：磁碟空間不足先清空間；佇列滿或寫入失敗請交給開發人員。", "健康檢查的「日誌管線」恢復綠燈。", "/system-health"),
    };

    private static void Health(SystemReportFacts facts, List<SystemReportFindingDto> findings)
    {
        foreach (var item in facts.Health ?? [])
        {
            if (item.Status == SystemHealthStatus.Healthy || !HealthTexts.TryGetValue(item.Category, out var text))
            {
                continue;
            }

            var level = item.Status == SystemHealthStatus.Unhealthy && !text.YellowOnly ? SystemReportLevels.Red : SystemReportLevels.Yellow;
            findings.Add(Finding($"HLT-{item.Category.ToUpperInvariant()}", SystemReportSections.Health, level, text.Title,
                $"健康檢查的「{item.Name}」為{(item.Status == SystemHealthStatus.Unhealthy ? "異常" : "注意")}。",
                text.IfIgnored, text.HowToFix, text.AfterFix, text.Route));
        }
    }

    // ───────────── 錯誤與例外 ─────────────

    private static void Errors(SystemReportFacts facts, List<SystemReportFindingDto> findings)
    {
        var days = facts.Period.Days;
        if (facts.ErrorLogCount is { } errors && errors > 0)
        {
            var level = errors >= 100 * days ? SystemReportLevels.Red : SystemReportLevels.Yellow;
            findings.Add(Finding("ERR-LOG", SystemReportSections.Errors, level,
                level == SystemReportLevels.Red ? "錯誤日誌大量出現" : "有錯誤日誌",
                $"區間內有 {errors:N0}{(facts.ErrorLogCapped ? " 筆以上" : " 筆")} Error 以上的日誌。",
                "使用者可能正在遇到操作失敗；大量出現時可能是某個功能整個壞掉。",
                "到「日誌檢視」把等級設為 Error 查看，依錯誤追蹤碼到「系統例外紀錄」看詳細內容。",
                "下一期的錯誤日誌明顯減少或歸零。",
                "/logs"));
        }

        if (facts.Metrics is { } metrics)
        {
            if (metrics.NewExceptionSignatures > 0)
            {
                var severe = metrics.NewExceptionsSevere > 0;
                findings.Add(Finding("ERR-EXC-NEW", SystemReportSections.Errors, severe ? SystemReportLevels.Red : SystemReportLevels.Yellow,
                    severe ? "出現新的例外，而且已經大量發生或來自背景作業" : "出現新的例外",
                    $"區間內第一次出現的例外有 {metrics.NewExceptionSignatures} 種"
                        + (severe ? $"，其中 {metrics.NewExceptionsSevere} 種已發生 {SystemReportMetricsService.HighOccurrenceThreshold} 次以上或來自背景作業、系統啟動。" : "。"),
                    "新的程式錯誤會持續發生，背景作業的錯誤使用者看不到，但資料可能沒有處理完。",
                    "到「系統例外紀錄」依最後發生時間排序，用「AI 分析」或交給開發人員修正。",
                    "下一期沒有新的例外種類。",
                    "/system-exceptions"));
            }

            if (metrics.ActiveExceptionSignaturesBySource.TryGetValue(ExceptionSources.Browser, out var browser) && browser >= 5)
            {
                findings.Add(Finding("ERR-CLIENT", SystemReportSections.Errors, SystemReportLevels.Yellow,
                    "瀏覽器端錯誤偏多",
                    $"區間內有 {browser} 種瀏覽器端錯誤還在發生。",
                    "部分使用者的畫面可能操作失敗或顯示不完整。",
                    "到「系統例外紀錄」把來源篩成「瀏覽器」，看是哪些頁面。",
                    "瀏覽器端錯誤的種類減少。",
                    "/system-exceptions"));
            }
        }
    }

    // ───────────── 排程作業 ─────────────

    private static void Jobs(SystemReportFacts facts, List<SystemReportFindingDto> findings)
    {
        if (facts.Jobs is not { } jobs)
        {
            return;
        }

        if (!jobs.SchedulingEnabled)
        {
            findings.Add(Finding("JOB-MASTER", SystemReportSections.Jobs, SystemReportLevels.Red,
                "排程總開關被關閉",
                "設定檔的 ScheduledJobSettings:Enabled 是 false，所有排程作業都不會自動執行。",
                "備份、各項清理與巡檢報告都停了：資料只增不減、出事時沒有新的備份。",
                "維護結束後把設定檔的 ScheduledJobSettings:Enabled 改回 true（約一分鐘內生效）。",
                "排程作業恢復依時間執行。",
                "/scheduled-jobs"));
        }

        if (jobs.SettingsError is not null)
        {
            findings.Add(Finding("JOB-SETTINGS", SystemReportSections.Jobs, SystemReportLevels.Yellow,
                "排程設定有誤",
                "設定檔的 ScheduledJobSettings 驗證失敗，排程沿用上一份正確的設定。",
                "修改的時間不會生效，重新啟動網站時會因為設定錯誤而無法啟動。",
                "到「排程作業」頁看錯誤訊息，修正設定檔。",
                "「排程作業」頁不再顯示設定錯誤。",
                "/scheduled-jobs"));
        }

        var enabled = jobs.Items.Where(x => x.IsEnabled).ToList();
        var failing = enabled.Where(x => x.LastFinishedRun?.Status == JobRunStatuses.Failed && x.Name != AuditIntegrityVerifyJob.JobName).ToList();
        if (failing.Count > 0)
        {
            findings.Add(Finding("JOB-FAILED", SystemReportSections.Jobs, SystemReportLevels.Red,
                "排程作業最後一次執行失敗",
                $"{failing.Count} 個作業最後一次執行失敗：{Names(failing.Select(x => x.DisplayName))}。",
                "這些作業負責的事（備份、清理、提醒）沒有完成，問題會一直累積。",
                "到「排程作業」頁看執行紀錄的訊息與錯誤追蹤碼，修正後按「立即執行」。",
                "最後一次執行為成功。",
                "/scheduled-jobs"));
        }

        if (facts.Metrics is { } metrics)
        {
            var recovered = metrics.FailedJobRuns.Keys
                .Where(name => failing.All(x => x.Name != name) && name != AuditIntegrityVerifyJob.JobName)
                .Select(name => jobs.Items.FirstOrDefault(x => x.Name == name)?.DisplayName ?? name)
                .ToList();
            if (recovered.Count > 0)
            {
                findings.Add(Finding("JOB-RECOVERED", SystemReportSections.Jobs, SystemReportLevels.Yellow,
                    "區間內有排程作業失敗過",
                    $"{Names(recovered)} 在區間內失敗過，之後已經成功。",
                    "如果是偶發（例如資料庫一時忙碌）不用處理；常常發生代表有不穩定的地方。",
                    "到「排程作業」頁看失敗那次的訊息。",
                    "下一期沒有失敗紀錄。",
                    "/scheduled-jobs"));
            }
        }

        var overdue = enabled.Where(x => x.OverdueSlotUtc is not null).ToList();
        if (jobs.SchedulingEnabled && overdue.Count > 0)
        {
            findings.Add(Finding("JOB-OVERDUE", SystemReportSections.Jobs, SystemReportLevels.Red,
                "排程作業到時間沒有執行",
                $"{Names(overdue.Select(x => x.DisplayName))} 超過預定時間一小時以上還沒有執行。",
                "網站閒置被 IIS 停掉時，所有排程都不會跑（包含備份）。",
                "IIS 應用程式集區設為「永遠執行」、閒置逾時設 0，並開啟「預先載入」。",
                "「排程作業」頁不再顯示逾期。",
                "/scheduled-jobs"));
        }

        var disabled = jobs.Items.Where(x => !x.IsEnabled && !IgnoredWhenDisabled(x.Name)).ToList();
        if (disabled.Count > 0)
        {
            findings.Add(Finding("JOB-DISABLED", SystemReportSections.Jobs, SystemReportLevels.Yellow,
                "有排程作業被停用",
                $"{Names(disabled.Select(x => x.DisplayName))} 目前停用。",
                "停用的作業不會執行，例如清理作業停用後資料會一直累積。",
                "如果不是刻意停用，到「排程作業」頁重新啟用。",
                "所有需要的作業都是啟用狀態。",
                "/scheduled-jobs"));
        }

        var interrupted = enabled.Where(x => x.LastRun?.Status == JobRunStatuses.Interrupted).ToList();
        if (interrupted.Count > 0)
        {
            findings.Add(Finding("JOB-INTERRUPTED", SystemReportSections.Jobs, SystemReportLevels.Yellow,
                "排程作業執行到一半被中斷",
                $"{Names(interrupted.Select(x => x.DisplayName))} 最後一次執行被中斷（網站在執行中關閉）。",
                "那一次的工作只做了一部分。",
                "到「排程作業」頁按「立即執行」補做一次。",
                "最後一次執行為成功。",
                "/scheduled-jobs"));
        }
    }

    // ───────────── 備份 ─────────────

    private static void Backup(SystemReportFacts facts, List<SystemReportFindingDto> findings)
    {
        if (facts.Backups is not { } backups)
        {
            return;
        }

        var backupJob = facts.Jobs?.Items.FirstOrDefault(x => x.Name == SystemBackupJob.JobName);
        var newest = backups.MaxBy(x => x.CreatedAtLocal);
        var interval = facts.BackupInterval ?? TimeSpan.FromDays(1);

        if (newest is null)
        {
            findings.Add(Finding("BKP-NONE", SystemReportSections.Backup, SystemReportLevels.Red,
                "沒有任何系統備份",
                "備份目錄裡沒有任何備份檔。",
                "磁碟損壞、誤刪資料或中毒時，資料救不回來。",
                "到「系統備份」頁按「立即備份」，並確認「系統備份」排程是啟用的。",
                "備份目錄有最新的備份。",
                "/backups"));
        }
        else
        {
            var age = facts.NowLocal - newest.CreatedAtLocal;
            var grace = TimeSpan.FromHours(2);
            if (age > interval + grace)
            {
                var red = age > interval * 2 + grace;
                findings.Add(Finding("BKP-AGE", SystemReportSections.Backup, red ? SystemReportLevels.Red : SystemReportLevels.Yellow,
                    red ? "最新的備份已經太舊" : "最新的備份比預期舊",
                    $"最新的備份在 {newest.CreatedAtLocal:yyyy/MM/dd HH:mm}，已經 {FormatAge(age)}。",
                    $"出事時只能還原到 {newest.CreatedAtLocal:yyyy/MM/dd HH:mm}，之後的資料會遺失。",
                    "到「排程作業」頁看「系統備份」的執行紀錄，修正後到「系統備份」頁按「立即備份」。",
                    "最新的備份在一個備份週期內。",
                    "/backups"));
            }
        }

        if (backupJob is { IsEnabled: false })
        {
            var deadline = (newest?.CreatedAtLocal ?? facts.NowLocal) + interval;
            findings.Add(Finding("BKP-DISABLED", SystemReportSections.Backup, SystemReportLevels.Orange,
                "系統備份排程被停用",
                "「系統備份」作業目前停用，不會再自動備份。",
                $"從 {deadline:yyyy/MM/dd} 起就沒有新的備份，出事時會遺失之後的資料。",
                "如果不是刻意停用，到「排程作業」頁重新啟用「系統備份」。",
                "每天自動產生新的備份。",
                "/scheduled-jobs",
                deadline.Date));
        }

        var backupFolder = facts.Capacity?.Folders.FirstOrDefault(x => x.Key == "BackupPath");
        var backupVolume = facts.Volumes.FirstOrDefault(x => backupFolder?.VolumeRoot is { } root && string.Equals(x.Usage.Root, root, StringComparison.OrdinalIgnoreCase));
        if (backupVolume is not null && newest is not null && backupJob is { IsEnabled: true })
        {
            var needed = newest.SizeBytes + (long)((facts.Capacity?.Database?.MainBytes ?? 0) * 1.1);
            if (backupVolume.Usage.FreeBytes < needed)
            {
                findings.Add(Finding("BKP-SPACE", SystemReportSections.Backup, SystemReportLevels.Orange,
                    "備份磁碟放不下下一份備份",
                    $"備份所在的磁碟剩 {SizeFormatHelper.FormatBytes(backupVolume.Usage.FreeBytes)}，下一份備份估計需要 {SizeFormatHelper.FormatBytes(needed)}。",
                    facts.NextBackupLocal is { } next ? $"下一次備份（{next:yyyy/MM/dd HH:mm}）很可能因為空間不足而失敗。" : "下一次備份很可能因為空間不足而失敗。",
                    "清出備份磁碟的空間，或到「系統參數」頁調低「備份保留份數」。",
                    "備份磁碟的空間足夠放下一份備份。",
                    "/backups",
                    facts.NextBackupLocal?.Date));
            }
        }

        var databaseRoot = facts.Capacity?.Database?.VolumeRoot;
        if (backupFolder?.VolumeRoot is { } backupRoot && databaseRoot is not null && string.Equals(backupRoot, databaseRoot, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(Finding("BKP-SAMEVOL", SystemReportSections.Backup, SystemReportLevels.Yellow,
                "備份和資料庫放在同一顆磁碟",
                $"備份與資料庫都在 {backupRoot}。",
                "這顆磁碟壞掉時，資料庫和備份會一起不見。",
                "把設定檔的 BackupPath 改到另一顆磁碟或網路位置，或定期把備份複製到別處。",
                "備份與資料庫在不同的磁碟。",
                "/backups"));
        }
    }

    // ───────────── 容量 ─────────────

    private static void Capacity(SystemReportFacts facts, SystemReportSettings settings, List<SystemReportFindingDto> findings)
    {
        foreach (var volume in facts.Volumes)
        {
            var usage = volume.Usage;
            var label = VolumeLabel(usage);
            var free = SizeFormatHelper.FormatBytes(usage.FreeBytes);
            var percent = volume.FreePercent;
            var forecast = volume.Forecast;
            DateTime? fullDate = forecast.DaysToFull is { } d ? facts.NowLocal.Date.AddDays(d) : null;

            var level = SystemReportLevels.Green;
            var reasons = new List<string>();
            if (percent < settings.CapacityRedFreePercent || (settings.CapacityRedFreeGb > 0 && usage.FreeBytes < settings.CapacityRedFreeGb * Gb))
            {
                level = SystemReportLevels.Red;
                reasons.Add($"可用空間只剩 {free}（{percent:0.#}%）");
            }
            else if (percent < settings.EffectiveOrangeFreePercent)
            {
                level = SystemReportLevels.Orange;
                reasons.Add($"可用空間剩 {free}（{percent:0.#}%）");
            }

            if (forecast.DaysToFull is { } days)
            {
                var forecastLevel = days <= settings.CapacityRedForecastDays ? SystemReportLevels.Red
                    : days <= settings.EffectiveOrangeForecastDays ? SystemReportLevels.Orange
                    : days <= settings.EffectiveYellowForecastDays ? SystemReportLevels.Yellow
                    : SystemReportLevels.Green;
                if (forecastLevel != SystemReportLevels.Green)
                {
                    reasons.Add($"每天約增加 {SizeFormatHelper.FormatBytes(forecast.DailyGrowthBytes ?? 0)}，預估 {days} 天後（{fullDate:yyyy/MM/dd}）用完");
                    level = SystemReportLevels.Max(level, forecastLevel);
                }
            }

            if (level == SystemReportLevels.Green)
            {
                continue;
            }

            var title = level switch
            {
                SystemReportLevels.Red => $"磁碟 {label} 空間即將用完",
                SystemReportLevels.Orange => $"磁碟 {label} 空間不足，要開始處理",
                _ => $"磁碟 {label} 要開始準備擴充",
            };
            findings.Add(Finding("CAP-VOLUME", SystemReportSections.Capacity, level, title,
                string.Join("；", reasons) + "。",
                $"磁碟滿了以後，放在這顆磁碟的{string.Join("、", usage.Purposes)}都會寫入失敗（資料庫無法存檔、備份與日誌中斷）。",
                "先到「資料保留與清理」頁看哪類資料最佔空間，縮短保留天數或手動清理；長期請擴充磁碟。"
                    + (forecast.DailyGrowthBytes is > 0 and var g ? $"照目前成長速度，一年約需要 {SizeFormatHelper.FormatBytes((long)(g * 365 * 1.15))}。" : string.Empty),
                $"可用空間高於 {settings.EffectiveOrangeFreePercent}%，預估用完的時間超過 {settings.EffectiveYellowForecastDays} 天。",
                "/data-retention",
                fullDate));
        }

        if (facts.Capacity?.Database is { } db)
        {
            if (db.WalBytes > Math.Max(256 * Mb, db.MainBytes / 2))
            {
                findings.Add(Finding("CAP-WAL", SystemReportSections.Capacity, SystemReportLevels.Yellow,
                    "資料庫的暫存寫入檔偏大",
                    $"資料庫的預寫日誌（-wal 檔）有 {SizeFormatHelper.FormatBytes(db.WalBytes)}，主檔 {SizeFormatHelper.FormatBytes(db.MainBytes)}。",
                    "佔用磁碟空間，查詢也可能變慢。",
                    "通常網站重新啟動（應用程式集區回收）後會收回；一直很大時請交給開發人員檢查是否有長時間開著的讀取。",
                    "預寫日誌回到幾 MB。",
                    "/database-usage"));
            }

            if (db.MainBytes > 0 && db.FreePageBytes > 500 * Mb && db.FreePageBytes > db.MainBytes * 0.3)
            {
                findings.Add(Finding("CAP-FREELIST", SystemReportSections.Capacity, SystemReportLevels.Yellow,
                    "資料庫有大量可回收的空間",
                    $"資料庫裡有 {SizeFormatHelper.FormatBytes(db.FreePageBytes)} 是刪除資料後空出來、還沒還給磁碟的空間。",
                    "這些空間會被之後的新資料重複使用，不會繼續變大，但磁碟上看起來比較大。",
                    "需要時在維護時段停站後執行 VACUUM（交給開發人員或依操作手冊）。",
                    "資料庫檔縮小到實際資料的大小。",
                    "/database-usage"));
            }
        }
    }

    // ───────────── 寄信 ─────────────

    private static void Email(SystemReportFacts facts, List<SystemReportFindingDto> findings)
    {
        if (string.Equals(facts.EmailProvider, "None", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(Finding("MAIL-OFF", SystemReportSections.Email, SystemReportLevels.Yellow,
                "沒有設定寄信",
                "設定檔的 EmailSettings:Provider 是 None，系統不會寄出任何信。",
                "這份報告、忘記密碼、排程失敗與安全告警都寄不出去，只剩站內通知。",
                "在設定檔設定 EmailSettings（SMTP 主機、帳號、寄件者），再到「系統健康監控」按「寄測試信」。",
                "測試信收得到，報告也會寄到管理員信箱。",
                "/system-health"));
            return;
        }

        if (facts.Metrics is not { } metrics)
        {
            return;
        }

        if (metrics.EmailsStuckPending > 0)
        {
            findings.Add(Finding("MAIL-STUCK", SystemReportSections.Email, SystemReportLevels.Red,
                "信件卡在佇列寄不出去",
                $"有 {metrics.EmailsStuckPending} 封信等待寄出超過兩小時。",
                "通知、忘記密碼與告警信都沒有送到收件者手上。",
                "到「寄信紀錄」看錯誤訊息，再到「系統健康監控」按「寄測試信」確認 SMTP 設定。",
                "佇列清空，信件正常寄出。",
                "/email-logs"));
        }

        if (metrics.EmailsFailed > 0)
        {
            var red = metrics.EmailsFailed >= 3 || (metrics.EmailsCreated > 0 && metrics.EmailsFailed * 5 > metrics.EmailsCreated);
            findings.Add(Finding("MAIL-FAILED", SystemReportSections.Email, red ? SystemReportLevels.Red : SystemReportLevels.Yellow,
                "有信件寄送失敗",
                $"區間內 {metrics.EmailsCreated} 封信中有 {metrics.EmailsFailed} 封寄送失敗。",
                "收件者沒有收到通知或密碼重設信。",
                "到「寄信紀錄」篩選「失敗」看原因（帳密錯誤、收件者被拒、連線逾時）。",
                "下一期沒有寄送失敗。",
                "/email-logs"));
        }
    }

    // ───────────── AI ─────────────

    private static void Ai(SystemReportFacts facts, List<SystemReportFindingDto> findings)
    {
        if (!facts.AiConfigured)
        {
            return;
        }

        if (facts.Metrics is { } metrics)
        {
            if (metrics.AiFailedCalls > 0 && metrics.AiCalls > 0)
            {
                var ratio = metrics.AiFailedCalls * 1.0 / metrics.AiCalls;
                var level = metrics.AiCalls >= 5 && ratio >= 0.5 ? SystemReportLevels.Red
                    : ratio >= 0.1 ? SystemReportLevels.Yellow
                    : SystemReportLevels.Green;
                if (level != SystemReportLevels.Green)
                {
                    findings.Add(Finding("AI-FAIL", SystemReportSections.Ai, level,
                        "AI 呼叫失敗偏多",
                        $"區間內 {metrics.AiCalls} 次 AI 呼叫中有 {metrics.AiFailedCalls} 次失敗（{ratio:P0}）。",
                        "AI 分析、系統管理助理等功能對使用者失效。",
                        "到「AI 對話紀錄」篩選失敗的呼叫看原因（金鑰、額度、模型名稱、逾時）。",
                        "失敗率低於 10%。",
                        "/ai-call-logs"));
                }
            }

            if (metrics.SecurityEvents.TryGetValue(AuditActions.Ai.QuotaBlocked, out var blocked) && blocked > 0)
            {
                findings.Add(Finding("AI-BLOCKED", SystemReportSections.Ai, SystemReportLevels.Yellow,
                    "有人因為 AI 用量上限被擋",
                    $"區間內有 {blocked} 次 AI 呼叫因為達到用量上限而沒有送出。",
                    "這些使用者在上限重置前無法使用 AI 功能。",
                    "到「Token 用量」看是誰、哪個功能用得多；需要時到「系統參數」頁調整 AI 用量上限。",
                    "沒有呼叫被擋。",
                    "/token-usage"));
            }

            if (metrics.AiUnpricedCalls > 0)
            {
                findings.Add(Finding("AI-UNPRICED", SystemReportSections.Ai, SystemReportLevels.Yellow,
                    "有 AI 呼叫沒有計算到費用",
                    $"區間內有 {metrics.AiUnpricedCalls} 次成功的 AI 呼叫沒有費用（模型沒有設定價格）。",
                    "費用報表與 AI 用量上限都會低估實際花費。",
                    "依「AI 模型計費更新指南」補上模型的價格。",
                    "之後的呼叫都有費用。",
                    "/token-usage"));
            }
        }

        var monthly = facts.Quota?.FirstOrDefault(x => x.Scope == AiQuotaScope.Global && x.Period == AiQuotaPeriod.Monthly && x.IsLimited);
        if (monthly is not null)
        {
            var money = AiQuotaService.Money;
            if (monthly.IsReached)
            {
                findings.Add(Finding("AI-QUOTA", SystemReportSections.Ai, SystemReportLevels.Red,
                    "全系統 AI 每月用量已達上限",
                    $"本月已用 NT$ {money(monthly.UsedTwd)}，上限 NT$ {monthly.LimitTwd:N0}。",
                    $"所有人的 AI 功能暫停到 {monthly.ResetAt:yyyy/MM/dd}。",
                    "到「系統參數」頁調高「全系統每月上限」，或到「Token 用量」找出用量大的功能。",
                    "AI 功能恢復使用。",
                    "/system-parameters"));
            }
            else
            {
                var elapsedDays = Math.Max(1, (facts.NowLocal - monthly.PeriodStart).TotalDays);
                var monthDays = (monthly.ResetAt - monthly.PeriodStart).TotalDays;
                var projected = monthly.UsedTwd / elapsedDays * monthDays;
                if (projected >= monthly.LimitTwd && monthly.UsedTwd > 0)
                {
                    var reachDate = monthly.PeriodStart.AddDays(monthly.LimitTwd / (monthly.UsedTwd / elapsedDays));
                    findings.Add(Finding("AI-QUOTA", SystemReportSections.Ai, SystemReportLevels.Orange,
                        "照目前速度，AI 用量會在月底前達到上限",
                        $"本月已用 NT$ {money(monthly.UsedTwd)}（上限 NT$ {monthly.LimitTwd:N0}），照目前速度月底約 NT$ {money(projected)}。",
                        $"約 {reachDate:yyyy/MM/dd} 達到上限，之後所有人的 AI 功能暫停到 {monthly.ResetAt:yyyy/MM/dd}。",
                        "到「Token 用量」看哪個功能、哪個人用得多；確定需要時到「系統參數」頁調高「全系統每月上限」。",
                        "月底前不會達到上限。",
                        "/token-usage",
                        reachDate.Date));
                }
                else if (monthly.Ratio >= 0.8)
                {
                    findings.Add(Finding("AI-QUOTA", SystemReportSections.Ai, SystemReportLevels.Yellow,
                        "AI 每月用量已超過上限的 80%",
                        $"本月已用 NT$ {money(monthly.UsedTwd)}，上限 NT$ {monthly.LimitTwd:N0}。",
                        "再多用一些就會達到上限，所有人的 AI 功能會暫停到下個月。",
                        "留意「Token 用量」，必要時調高上限。",
                        "本月用量在上限內。",
                        "/token-usage"));
                }
            }
        }
    }

    // ───────────── 帳號與安全 ─────────────

    private static void Security(SystemReportFacts facts, List<SystemReportFindingDto> findings)
    {
        var integrity = facts.Jobs?.Items.FirstOrDefault(x => x.Name == AuditIntegrityVerifyJob.JobName);
        if (integrity is { IsEnabled: true } && integrity.LastFinishedRun?.Status == JobRunStatuses.Failed)
        {
            findings.Add(Finding("SEC-INTEGRITY", SystemReportSections.Security, SystemReportLevels.Red,
                "稽核紀錄完整性檢查失敗",
                "最後一次「稽核紀錄完整性檢查」沒有通過。",
                "稽核紀錄可能被竄改或損毀，無法當作責任證據。",
                "到「稽核紀錄」頁按「驗證完整性」看是哪幾筆，並保留現場（備份、日誌）再調查。",
                "完整性檢查通過。",
                "/audit-logs"));
        }

        if (facts.Metrics is not { } metrics)
        {
            return;
        }

        var days = facts.Period.Days;
        int Count(string action) => metrics.SecurityEvents.GetValueOrDefault(action);

        if (Count(AuditActions.Token.ReuseDetected) > 0)
        {
            findings.Add(Finding("SEC-REUSE", SystemReportSections.Security, SystemReportLevels.Red,
                "偵測到登入 Token 被重複使用",
                $"區間內有 {Count(AuditActions.Token.ReuseDetected)} 次已經用過的登入 Token 又被拿來使用，系統已撤銷那些登入。",
                "可能有人偷到使用者的登入 Token，冒用身分。",
                "到「稽核紀錄」篩選「Token.ReuseDetected」找出帳號與 IP，請本人改密碼，必要時在「使用者管理」強制登出。",
                "沒有再發生 Token 重複使用。",
                "/audit-logs"));
        }

        if (metrics.LoginFailureBursts > 0)
        {
            findings.Add(Finding("SEC-BURST", SystemReportSections.Security, SystemReportLevels.Red,
                "登入失敗次數異常",
                $"區間內發出 {metrics.LoginFailureBursts} 次「登入失敗次數異常」告警。",
                "可能有人對很多帳號猜密碼，弱密碼的帳號可能被登入。",
                "到「稽核紀錄」篩選登入失敗，看來源 IP 是否集中；集中時在防火牆或 IIS 封鎖。",
                "沒有再發出告警。",
                "/audit-logs"));
        }

        var loginFailures = Count(AuditActions.Login.Failed) + Count(AuditActions.Login.TwoFactorFailed) + Count(AuditActions.Login.SsoFailed);
        if (loginFailures >= 20 * days)
        {
            findings.Add(Finding("SEC-LOGINFAIL", SystemReportSections.Security, SystemReportLevels.Yellow,
                "登入失敗偏多",
                $"區間內有 {loginFailures:N0} 次登入失敗（含兩步驟驗證與 Google 登入）。",
                "可能有人在猜密碼，或使用者常忘記密碼。",
                "到「稽核紀錄」看是哪些帳號、哪些 IP。",
                "登入失敗次數下降。",
                "/audit-logs"));
        }

        if (metrics.LockedAccounts > 0)
        {
            findings.Add(Finding("SEC-LOCKED", SystemReportSections.Security, SystemReportLevels.Yellow,
                "有帳號正被鎖定",
                $"目前有 {metrics.LockedAccounts} 個帳號因連續輸錯密碼被鎖定。",
                "這些人暫時無法登入；如果不是本人輸錯，可能有人在猜他的密碼。",
                "到「使用者管理」確認是否本人，需要時解除鎖定並請他改密碼。",
                "沒有被鎖定的帳號。",
                "/myusers"));
        }

        if (metrics.AdminsWithoutTwoFactor > 0)
        {
            findings.Add(Finding("SEC-ADMIN2FA", SystemReportSections.Security, SystemReportLevels.Yellow,
                "有管理員沒有啟用兩步驟驗證",
                $"{metrics.EnabledAdmins} 位啟用中的管理員裡有 {metrics.AdminsWithoutTwoFactor} 位沒有啟用兩步驟驗證。",
                "管理員的密碼一旦外洩，整個系統就會被接管。",
                "請這些管理員到「個人資料」啟用，或到「系統參數」頁開啟「管理員必須使用兩步驟驗證」。",
                "所有管理員都啟用兩步驟驗證。",
                "/system-parameters"));
        }

        var denied = Count(AuditActions.Permission.Denied);
        if (denied >= 20 * days)
        {
            findings.Add(Finding("SEC-DENIED", SystemReportSections.Security, SystemReportLevels.Yellow,
                "權限被拒的次數偏多",
                $"區間內有 {denied:N0} 次權限被拒。",
                "可能有人在嘗試不該用的功能，或角色權限設錯讓使用者一直碰壁。",
                "到「稽核紀錄」篩選「Permission.Denied」看是哪些人、哪些功能。",
                "權限被拒的次數下降。",
                "/audit-logs"));
        }
    }

    // ───────────── 共用 ─────────────

    /// <summary>磁碟名稱：根目錄＋用途，例如「C:\（程式、資料庫）」。</summary>
    public static string VolumeLabel(CapacityVolumeUsage usage)
        => usage.Purposes.Count == 0 ? usage.Root : $"{usage.Root}（{string.Join("、", usage.Purposes)}）";

    private static string Names(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count <= 5 ? string.Join("、", list) : string.Join("、", list.Take(5)) + $" 等 {list.Count} 個";
    }

    private static string FormatAge(TimeSpan age)
        => age.TotalDays >= 1 ? $"{age.TotalDays:0.#} 天" : $"{age.TotalHours:0} 小時";

    private static SystemReportFindingDto Finding(string ruleId, string section, string level, string title, string detail,
        string ifIgnored, string howToFix, string afterFix, string? route, DateTime? deadline = null)
        => new()
        {
            RuleId = ruleId,
            Section = section,
            Level = level,
            Title = title,
            Detail = detail,
            IfIgnored = ifIgnored,
            HowToFix = howToFix,
            AfterFix = afterFix,
            ActionRoute = route,
            Deadline = deadline,
        };
}
