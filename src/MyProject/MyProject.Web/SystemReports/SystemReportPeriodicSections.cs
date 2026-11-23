using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Dtos.Admins;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Backup;

namespace MyProject.Web.SystemReports;

/// <summary>
/// 週報、月報才有的章節（0.10.41 起，純函式）：趨勢比較、容量成長與規劃、備份可還原驗證、AI 費用、帳號與權限清查。
/// 每一節是「重點（Highlights）＋表格」，畫面與 PDF 用同一套通用的呈現方式。
///
/// ⚠️ Highlights 只能放數字與固定用語（會進通知、信件內文與 AI 說明）；
/// 帳號與姓名只能放在 AI 費用與帳號清查的表格裡（使用者 2026/11/20 決定的例外，速查表 §6.34）。
/// </summary>
public static class SystemReportPeriodicSections
{
    public static List<SystemReportSectionDto> Build(SystemReportFacts facts)
    {
        var sections = new List<SystemReportSectionDto>();
        if (!facts.Period.IsPeriodic)
        {
            return sections;
        }

        if (facts.Metrics is { } current && facts.PreviousMetrics is { } previous)
        {
            sections.Add(Trend(facts.Period, current, previous));
        }

        if (facts.Capacity is not null)
        {
            sections.Add(Growth(facts));
        }

        sections.Add(BackupVerify(facts));

        if (facts.AiCost is { } aiCost)
        {
            sections.Add(AiCost(facts.Period, aiCost));
        }

        if (facts.AccountReview is { } review)
        {
            sections.Add(AccountReview(facts, review));
        }

        return sections;
    }

    // ───────────── 趨勢比較 ─────────────

    private sealed record TrendRow(string Label, double Current, double Previous, bool Money = false);

    private static SystemReportSectionDto Trend(SystemReportPeriod period, SystemReportMetrics current, SystemReportMetrics previous)
    {
        static double LoginFailures(SystemReportMetrics m) => m.SecurityEvents.Where(x => x.Key.StartsWith("Login.", StringComparison.Ordinal)).Sum(x => x.Value);

        var rows = new List<TrendRow>
        {
            new("新出現的例外種類", current.NewExceptionSignatures, previous.NewExceptionSignatures),
            new("還在發生的例外種類", current.ActiveExceptionSignaturesBySource.Values.Sum(), previous.ActiveExceptionSignaturesBySource.Values.Sum()),
            new("排程失敗次數", current.FailedJobRuns.Values.Sum(), previous.FailedJobRuns.Values.Sum()),
            new("寄出的信", current.EmailsCreated, previous.EmailsCreated),
            new("寄送失敗的信", current.EmailsFailed, previous.EmailsFailed),
            new("AI 呼叫次數", current.AiCalls, previous.AiCalls),
            new("AI 呼叫失敗", current.AiFailedCalls, previous.AiFailedCalls),
            new("AI 費用", current.AiCostTwd, previous.AiCostTwd, Money: true),
            new("登入失敗", LoginFailures(current), LoginFailures(previous)),
            new("權限被拒", current.SecurityEvents.GetValueOrDefault(AuditActions.Permission.Denied), previous.SecurityEvents.GetValueOrDefault(AuditActions.Permission.Denied)),
        };

        var increased = rows
            .Where(x => x.Current - x.Previous >= 1)
            .OrderByDescending(x => x.Previous <= 0 ? double.MaxValue : (x.Current - x.Previous) / x.Previous)
            .Take(3)
            .ToList();
        var previousName = period.Frequency == SystemReportFrequencies.Weekly ? "上一週" : "上個月";
        var highlights = new List<string>
        {
            increased.Count == 0
                ? $"和{previousName}相比，錯誤、排程失敗、寄信失敗、AI 與登入失敗都沒有增加。"
                : $"和{previousName}相比增加的：" + string.Join("、", increased.Select(x => $"{x.Label} {Format(x.Previous, x.Money)} → {Format(x.Current, x.Money)}")) + "。",
        };

        return new SystemReportSectionDto
        {
            Key = SystemReportSectionKeys.Trend,
            Title = "趨勢比較",
            Highlights = highlights,
            Tables =
            [
                new SystemReportTableDto
                {
                    Caption = $"本期（{period.Display}）與{previousName}（{period.Previous().Display}）",
                    Columns = ["項目", "本期", previousName, "變化"],
                    Rows = rows.Select(x => new List<string> { x.Label, Format(x.Current, x.Money), Format(x.Previous, x.Money), Change(x.Current, x.Previous, x.Money) }).ToList(),
                },
            ],
        };
    }

