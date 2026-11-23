using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;

namespace MyProject.Business.Services.DataAccess;

/// <summary>
/// 月報才有的資料（0.10.41 起）：AI 費用與季度帳號與權限清查。
///
/// ⚠️ 這兩項<b>會回帳號與姓名</b>（使用者 2026/11/20 決定：清查要列出帳號才有意義，PDF 是資安稽核的留存）。
/// 只能放進報告內容的表格；通知、信件內文與 AI 說明只能用數量（見 SystemReportSectionDto.Highlights）。
/// 不寫稽核（系統自己的巡檢）。時間：Token 用量是本地時間；稽核是 UTC，由呼叫端換算顯示。
/// </summary>
public partial class SystemReportMetricsService
{
    /// <summary>AI 費用列出前幾名使用者。</summary>
    public const int TopAiUsers = 10;

    /// <summary>帳號清查每張清單最多列幾筆（其餘只算數量）。</summary>
    public const int MaxReviewRows = 200;

    /// <summary>帳號清查關心的異動：建立、修改（含角色與團隊）、啟用停用、刪除還原、兩步驟驗證重設與關閉、角色的增刪改。</summary>
    public static IReadOnlyList<string> AccountChangeActions { get; } =
    [
        AuditActions.User.Create, AuditActions.User.SsoCreate, AuditActions.User.Update, AuditActions.User.Enable, AuditActions.User.Disable,
        AuditActions.User.Delete, AuditActions.User.Restore, AuditActions.User.Purge, AuditActions.User.TwoFactorReset, AuditActions.User.TwoFactorDisable,
        AuditActions.Role.Create, AuditActions.Role.Update, AuditActions.Role.Delete, AuditActions.Role.Restore, AuditActions.Role.Purge,
    ];

    /// <summary>
    /// 一個區間與上一個區間的 AI 費用：依功能、依模型、前 <see cref="TopAiUsers"/> 名使用者（含帳號與姓名）。
    /// 區間是本地時間（Token 用量存本地時間），結束不含。
    /// </summary>
    public async Task<SystemReportAiCost> GetAiCostAsync(
        DateTime startLocal, DateTime endLocal, DateTime previousStartLocal, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await context.TokenUsageLog.AsNoTracking()
            .Where(x => x.OccurredAt >= previousStartLocal && x.OccurredAt < endLocal)
            .Select(x => new { x.OccurredAt, x.Operation, x.Model, x.UserId, x.Account, x.Success, x.CostTwd })
            .ToListAsync(cancellationToken);
        var current = rows.Where(x => x.OccurredAt >= startLocal).ToList();
        var previous = rows.Where(x => x.OccurredAt < startLocal).ToList();

        List<SystemReportAiCostLine> Group(Func<string?, string?, string> key)
            => current.GroupBy(x => key(x.Operation, x.Model))
                .Select(g => new SystemReportAiCostLine(g.Key, g.Count(), g.Sum(x => x.CostTwd ?? 0),
                    previous.Where(x => key(x.Operation, x.Model) == g.Key).Sum(x => x.CostTwd ?? 0)))
                .OrderByDescending(x => x.CostTwd)
                .ThenBy(x => x.Name, StringComparer.Ordinal)
                .ToList();

        var byUser = current
            .GroupBy(x => new { x.UserId, Account = x.Account ?? string.Empty })
            .Select(g => new { g.Key.UserId, g.Key.Account, Calls = g.Count(), Cost = g.Sum(x => x.CostTwd ?? 0) })
            .OrderByDescending(x => x.Cost)
            .ThenBy(x => x.Account, StringComparer.Ordinal)
            .ToList();
        var userIds = byUser.Take(TopAiUsers).Where(x => x.UserId is not null).Select(x => x.UserId!.Value).ToList();
        var names = await context.MyUser.AsNoTracking().IgnoreQueryFilters()
            .Where(x => userIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);

        return new SystemReportAiCost
        {
            CostTwd = current.Sum(x => x.CostTwd ?? 0),
            PreviousCostTwd = previous.Sum(x => x.CostTwd ?? 0),
            Calls = current.Count,
            PreviousCalls = previous.Count,
            FailedCalls = current.Count(x => !x.Success),
            UnpricedCalls = current.Count(x => x.Success && x.CostTwd == null),
            UserCount = byUser.Count(x => x.UserId is not null),
            ByOperation = Group((operation, _) => string.IsNullOrWhiteSpace(operation) ? "（未標示）" : operation),
            ByModel = Group((_, model) => string.IsNullOrWhiteSpace(model) ? "（未標示）" : model),
            TopUsers = byUser.Take(TopAiUsers)
                .Select(x => new SystemReportAiUserCost(
                    x.UserId is null ? null : x.Account,
                    x.UserId is { } id ? names.GetValueOrDefault(id) : null,
                    x.Calls,
                    x.Cost))
                .ToList(),
        };
    }

