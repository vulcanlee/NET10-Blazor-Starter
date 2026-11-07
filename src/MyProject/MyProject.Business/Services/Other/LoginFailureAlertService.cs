using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.Business.Helpers;
using MyProject.Models.Systems;

namespace MyProject.Business.Services.Other;

/// <summary>登入失敗寫完稽核後評估要不要告警（0.9.166 起）。</summary>
public interface ILoginFailureAlert
{
    /// <summary>⚠️ 絕不丟例外：告警失敗不可影響登入回應。</summary>
    Task EvaluateAsync();
}

/// <summary>登入失敗告警的冷卻狀態（singleton）：上次發出時間與同時只評估一個的閘門。</summary>
public sealed class LoginFailureAlertState
{
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    internal DateTime? LastAlertUtc { get; set; }
}

/// <summary>
/// 登入失敗告警（0.9.166 起）：全系統最近 <c>WindowMinutes</c> 分鐘內的登入失敗達 <c>Threshold</c> 次時，
/// 通知全體管理員並寄信，內文列出最常被試的帳號與來源 IP。發出後 <c>CooldownMinutes</c> 內不再重發。
///
/// 同一帳號連續輸錯由帳號鎖定處理（鎖定時已通知管理員）；這裡抓的是橫向猜測：
/// 不存在的帳號（沒有可以鎖的對象）、多個帳號各試幾次、多個 IP 慢慢試。
/// ⚠️ 冷卻狀態只在記憶體（<see cref="LoginFailureAlertState"/>）：重新啟動後攻擊仍在進行會再發一次，這是刻意的取捨。
/// </summary>
public sealed class LoginFailureAlertService : ILoginFailureAlert
{
    /// <summary>通知裡每個帳號最多顯示幾個字：帳號是攻擊者輸入的任意文字，不截斷會塞爆通知與信件。</summary>
    public const int MaxLabelLength = 30;

    /// <summary>IP 的上限放到 45 字（IPv6 最長的寫法），管理員才能照著通知去封鎖。</summary>
    public const int MaxIpLength = 45;

    /// <summary>通知裡帳號與 IP 各列幾名。</summary>
    public const int TopCount = 5;

    /// <summary>算進告警的稽核動作（與巡檢「安全總覽」的登入類一致；停用帳號的登入不算）。</summary>
    public static readonly IReadOnlyList<string> CountedActions =
    [
        AuditActions.Login.Failed, AuditActions.Login.LockedOut, AuditActions.Login.TwoFactorFailed, AuditActions.Login.SsoFailed,
    ];

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly IOptionsMonitor<LoginFailureAlertSettings> options;
    private readonly INotificationSender notificationSender;
    private readonly LoginFailureAlertState state;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<LoginFailureAlertService> logger;

    public LoginFailureAlertService(
        IDbContextFactory<BackendDBContext> contextFactory,
        IOptionsMonitor<LoginFailureAlertSettings> options,
        INotificationSender notificationSender,
        LoginFailureAlertState state,
        TimeProvider timeProvider,
        ILogger<LoginFailureAlertService> logger)
    {
        this.contextFactory = contextFactory;
        this.options = options;
        this.notificationSender = notificationSender;
        this.state = state;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task EvaluateAsync()
    {
        // 同時有好幾筆失敗進來時只讓一個評估，其他直接略過：正在評估的那一個會看到它們（剛好錯過的，下一次失敗會補上）。
        if (await state.Gate.WaitAsync(0) == false)
        {
            return;
        }

        try
        {
            // 讀設定也在 try 裡：系統參數若被寫成不合法的值，驗證例外不可傳到登入請求。
            var settings = options.CurrentValue;
            if (settings.Threshold <= 0)
            {
                return;
            }

            var now = timeProvider.GetUtcNow().UtcDateTime;
            if (state.LastAlertUtc is { } last && now - last < TimeSpan.FromMinutes(settings.CooldownMinutes))
            {
                return;
            }

            var since = now.AddMinutes(-settings.WindowMinutes);
            await using var context = await contextFactory.CreateDbContextAsync();
            var failures = context.AuditLog.AsNoTracking()
                .Where(x => x.OccurredAt >= since && CountedActions.Contains(x.Action));
            var count = await failures.CountAsync();
            if (count < settings.Threshold)
            {
                return;
            }

            var accounts = await failures.GroupBy(x => x.ActorAccount)
                .Select(g => new { Label = g.Key, Count = g.Count() }).ToListAsync();
            var ips = await failures.Where(x => x.ClientIp != null).GroupBy(x => x.ClientIp)
                .Select(g => new { Label = g.Key, Count = g.Count() }).ToListAsync();

            var accountText = Top(accounts.Select(x => (x.Label ?? "（沒有帳號）", x.Count)), MaxLabelLength);
            var ipText = Top(ips.Select(x => (x.Label!, x.Count)), MaxIpLength);
            var body = $"過去 {settings.WindowMinutes} 分鐘內有 {count} 次登入失敗（門檻 {settings.Threshold} 次），可能有人在猜帳號或密碼。"
                + $"最常被嘗試的帳號：{accountText}；來源 IP：{ipText}。不存在的帳號也算在內。"
                + "請到「稽核紀錄」查看登入失敗的紀錄；同一個 IP 很集中時，可以考慮在防火牆或 IIS 封鎖它。";

            logger.LogWarning("Login failure burst detected. Count={Count}, WindowMinutes={WindowMinutes}, Threshold={Threshold}",
                count, settings.WindowMinutes, settings.Threshold);
            var result = await notificationSender.SendAsync(new NotificationRequest(
                NotificationCategories.LoginFailureBurst,
                $"登入失敗次數異常：{settings.WindowMinutes} 分鐘內 {count} 次",
                body,
                "/audit-logs",
                NotificationTarget.AllAdmins(),
                AlsoEmail: true));

            // 發送失敗（例如資料庫被鎖）不進入冷卻，下一次失敗會再試；否則這段時間內攻擊持續也不會再告警。
            if (result.Failed == false)
            {
                state.LastAlertUtc = now;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to evaluate login failure alert.");
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private static string Top(IEnumerable<(string Label, int Count)> items, int maxLength)
    {
        var top = items.OrderByDescending(x => x.Count).ThenBy(x => x.Label, StringComparer.Ordinal).Take(TopCount)
            .Select(x => $"{Truncate(x.Label, maxLength)}（{x.Count} 次）")
            .ToList();
        return top.Count == 0 ? "（無）" : string.Join("、", top);
    }

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "…";
}