    private static string Format(double value, bool money) => money ? $"NT$ {value:N2}" : $"{value:N0}";

    /// <summary>「+12（+20%）」「-3（-50%）」「持平」；上期是 0 時不算百分比。</summary>
    public static string Change(double current, double previous, bool money = false)
    {
        var delta = current - previous;
        if (Math.Abs(delta) < (money ? 0.005 : 0.5))
        {
            return "持平";
        }

        var sign = delta > 0 ? "+" : "-";
        var amount = money ? $"NT$ {Math.Abs(delta):N2}" : $"{Math.Abs(delta):N0}";
        return previous > 0 ? $"{sign}{amount}（{sign}{Math.Abs(delta) / previous * 100:N0}%）" : $"{sign}{amount}";
    }

    // ───────────── 容量成長與規劃 ─────────────

    private static SystemReportSectionDto Growth(SystemReportFacts facts)
    {
        var period = facts.Period;
        var history = (facts.CapacityHistory ?? []).ToList();
        // 今天的量測也算一筆（快照作業還沒跑時也能用到最新的數字）。
        var today = facts.NowLocal.Date;
        history.RemoveAll(x => x.SnapshotDate.Date == today);
        if (facts.Capacity is { } capacity)
        {
            history.AddRange(capacity.ToSnapshotRows(today));
        }

        (long Start, long End)? Range(string category, string key)
        {
            var samples = history
                .Where(x => x.Category == category && string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase) && x.UsedBytes is not null
                    && x.SnapshotDate.Date >= period.Start && x.SnapshotDate.Date <= period.End)
                .OrderBy(x => x.SnapshotDate)
                .ToList();
            return samples.Count >= 2 ? (samples[0].UsedBytes!.Value, samples[^1].UsedBytes!.Value) : null;
        }

        static string Delta(long bytes) => bytes == 0 ? "沒有變化" : (bytes > 0 ? "+" : "-") + SizeFormatHelper.FormatBytes(Math.Abs(bytes));

        var volumeRows = new List<List<string>>();
        var shortVolumes = new List<string>();
        foreach (var volume in facts.Volumes)
        {
            var label = SystemReportRules.VolumeLabel(volume.Usage);
            var used = volume.Usage.TotalBytes - volume.Usage.FreeBytes;
            var range = Range(CapacitySnapshotKinds.Volume, volume.Usage.Root);
            string yearLater;
            if (volume.Forecast.DailyGrowthBytes is { } daily && volume.Forecast.HasEnoughData)
            {
                var projected = used + Math.Max(0, daily) * 365;
                yearLater = projected <= volume.Usage.TotalBytes
                    ? $"約 {SizeFormatHelper.FormatBytes(projected)}，足夠"
                    : $"約 {SizeFormatHelper.FormatBytes(projected)}，不夠（差約 {SizeFormatHelper.FormatBytes(projected - volume.Usage.TotalBytes)}）";
                if (projected > volume.Usage.TotalBytes)
                {
                    shortVolumes.Add(label);
                }
            }
            else
            {
                yearLater = $"資料累積中（{volume.Forecast.SampleDays} 天）";
            }

            // 表格只放磁碟代號（用途很長，另見「磁碟容量」表）；一年後不夠的重點仍用含用途的名稱。
            volumeRows.Add(
            [
                volume.Usage.Root,
                range is { } r ? SizeFormatHelper.FormatBytes(r.Start) : "—",
                SizeFormatHelper.FormatBytes(used),
                range is { } g ? Delta(g.End - g.Start) : "快照不足",
                SizeFormatHelper.FormatBytes(volume.Usage.TotalBytes),
                yearLater,
            ]);
        }

        var itemRows = new List<List<string>>();
        long? databaseGrowth = null;
        if (Range(CapacitySnapshotKinds.Database, CapacityProbe.DatabaseMainKey) is { } db)
        {
            databaseGrowth = db.End - db.Start;
            itemRows.Add(["資料庫", SizeFormatHelper.FormatBytes(db.Start), SizeFormatHelper.FormatBytes(db.End), Delta(db.End - db.Start)]);
        }