    /// <summary>
    /// 帳號與權限清查（列清單，不做簽核）：全部管理員、久未登入的帳號、沒有兩步驟驗證的帳號、季度內的帳號與角色異動。
    /// 最後登入時間取自稽核紀錄的登入成功（UTC）；超過稽核保留天數的登入查不到，會被當成「沒有登入紀錄」。
    /// </summary>
    public async Task<SystemReportAccountReview> GetAccountReviewAsync(
        DateTime quarterStartUtc, DateTime endUtc, DateTime nowUtc, DateTime nowLocal, int staleDays, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var users = await context.MyUser.AsNoTracking()
            .Select(x => new
            {
                x.Id,
                x.Account,
                x.Name,
                x.Status,
                x.IsAdmin,
                x.TwoFactorEnabled,
                x.OAuthProvider,
                x.CreateAt,
                Role = x.RoleView == null ? null : x.RoleView.Name,
            })
            .ToListAsync(cancellationToken);
        var loginActions = new[] { AuditActions.Login.Success, AuditActions.Login.SsoSuccess };
        var lastLogins = await context.AuditLog.AsNoTracking()
            .Where(x => x.ActorUserId != null && loginActions.Contains(x.Action) && x.Success)
            .GroupBy(x => x.ActorUserId!.Value)
            .Select(g => new { UserId = g.Key, Last = g.Max(x => x.OccurredAt) })
            .ToDictionaryAsync(x => x.UserId, x => x.Last, cancellationToken);

        SystemReportReviewAccount ToAccount(int id, string account, string name, bool isAdmin, string? role, bool twoFactor, bool google)
            => new(account, name, isAdmin, role, twoFactor || google, google, lastLogins.TryGetValue(id, out var last) ? last : null);

        var enabled = users.Where(x => x.Status).ToList();
        var staleBeforeUtc = nowUtc.AddDays(-staleDays);
        var accounts = enabled
            .Select(x => new
            {
                x.CreateAt,
                Account = ToAccount(x.Id, x.Account, x.Name, x.IsAdmin, x.Role, x.TwoFactorEnabled, string.Equals(x.OAuthProvider, "google", StringComparison.OrdinalIgnoreCase)),
            })
            .OrderBy(x => x.Account.Account, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // 久未登入：最後一次登入早於門檻；沒有登入紀錄的只算建立超過門檻天數的（剛建立還沒登入的不算）。
        // MyUser.CreateAt 是本地時間，和本地的現在比。
        var stale = accounts
            .Where(x => x.Account.LastLoginUtc is { } last ? last < staleBeforeUtc : x.CreateAt < nowLocal.AddDays(-staleDays))
            .Select(x => x.Account)
            .ToList();
        var withoutTwoFactor = accounts.Where(x => !x.Account.TwoFactorOrGoogle).Select(x => x.Account).ToList();
        var admins = accounts.Where(x => x.Account.IsAdmin).Select(x => x.Account).ToList();

        var actions = AccountChangeActions.ToList();
        var changeRows = await context.AuditLog.AsNoTracking()
            .Where(x => x.OccurredAt >= quarterStartUtc && x.OccurredAt < endUtc && x.Success && actions.Contains(x.Action))
            .OrderBy(x => x.OccurredAt)
            .ThenBy(x => x.Id)
            .Select(x => new { x.OccurredAt, x.Action, x.ActorAccount, x.TargetType, x.TargetId })
            .ToListAsync(cancellationToken);
        var userIds = changeRows.Where(x => x.TargetType == nameof(MyUser)).Select(x => int.TryParse(x.TargetId, out var id) ? id : 0).Distinct().ToList();
        var roleIds = changeRows.Where(x => x.TargetType == nameof(RoleView)).Select(x => int.TryParse(x.TargetId, out var id) ? id : 0).Distinct().ToList();
        // 已刪除或已永久刪除的對象也要顯示名字（軟刪除的仍查得到；永久刪除的只剩編號）。
        var userNames = await context.MyUser.AsNoTracking().IgnoreQueryFilters()
            .Where(x => userIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Account, cancellationToken);
        var roleNames = await context.RoleView.AsNoTracking().IgnoreQueryFilters()
            .Where(x => roleIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var changes = changeRows.Select(x =>
        {
            var id = int.TryParse(x.TargetId, out var parsed) ? parsed : 0;
            var target = x.TargetType switch
            {
                nameof(MyUser) => userNames.TryGetValue(id, out var account) ? $"使用者 {account}" : $"使用者（編號 {x.TargetId}）",
                nameof(RoleView) => roleNames.TryGetValue(id, out var role) ? $"角色 {role}" : $"角色（編號 {x.TargetId}）",
                _ => x.TargetId ?? string.Empty,
            };
            return new SystemReportAccountChange(x.OccurredAt, x.Action, x.ActorAccount, target);
        }).ToList();

        logger.LogDebug("System report account review collected. Admins={Admins}, Stale={Stale}, Changes={Changes}", admins.Count, stale.Count, changes.Count);
        return new SystemReportAccountReview
        {
            StaleDays = staleDays,
            EnabledAccounts = enabled.Count,
            DisabledAccounts = users.Count - enabled.Count,
            Admins = admins,
            StaleAccounts = stale,
            WithoutTwoFactor = withoutTwoFactor,
            Changes = changes,
        };
    }
}

/// <summary>一個功能或模型的 AI 費用（新台幣）。</summary>
public sealed record SystemReportAiCostLine(string Name, int Calls, double CostTwd, double PreviousCostTwd);

/// <summary>一位使用者的 AI 費用；系統自己呼叫的（排程作業）帳號為 null。</summary>
public sealed record SystemReportAiUserCost(string? Account, string? Name, int Calls, double CostTwd);

/// <summary>月報的 AI 費用。</summary>
public sealed class SystemReportAiCost
{
    public double CostTwd { get; init; }

    public double PreviousCostTwd { get; init; }

    public int Calls { get; init; }

    public int PreviousCalls { get; init; }

    public int FailedCalls { get; init; }

    public int UnpricedCalls { get; init; }

    /// <summary>有用到 AI 的使用者人數（不含系統自己呼叫的）。</summary>
    public int UserCount { get; init; }

    public IReadOnlyList<SystemReportAiCostLine> ByOperation { get; init; } = [];

    public IReadOnlyList<SystemReportAiCostLine> ByModel { get; init; } = [];

    public IReadOnlyList<SystemReportAiUserCost> TopUsers { get; init; } = [];
}

/// <summary>清查的一個帳號。<see cref="LastLoginUtc"/> 取自稽核紀錄，查不到時為 null。</summary>
public sealed record SystemReportReviewAccount(
    string Account, string Name, bool IsAdmin, string? Role, bool TwoFactorOrGoogle, bool Google, DateTime? LastLoginUtc);

/// <summary>季度內的一筆帳號或角色異動（時間是 UTC；動作是稽核代碼）。</summary>
public sealed record SystemReportAccountChange(DateTime OccurredAtUtc, string Action, string? ActorAccount, string Target);

/// <summary>帳號與權限清查（只列清單）。</summary>
public sealed class SystemReportAccountReview
{
    public int StaleDays { get; init; }

    public int EnabledAccounts { get; init; }

    public int DisabledAccounts { get; init; }

    public IReadOnlyList<SystemReportReviewAccount> Admins { get; init; } = [];

    public IReadOnlyList<SystemReportReviewAccount> StaleAccounts { get; init; } = [];

    public IReadOnlyList<SystemReportReviewAccount> WithoutTwoFactor { get; init; } = [];

    public IReadOnlyList<SystemReportAccountChange> Changes { get; init; } = [];
}
