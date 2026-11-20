using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;

namespace MyProject.Business.Services.DataAccess;

/// <summary>
/// 系統巡檢報告要的統計數字（0.10.39 起）：一個區間內的例外、排程、寄信、AI 呼叫與安全事件，以及目前的帳號狀態。
///
/// 只回數字與固定用語（例外來源、作業名稱、稽核動作代碼），<b>不回訊息內文、帳號、IP</b> ——
/// 報告會寄出系統外，內容要能放心給 AI 與信件（速查表 §6.34）。不寫稽核（這是系統自己的巡檢，不是有人在瀏覽）。
///
/// 時區：例外與 Token 用量存本地時間；稽核、排程執行、寄信、通知存 UTC。呼叫端兩種都傳進來，這裡不換算。
/// </summary>
public class SystemReportMetricsService
{
    /// <summary>新例外算紅燈的次數門檻（同一個簽章在區間內第一次出現、而且已經發生這麼多次）。</summary>
    public const int HighOccurrenceThreshold = 20;

    /// <summary>報告關心的安全事件。</summary>
    public static IReadOnlyList<string> SecurityActions { get; } =
    [
        AuditActions.Login.Failed, AuditActions.Login.LockedOut, AuditActions.Login.TwoFactorFailed, AuditActions.Login.SsoFailed,
        AuditActions.Permission.Denied, AuditActions.Token.ReuseDetected, AuditActions.Ai.QuotaBlocked,
    ];

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly ILogger<SystemReportMetricsService> logger;

    // ⚠️ 只能有一個建構式：DataAccessServiceLifetimeTests 用 GetConstructors().Single()。
    public SystemReportMetricsService(IDbContextFactory<BackendDBContext> contextFactory, ILogger<SystemReportMetricsService> logger)
    {
        this.contextFactory = contextFactory;
        this.logger = logger;
    }