        foreach (var folder in facts.Capacity?.Folders ?? [])
        {
            if (Range(CapacitySnapshotKinds.Folder, folder.Key) is { } f)
            {
                itemRows.Add([folder.Label, SizeFormatHelper.FormatBytes(f.Start), SizeFormatHelper.FormatBytes(f.End), Delta(f.End - f.Start)]);
            }
        }

        var highlights = new List<string>();
        if (databaseGrowth is { } growth)
        {
            highlights.Add($"本期資料庫{(growth == 0 ? "大小沒有變化" : growth > 0 ? $"增加 {SizeFormatHelper.FormatBytes(growth)}" : $"減少 {SizeFormatHelper.FormatBytes(-growth)}")}。");
        }

        highlights.Add(shortVolumes.Count > 0
            ? $"照目前的成長速度，一年後 {string.Join("、", shortVolumes)} 的空間不夠，要開始規劃擴充。"
            : facts.Volumes.Any(x => x.Forecast.HasEnoughData)
                ? "照目前的成長速度，一年內各磁碟的空間都還夠。"
                : $"容量快照還不到 {CapacityForecast.MinimumSampleDays} 天，暫時無法預估一年後的需求。");

        return new SystemReportSectionDto
        {
            Key = SystemReportSectionKeys.Growth,
            Title = "容量成長與規劃",
            Highlights = highlights,
            Tables =
            [
                new SystemReportTableDto
                {
                    Caption = "磁碟",
                    Columns = ["磁碟", "期初已用", "目前已用", "本期增加", "總容量", "一年後預估已用"],
                    Rows = volumeRows,
                },
                new SystemReportTableDto
                {
                    Caption = "資料庫與資料目錄",
                    Columns = ["項目", "期初", "期末", "本期增加"],
                    Rows = itemRows,
                    EmptyText = "容量快照不足，還無法比較期初與期末。",
                },
            ],
            Notes = ["期初、期末取自每天的容量快照（每天一次）；「一年後預估已用」以最近 90 天的成長速度推算。"],
        };
    }

    // ───────────── 備份可還原驗證 ─────────────

    private static SystemReportSectionDto BackupVerify(SystemReportFacts facts)
    {
        var section = new SystemReportSectionDto { Key = SystemReportSectionKeys.BackupVerify, Title = "備份可還原驗證" };
        if (facts.BackupVerification is not { } result)
        {
            section.Highlights.Add(facts.Backups is { Count: 0 } ? "沒有任何備份可以驗證。" : "這次沒有驗證備份（讀不到備份清單）。");
            return section;
        }

        var failed = result.Checks.Where(x => !x.Passed).Select(x => x.Name).ToList();
        section.Highlights.Add(result.Outcome switch
        {
            BackupVerificationOutcomes.Passed => $"最新的備份（{result.BackupCreatedAtLocal:yyyy/MM/dd HH:mm}）可以還原：{result.Checks.Count} 項檢查都通過。",
            BackupVerificationOutcomes.Failed => $"最新的備份（{result.BackupCreatedAtLocal:yyyy/MM/dd HH:mm}）無法還原：{string.Join("、", failed)}沒有通過。",
            _ => $"這次沒有完成備份可還原驗證（{result.BackupCreatedAtLocal:yyyy/MM/dd HH:mm} 的備份）：{result.Checks.LastOrDefault()?.Detail}",
        });
        section.Tables.Add(new SystemReportTableDto
        {
            Caption = "驗證的備份",
            Columns = ["項目", "內容"],
            Rows =
            [
                ["備份檔", result.FileName],
                ["備份時間", $"{result.BackupCreatedAtLocal:yyyy/MM/dd HH:mm}"],
                ["資料庫大小", result.DatabaseBytes > 0 ? SizeFormatHelper.FormatBytes(result.DatabaseBytes) : "—"],
                ["附件與資料檔", $"{result.FileCount:N0} 個"],
                ["驗證花費時間", $"{result.Elapsed.TotalSeconds:N0} 秒"],
            ],
        });
        section.Tables.Add(new SystemReportTableDto
        {
            Caption = "檢查項目",
            Columns = ["檢查", "結果", "說明"],
            Rows = result.Checks.Select(x => new List<string> { x.Name, x.Passed ? "通過" : "沒有通過", x.Detail }).ToList(),
        });
        section.Notes.Add("驗證做的事和還原腳本相同：打開壓縮檔、解壓縮每個檔案比對校驗碼、比對檔案數，把資料庫解到暫存目錄比對雜湊、做完整性檢查並讀取；不碰正在使用的資料庫，做完就刪除暫存檔。");
        return section;
    }

    // ───────────── AI 費用 ─────────────

    private static SystemReportSectionDto AiCost(SystemReportPeriod period, SystemReportAiCost cost)
    {
        static List<List<string>> Lines(IEnumerable<SystemReportAiCostLine> lines)
            => lines.Select(x => new List<string> { x.Name, $"{x.Calls:N0}", $"NT$ {x.CostTwd:N2}", $"NT$ {x.PreviousCostTwd:N2}", Change(x.CostTwd, x.PreviousCostTwd, money: true) }).ToList();

        var highlights = new List<string>
        {
            $"{period.Start:yyyy/MM} 的 AI 費用 NT$ {cost.CostTwd:N2}（前一個月 NT$ {cost.PreviousCostTwd:N2}，{Change(cost.CostTwd, cost.PreviousCostTwd, money: true)}），呼叫 {cost.Calls:N0} 次、{cost.UserCount:N0} 位使用者。",
        };
        if (cost.UnpricedCalls > 0)
        {
            highlights.Add($"有 {cost.UnpricedCalls:N0} 次成功的呼叫沒有計價（模型沒有價格），實際費用比這裡高。");
        }

        return new SystemReportSectionDto
        {
            Key = SystemReportSectionKeys.AiCost,
            Title = "AI 費用",
            Highlights = highlights,
            Tables =
            [
                new SystemReportTableDto { Caption = "依功能", Columns = ["功能", "呼叫次數", "費用", "前一個月", "變化"], Rows = Lines(cost.ByOperation), EmptyText = "這個月沒有 AI 呼叫。" },
                new SystemReportTableDto { Caption = "依模型", Columns = ["模型", "呼叫次數", "費用", "前一個月", "變化"], Rows = Lines(cost.ByModel), EmptyText = "這個月沒有 AI 呼叫。" },
                new SystemReportTableDto
                {
                    Caption = $"費用最高的 {SystemReportMetricsService.TopAiUsers} 位使用者",
                    Columns = ["帳號", "姓名", "呼叫次數", "費用"],
                    Rows = cost.TopUsers.Select(x => new List<string> { x.Account ?? "（系統排程）", x.Name ?? "—", $"{x.Calls:N0}", $"NT$ {x.CostTwd:N2}" }).ToList(),
                    EmptyText = "這個月沒有 AI 呼叫。",
                },
            ],
            Notes = ["費用以呼叫當時的價格與匯率計算（新台幣）；詳細紀錄在「Token 用量」頁。"],
        };
    }

    // ───────────── 帳號與權限清查 ─────────────

    private static readonly Dictionary<string, string> ChangeNames = new(StringComparer.Ordinal)
    {
        [AuditActions.User.Create] = "新增使用者",
        [AuditActions.User.SsoCreate] = "Google 登入自動建立使用者",
        [AuditActions.User.Update] = "修改使用者（含角色、團隊）",
        [AuditActions.User.Enable] = "啟用使用者",
        [AuditActions.User.Disable] = "停用使用者",
        [AuditActions.User.Delete] = "刪除使用者",
        [AuditActions.User.Restore] = "還原使用者",
        [AuditActions.User.Purge] = "永久刪除使用者",
        [AuditActions.User.TwoFactorReset] = "重設兩步驟驗證",
        [AuditActions.User.TwoFactorDisable] = "關閉兩步驟驗證",
        [AuditActions.Role.Create] = "新增角色",
        [AuditActions.Role.Update] = "修改角色權限",
        [AuditActions.Role.Delete] = "刪除角色",
        [AuditActions.Role.Restore] = "還原角色",
        [AuditActions.Role.Purge] = "永久刪除角色",
    };

    private static SystemReportSectionDto AccountReview(SystemReportFacts facts, SystemReportAccountReview review)
    {
        var quarter = facts.Period.Quarter;
        string LastLogin(SystemReportReviewAccount x)
            => x.LastLoginUtc is { } utc ? $"{TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), facts.Zone):yyyy/MM/dd}" : "沒有紀錄";
        static string Yes(bool value) => value ? "是" : "否";
        static string TwoFactor(SystemReportReviewAccount x) => x.Google ? "Google 登入" : x.TwoFactorOrGoogle ? "已啟用" : "未啟用";

        var notes = new List<string>
        {
            "這是清單，請逐一確認帳號與權限是否仍然需要，到「使用者管理」與「角色權限」頁處理；系統不記錄確認結果，這份報告（PDF）可留存作為清查紀錄。",
            "最後登入時間取自稽核紀錄的登入成功；超過稽核紀錄保留天數的登入查不到，會顯示「沒有紀錄」。",
        };

        List<List<string>> Limit(IReadOnlyCollection<List<string>> rows, string what)
        {
            if (rows.Count > SystemReportMetricsService.MaxReviewRows)
            {
                notes.Add($"「{what}」共 {rows.Count:N0} 筆，只列前 {SystemReportMetricsService.MaxReviewRows} 筆。");
            }

            return rows.Take(SystemReportMetricsService.MaxReviewRows).ToList();
        }

        return new SystemReportSectionDto
        {
            Key = SystemReportSectionKeys.AccountReview,
            Title = $"帳號與權限清查（{quarter.Start:yyyy/MM}～{quarter.End.AddDays(-1):yyyy/MM}）",
            Highlights =
            [
                $"啟用中的帳號 {review.EnabledAccounts:N0} 個（停用 {review.DisabledAccounts:N0} 個），其中管理員 {review.Admins.Count:N0} 位。",
                $"超過 {review.StaleDays} 天沒有登入 {review.StaleAccounts.Count:N0} 個、沒有兩步驟驗證 {review.WithoutTwoFactor.Count:N0} 個；本季帳號與角色異動 {review.Changes.Count:N0} 筆。請逐一確認是否仍需要。",
            ],
            Tables =
            [
                new SystemReportTableDto
                {
                    Caption = "管理員",
                    Columns = ["帳號", "姓名", "兩步驟驗證", "最後登入"],
                    Rows = Limit(review.Admins.Select(x => new List<string> { x.Account, x.Name, TwoFactor(x), LastLogin(x) }).ToList(), "管理員"),
                    EmptyText = "沒有啟用中的管理員。",
                },
                new SystemReportTableDto
                {
                    Caption = $"超過 {review.StaleDays} 天沒有登入",
                    Columns = ["帳號", "姓名", "角色", "管理員", "最後登入"],
                    Rows = Limit(review.StaleAccounts.Select(x => new List<string> { x.Account, x.Name, x.Role ?? "—", Yes(x.IsAdmin), LastLogin(x) }).ToList(), "久未登入"),
                    EmptyText = "沒有久未登入的帳號。",
                },
                new SystemReportTableDto
                {
                    Caption = "沒有兩步驟驗證",
                    Columns = ["帳號", "姓名", "角色", "管理員"],
                    Rows = Limit(review.WithoutTwoFactor.Select(x => new List<string> { x.Account, x.Name, x.Role ?? "—", Yes(x.IsAdmin) }).ToList(), "沒有兩步驟驗證"),
                    EmptyText = "每個帳號都有兩步驟驗證（或用 Google 登入）。",
                },
                new SystemReportTableDto
                {
                    Caption = "本季帳號與角色異動",
                    Columns = ["時間", "動作", "操作者", "對象"],
                    Rows = Limit(review.Changes.Select(x => new List<string>
                    {
                        $"{TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(x.OccurredAtUtc, DateTimeKind.Utc), facts.Zone):yyyy/MM/dd HH:mm}",
                        ChangeNames.GetValueOrDefault(x.Action, x.Action),
                        x.ActorAccount ?? "（系統）",
                        x.Target,
                    }).ToList(), "本季帳號與角色異動"),
                    EmptyText = "本季沒有帳號與角色異動。",
                },
            ],
            Notes = notes,
        };
    }
}