    public async Task<SystemReportMetrics> GetAsync(SystemReportMetricsRange range, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // 例外：區間內第一次出現（新的問題）與區間內還在發生（還沒解決）。
        var newSignatures = await context.ExceptionLog.AsNoTracking()
            .Where(x => x.FirstOccurredAt >= range.StartLocal && x.FirstOccurredAt < range.EndLocal)
            .Select(x => new { x.Source, x.OccurrenceCount })
            .ToListAsync(cancellationToken);
        var activeBySource = await context.ExceptionLog.AsNoTracking()
            .Where(x => x.LastOccurredAt >= range.StartLocal && x.LastOccurredAt < range.EndLocal)
            .GroupBy(x => x.Source)
            .Select(g => new { Source = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        // 排程：區間內每個作業的失敗與中斷次數。
        var jobRuns = await context.JobRun.AsNoTracking()
            .Where(x => x.StartedAtUtc >= range.StartUtc && x.StartedAtUtc < range.EndUtc)
            .GroupBy(x => new { x.JobName, x.Status })
            .Select(g => new { g.Key.JobName, g.Key.Status, Count = g.Count() })
            .ToListAsync(cancellationToken);

        // 寄信：區間內建立的信，與「現在」還卡在佇列超過兩小時的信。
        var emailInRange = await context.EmailOutbox.AsNoTracking()
            .Where(x => x.CreatedAtUtc >= range.StartUtc && x.CreatedAtUtc < range.EndUtc)
            .GroupBy(x => x.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var stuckBefore = range.NowUtc.AddHours(-2);
        var stuckPending = await context.EmailOutbox.AsNoTracking()
            .CountAsync(x => x.Status == EmailOutboxStatus.Pending && x.CreatedAtUtc < stuckBefore, cancellationToken);

        // AI：區間內的呼叫、失敗、費用與沒有計價的呼叫。
        var aiRows = await context.TokenUsageLog.AsNoTracking()
            .Where(x => x.OccurredAt >= range.StartLocal && x.OccurredAt < range.EndLocal)
            .Select(x => new { x.Success, x.CostTwd })
            .ToListAsync(cancellationToken);

        // 安全事件（稽核是 UTC）。
        var securityActions = SecurityActions.ToList();
        var auditCounts = await context.AuditLog.AsNoTracking()
            .Where(x => x.OccurredAt >= range.StartUtc && x.OccurredAt < range.EndUtc && securityActions.Contains(x.Action))
            .GroupBy(x => x.Action)
            .Select(g => new { Action = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        // 登入失敗爆量告警：同一次告警會發給每位管理員，以標題（含時間窗與次數）去重。
        var loginBursts = await context.Notification.AsNoTracking()
            .Where(x => x.Category == NotificationCategories.LoginFailureBurst && x.CreatedAtUtc >= range.StartUtc && x.CreatedAtUtc < range.EndUtc)
            .Select(x => new { x.Title, Minute = x.CreatedAtUtc })
            .ToListAsync(cancellationToken);

        // 目前的帳號狀態。
        var lockedAccounts = await context.MyUser.AsNoTracking()
            .CountAsync(x => x.Status && x.LockoutEndUtc != null && x.LockoutEndUtc > range.NowUtc, cancellationToken);
        var adminsWithoutTwoFactor = await context.MyUser.AsNoTracking()
            .CountAsync(x => x.IsAdmin && x.Status && !x.TwoFactorEnabled && (x.OAuthProvider == null || x.OAuthProvider.ToLower() != "google"), cancellationToken);
        var enabledAdmins = await context.MyUser.AsNoTracking().CountAsync(x => x.IsAdmin && x.Status, cancellationToken);
        logger.LogDebug("System report metrics collected. StartLocal={StartLocal}, EndLocal={EndLocal}", range.StartLocal, range.EndLocal);

        return new SystemReportMetrics
        {
            NewExceptionSignatures = newSignatures.Count,
            NewExceptionsSevere = newSignatures.Count(x => x.OccurrenceCount >= HighOccurrenceThreshold
                || x.Source == ExceptionSources.Background || x.Source == ExceptionSources.Startup),
            ActiveExceptionSignaturesBySource = activeBySource.ToDictionary(x => x.Source, x => x.Count),
            FailedJobRuns = jobRuns.Where(x => x.Status == JobRunStatuses.Failed).ToDictionary(x => x.JobName, x => x.Count),
            InterruptedJobRuns = jobRuns.Where(x => x.Status == JobRunStatuses.Interrupted).Sum(x => x.Count),
            EmailsCreated = emailInRange.Sum(x => x.Count),
            EmailsFailed = emailInRange.Where(x => x.Status == EmailOutboxStatus.Failed).Sum(x => x.Count),
            EmailsStuckPending = stuckPending,
            AiCalls = aiRows.Count,
            AiFailedCalls = aiRows.Count(x => x.Success == false),
            AiCostTwd = aiRows.Sum(x => x.CostTwd ?? 0),
            AiUnpricedCalls = aiRows.Count(x => x.Success && x.CostTwd == null),
            SecurityEvents = securityActions.ToDictionary(x => x, x => auditCounts.FirstOrDefault(c => c.Action == x)?.Count ?? 0),
            LoginFailureBursts = loginBursts.Select(x => x.Title).Distinct().Count(),
            LockedAccounts = lockedAccounts,
            EnabledAdmins = enabledAdmins,
            AdminsWithoutTwoFactor = adminsWithoutTwoFactor,
        };
    }
}

/// <summary>統計區間：本地時間與 UTC 各一組（結束不含），加上「現在」（判斷卡住的信、鎖定中的帳號）。</summary>
public sealed record SystemReportMetricsRange(DateTime StartLocal, DateTime EndLocal, DateTime StartUtc, DateTime EndUtc, DateTime NowUtc);

/// <summary>區間統計。只有數字與固定代碼。</summary>
public sealed class SystemReportMetrics
{
    public int NewExceptionSignatures { get; init; }

    /// <summary>新例外裡已經發生很多次、或來自背景作業與系統啟動的（使用者看不到但系統在壞）。</summary>
    public int NewExceptionsSevere { get; init; }

    /// <summary>區間內還在發生的例外簽章數，依來源（畫面、WebAPI、瀏覽器、背景作業…）。</summary>
    public IReadOnlyDictionary<string, int> ActiveExceptionSignaturesBySource { get; init; } = new Dictionary<string, int>();

    /// <summary>區間內失敗的執行次數，依作業名稱。</summary>
    public IReadOnlyDictionary<string, int> FailedJobRuns { get; init; } = new Dictionary<string, int>();

    public int InterruptedJobRuns { get; init; }

    public int EmailsCreated { get; init; }

    public int EmailsFailed { get; init; }

    /// <summary>現在還在等待寄出、而且建立超過兩小時的信。</summary>
    public int EmailsStuckPending { get; init; }

    public int AiCalls { get; init; }

    public int AiFailedCalls { get; init; }

    public double AiCostTwd { get; init; }

    public int AiUnpricedCalls { get; init; }

    /// <summary>安全事件次數，依稽核動作代碼（<see cref="SystemReportMetricsService.SecurityActions"/>，沒發生的為 0）。</summary>
    public IReadOnlyDictionary<string, int> SecurityEvents { get; init; } = new Dictionary<string, int>();

    public int LoginFailureBursts { get; init; }

    public int LockedAccounts { get; init; }

    public int EnabledAdmins { get; init; }

    /// <summary>啟用中、沒有開兩步驟驗證的管理員（只用 Google 登入的不算）。</summary>
    public int AdminsWithoutTwoFactor { get; init; }
}
