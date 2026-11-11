using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.DataAccess;

namespace MyProject.Business.Services.Other;

/// <summary>刷新 Token 的發出管道（寫在 <see cref="RefreshSession.ClientKind"/>）。</summary>
public static class RefreshClientKinds
{
    /// <summary>以帳號密碼呼叫 API 登入（未來的 App 也走這條）。</summary>
    public const string Api = "api";

    /// <summary>
    /// 網頁以 Cookie 換 Token（WASM 遷移過渡期，0.10.0～0.10.7）。0.10.8 起不再發出（bridge 已移除），
    /// 常數留著辨識資料表裡的舊紀錄（到期後由排程清掉）。
    /// </summary>
    public const string WebBridge = "web-bridge";

    /// <summary>
    /// 網頁以帳號密碼（或 Google）直接換 Token（0.10.8 起，WASM 遷移 M9）。效期依「記住我」：勾了整串上限 <c>SessionSettings:RememberMeDays</c>，
    /// 沒勾則刷新 Token 閒置上限 <c>SessionSettings:IdleTimeoutMinutes</c>（滑動）；存取 Token <c>SessionSettings:AccessTokenMinutes</c>。
    /// 同一次網頁登入的每個分頁各自一串（<see cref="IRefreshTokenStore.ForkAsync"/>），以 <see cref="RefreshSession.LoginSessionId"/> 串起來，登出時一起撤銷。
    /// </summary>
    public const string Web = "web";

    /// <summary>瀏覽器裡的 WASM 前端用的管道：沒有主要角色的帳號不能用網頁（與 Server 版相同）。</summary>
    public static bool IsBrowser(string? clientKind) => clientKind is Web or WebBridge;
}

/// <summary>刷新的結果。</summary>
public enum RefreshRotationStatus
{
    /// <summary>成功，已換發新的刷新 Token。</summary>
    Rotated,

    /// <summary>不認得、已過期或已撤銷（例如已登出）。</summary>
    Invalid,

    /// <summary>
    /// 剛被換掉的 Token 在寬限期（<see cref="RefreshTokenStore.ReuseGracePeriod"/>）內又被用：多半是同時刷新或回應遺失後重送。
    /// 回 401 但不撤銷整串，換到新 Token 的那一邊照常使用。
    /// </summary>
    Superseded,

    /// <summary>已被換掉的舊 Token 又被使用，可能外洩，整串已撤銷。</summary>
    Reused,

    /// <summary>帳號不存在、已刪除或已停用，整串已撤銷。</summary>
    UserInactive,

    /// <summary>工作階段版本已換（改密碼、強制登出、改角色等），整串已撤銷。</summary>
    SessionRevoked,
}

/// <summary>新發出的刷新 Token 原值（只回給呼叫端這一次，資料庫只存雜湊）。</summary>
/// <param name="FamilyId">這一串的識別（0.10.8 起寫進存取 Token，驗證時檢查這一串是否已撤銷）。</param>
/// <param name="ClientKind">發出管道（<see cref="RefreshClientKinds"/>），決定存取 Token 的效期。</param>
public sealed record IssuedRefreshToken(string Token, DateTime ExpiresAtUtc, string FamilyId = "", string ClientKind = "");

/// <summary>決定刷新 Token 效期的依據（發出管道與是否「記住我」）。</summary>
public delegate TimeSpan RefreshLifetimePolicy(string clientKind, bool isPersistent);

/// <param name="UserId">找得到紀錄時是哪一位使用者（稽核用），找不到為 null。</param>
public sealed record RefreshRotationResult(RefreshRotationStatus Status, int? UserId, MyUser? User = null, IssuedRefreshToken? Token = null, bool IsPersistent = false);

/// <summary>
/// 刷新 Token 的伺服器端紀錄（0.10.0 起，WASM 遷移 M1；singleton，每個方法用工廠開新的 context）。
/// 規則見 <see cref="RefreshSession"/>。⚠️ 不可把 Token 原值寫進日誌（<c>LoggingConventionTests</c> 守門）。
/// </summary>
public interface IRefreshTokenStore
{
    /// <summary>為使用者發一個新的刷新 Token（新的一串）。</summary>
    /// <param name="absoluteExpiresAtUtc">整串的最晚到期時間（網頁以 Cookie 換出時取 Cookie 的到期時間）；null＝不設上限。</param>
    Task<IssuedRefreshToken> IssueAsync(MyUser user, bool isPersistent, string clientKind, string? loginSessionId, string? deviceLabel, TimeSpan lifetime, DateTime? absoluteExpiresAtUtc = null, CancellationToken cancellationToken = default);

    /// <summary>用刷新 Token 換一個新的；檢查不過時依情況撤銷整串。</summary>
    Task<RefreshRotationResult> RotateAsync(string token, TimeSpan lifetime, CancellationToken cancellationToken = default);

    /// <summary>同上，新 Token 的效期依這一串的發出管道與「記住我」決定（0.10.8 起）。</summary>
    Task<RefreshRotationResult> RotateAsync(string token, RefreshLifetimePolicy lifetime, CancellationToken cancellationToken = default);

    /// <summary>
    /// 以自己的刷新 Token 為新分頁開一串（0.10.8 起，只限 <see cref="RefreshClientKinds.Web"/>）：沿用登入識別、「記住我」與整串上限，
    /// 原本那一串不受影響（不輪替）。來源必須是目前有效的那一個（被換掉、撤銷、過期、版本不符都不行）。
    /// </summary>
    Task<RefreshRotationResult> ForkAsync(string token, RefreshLifetimePolicy lifetime, CancellationToken cancellationToken = default);

    /// <summary>
    /// 自己換了工作階段版本（改密碼、啟用／停用兩步驟驗證、管理員改到自己）之後，把這一串換成新的一串（0.10.8 起）：
    /// 沿用發出管道、登入識別、「記住我」與整串上限，舊的一串撤銷。這一串已不存在（登出、過期、不屬於這個人）時回 <see cref="RefreshRotationStatus.Invalid"/>。
    /// <paramref name="user"/> 必須是存檔後重新讀出的使用者（新版本）。
    /// </summary>
    Task<RefreshRotationResult> ReissueFamilyAsync(string familyId, MyUser user, RefreshLifetimePolicy lifetime, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// 這一串還有沒有有效的刷新 Token（0.10.8 起，存取 Token 驗證時呼叫：登出、重用撤銷之後，已發出的存取 Token 也立即失效）。
    /// 有效的結果在同一個行程快取 <see cref="RefreshTokenStore.FamilyCheckWindow"/>；這個行程裡撤銷時立即清掉。
    /// </summary>
    Task<bool> IsFamilyActiveAsync(string familyId, CancellationToken cancellationToken = default);

    /// <summary>撤銷這個 Token 所屬的整串（登出）；回傳擁有者，Token 不認得時回 null。</summary>
    Task<int?> RevokeFamilyAsync(string token, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// 登出（0.10.8 起）：網頁登入（有登入識別）撤銷這次登入的每一串（每個分頁各一串），其他管道撤銷這一串。
    /// 回傳擁有者與發出管道；Token 不認得時兩者皆為 null。
    /// </summary>
    Task<(int? UserId, string? ClientKind)> RevokeLoginAsync(string token, string reason, CancellationToken cancellationToken = default);

    /// <summary>撤銷某次網頁登入換出的所有 Token（從網頁登出時）；回傳撤銷筆數。</summary>
    Task<int> RevokeByLoginSessionAsync(string loginSessionId, string reason, CancellationToken cancellationToken = default);

    /// <summary>刪除到期時間早於 <paramref name="cutoffUtc"/> 的紀錄；回傳刪除筆數。</summary>
    Task<int> PurgeExpiredAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default);

    /// <summary>「資料保留與清理」頁用：總筆數、最舊建立時間（UTC）、到期時間早於 <paramref name="cutoffUtc"/> 的筆數。</summary>
    Task<RetentionStats> GetRetentionStatsAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default);
}

public sealed class RefreshTokenStore : IRefreshTokenStore
{
    /// <summary>
    /// 剛被換掉的 Token 在這段時間內又被用，視為「同時刷新或重送」而不是外洩，不撤銷整串（<see cref="RefreshRotationStatus.Superseded"/>）。
    /// 取 30 秒：涵蓋多個分頁同時到期刷新與網路重送；超過就是重用，整串撤銷。攻擊者要在這 30 秒內搶先使用才躲得過偵測。
    /// </summary>
    public static readonly TimeSpan ReuseGracePeriod = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 「這一串仍有效」的結果可以沿用多久（0.10.8 起）。同一個行程裡撤銷（登出、重用、換一串）會立即清掉；
    /// 只影響別的行程（多台主機或 IIS 重疊回收）—— 那邊最晚這段時間後才看到登出。
    /// </summary>
    public static readonly TimeSpan FamilyCheckWindow = TimeSpan.FromSeconds(30);

    private const int TokenByteLength = 32;
    private const int DeviceLabelMaxLength = 200;
    private const int FamilyCacheLimit = 10_000;

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<RefreshTokenStore> logger;
    private readonly ConcurrentDictionary<string, DateTime> activeFamilies = new(StringComparer.Ordinal);

    public RefreshTokenStore(IDbContextFactory<BackendDBContext> contextFactory, TimeProvider timeProvider, ILogger<RefreshTokenStore> logger)
    {
        this.contextFactory = contextFactory;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<IssuedRefreshToken> IssueAsync(MyUser user, bool isPersistent, string clientKind, string? loginSessionId, string? deviceLabel, TimeSpan lifetime, DateTime? absoluteExpiresAtUtc = null, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var (session, token) = NewSession(user.Id, Guid.NewGuid().ToString("N"), user.SecurityStamp, isPersistent, clientKind, loginSessionId, deviceLabel, lifetime, absoluteExpiresAtUtc);
        context.RefreshSession.Add(session);
        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Refresh token issued. UserId={UserId}, ClientKind={ClientKind}, Persistent={Persistent}", user.Id, clientKind, isPersistent);
        return Issued(session, token);
    }

    public Task<RefreshRotationResult> RotateAsync(string token, TimeSpan lifetime, CancellationToken cancellationToken = default)
        => RotateAsync(token, (_, _) => lifetime, cancellationToken);

    public async Task<RefreshRotationResult> RotateAsync(string token, RefreshLifetimePolicy lifetime, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(token))
        {
            return new RefreshRotationResult(RefreshRotationStatus.Invalid, null);
        }

        var now = UtcNow();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var hash = HashToken(token);
        var session = await context.RefreshSession.AsNoTracking().FirstOrDefaultAsync(x => x.TokenHash == hash, cancellationToken);
        if (session is null)
        {
            return new RefreshRotationResult(RefreshRotationStatus.Invalid, null);
        }

        if (session.ReplacedById is not null)
        {
            return await ReplacedTokenUsedAsync(context, session, now, cancellationToken);
        }

        if (session.RevokedAtUtc is not null || session.ExpiresAtUtc <= now)
        {
            return new RefreshRotationResult(RefreshRotationStatus.Invalid, session.MyUserId);
        }

        // 全域查詢篩選會排除已刪除的使用者，所以「找不到」也涵蓋已刪除。
        var user = await context.MyUser.AsNoTracking().FirstOrDefaultAsync(x => x.Id == session.MyUserId, cancellationToken);
        if (user is null || !user.Status)
        {
            await RevokeFamilyAsync(context, session.FamilyId, now, "UserInactive", cancellationToken);
            activeFamilies.TryRemove(session.FamilyId, out _);
            return new RefreshRotationResult(RefreshRotationStatus.UserInactive, session.MyUserId);
        }

        if (!SecurityStamps.Matches(session.SecurityStamp, user.SecurityStamp))
        {
            await RevokeFamilyAsync(context, session.FamilyId, now, "SessionRevoked", cancellationToken);
            activeFamilies.TryRemove(session.FamilyId, out _);
            return new RefreshRotationResult(RefreshRotationStatus.SessionRevoked, session.MyUserId);
        }

        // 先以條件式更新「搶」下這一筆：同一個 Token 同時被用兩次時，只有一個請求更新得到，另一個視為重用。
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var claimed = await context.RefreshSession
            .Where(x => x.Id == session.Id && x.RevokedAtUtc == null && x.ReplacedById == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.RevokedAtUtc, now)
                .SetProperty(x => x.RevokedReason, "Rotated")
                .SetProperty(x => x.LastUsedAtUtc, now), cancellationToken);
        if (claimed == 0)
        {
            // 搶輸了：重新讀這一筆。被別的請求換掉了才算「已被取代／重用」；同時被登出或整串撤銷則只是無效，不要誤記成重用。
            await transaction.RollbackAsync(cancellationToken);
            var current = await context.RefreshSession.AsNoTracking().FirstAsync(x => x.Id == session.Id, cancellationToken);
            return current.ReplacedById is not null
                ? await ReplacedTokenUsedAsync(context, current, now, cancellationToken)
                : new RefreshRotationResult(RefreshRotationStatus.Invalid, session.MyUserId);
        }

        var (next, nextToken) = NewSession(user.Id, session.FamilyId, user.SecurityStamp, session.IsPersistent, session.ClientKind, session.LoginSessionId, session.DeviceLabel, lifetime(session.ClientKind, session.IsPersistent), session.AbsoluteExpiresAtUtc);
        context.RefreshSession.Add(next);
        await context.SaveChangesAsync(cancellationToken);
        await context.RefreshSession
            .Where(x => x.Id == session.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReplacedById, next.Id), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new RefreshRotationResult(RefreshRotationStatus.Rotated, user.Id, user, Issued(next, nextToken), session.IsPersistent);
    }

    public async Task<RefreshRotationResult> ForkAsync(string token, RefreshLifetimePolicy lifetime, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(token))
        {
            return new RefreshRotationResult(RefreshRotationStatus.Invalid, null);
        }

        var now = UtcNow();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var hash = HashToken(token);
        var session = await context.RefreshSession.AsNoTracking().FirstOrDefaultAsync(x => x.TokenHash == hash, cancellationToken);

        // 只接受目前有效的那一個：被換掉的舊 Token 不能拿來開新的一串（否則外洩的舊 Token 可以躲過重用偵測）。
        if (session is null || session.ReplacedById is not null || session.RevokedAtUtc is not null || session.ExpiresAtUtc <= now
            || session.ClientKind != RefreshClientKinds.Web)
        {
            return new RefreshRotationResult(RefreshRotationStatus.Invalid, session?.MyUserId);
        }

        var user = await context.MyUser.AsNoTracking().FirstOrDefaultAsync(x => x.Id == session.MyUserId, cancellationToken);
        if (user is null || !user.Status)
        {
            return new RefreshRotationResult(RefreshRotationStatus.UserInactive, session.MyUserId);
        }

        if (!SecurityStamps.Matches(session.SecurityStamp, user.SecurityStamp))
        {
            return new RefreshRotationResult(RefreshRotationStatus.SessionRevoked, session.MyUserId);
        }

        var (fork, forkToken) = NewSession(user.Id, Guid.NewGuid().ToString("N"), user.SecurityStamp, session.IsPersistent, session.ClientKind, session.LoginSessionId, session.DeviceLabel, lifetime(session.ClientKind, session.IsPersistent), session.AbsoluteExpiresAtUtc);
        context.RefreshSession.Add(fork);
        await context.SaveChangesAsync(cancellationToken);

        // 讀來源與新增之間剛好登出（依登入撤銷）：新的這串也要作廢，否則登出後新分頁仍維持登入（0.10.8 獨立審查）。
        var sourceRevoked = await context.RefreshSession.AsNoTracking()
            .AnyAsync(x => x.Id == session.Id && x.RevokedAtUtc != null && x.ReplacedById == null, cancellationToken);
        if (sourceRevoked)
        {
            await RevokeFamilyAsync(context, fork.FamilyId, now, "SessionRevoked", cancellationToken);
            return new RefreshRotationResult(RefreshRotationStatus.SessionRevoked, user.Id);
        }

        logger.LogInformation("Refresh token family forked for another tab. UserId={UserId}, Persistent={Persistent}", user.Id, session.IsPersistent);
        return new RefreshRotationResult(RefreshRotationStatus.Rotated, user.Id, user, Issued(fork, forkToken), session.IsPersistent);
    }

    public async Task<RefreshRotationResult> ReissueFamilyAsync(string familyId, MyUser user, RefreshLifetimePolicy lifetime, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(familyId))
        {
            return new RefreshRotationResult(RefreshRotationStatus.Invalid, user.Id);
        }

        var now = UtcNow();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var current = await context.RefreshSession.AsNoTracking()
            .Where(x => x.FamilyId == familyId && x.MyUserId == user.Id && x.RevokedAtUtc == null && x.ExpiresAtUtc > now)
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
        {
            return new RefreshRotationResult(RefreshRotationStatus.Invalid, user.Id);
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await RevokeFamilyAsync(context, familyId, now, reason, cancellationToken);
        var (next, nextToken) = NewSession(user.Id, Guid.NewGuid().ToString("N"), user.SecurityStamp, current.IsPersistent, current.ClientKind, current.LoginSessionId, current.DeviceLabel, lifetime(current.ClientKind, current.IsPersistent), current.AbsoluteExpiresAtUtc);
        context.RefreshSession.Add(next);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        activeFamilies.TryRemove(familyId, out _);
        logger.LogInformation("Refresh token family replaced after the user changed their own session. UserId={UserId}, Reason={Reason}", user.Id, reason);
        return new RefreshRotationResult(RefreshRotationStatus.Rotated, user.Id, user, Issued(next, nextToken), current.IsPersistent);
    }

    public async Task<bool> IsFamilyActiveAsync(string familyId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(familyId))
        {
            return false;
        }

        var now = UtcNow();
        if (activeFamilies.TryGetValue(familyId, out var checkedAt) && now >= checkedAt && now - checkedAt < FamilyCheckWindow)
        {
            return true;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var active = await context.RefreshSession.AnyAsync(x => x.FamilyId == familyId && x.RevokedAtUtc == null && x.ExpiresAtUtc > now, cancellationToken);
        if (!active)
        {
            activeFamilies.TryRemove(familyId, out _);
            return false;
        }

        if (activeFamilies.Count >= FamilyCacheLimit)
        {
            // 過期的快取不會自己消失：數量太多時整個清掉重來（只是多查幾次資料庫）。
            activeFamilies.Clear();
        }

        activeFamilies[familyId] = now;
        return true;
    }

    public async Task<int?> RevokeFamilyAsync(string token, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var hash = HashToken(token);
        var session = await context.RefreshSession.AsNoTracking()
            .Where(x => x.TokenHash == hash)
            .Select(x => new { x.FamilyId, x.MyUserId })
            .FirstOrDefaultAsync(cancellationToken);
        if (session is null)
        {
            return null;
        }

        await RevokeFamilyAsync(context, session.FamilyId, UtcNow(), reason, cancellationToken);
        activeFamilies.TryRemove(session.FamilyId, out _);
        return session.MyUserId;
    }

    public async Task<(int? UserId, string? ClientKind)> RevokeLoginAsync(string token, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(token))
        {
            return (null, null);
        }

        string? loginSessionId;
        int userId;
        string clientKind;
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var hash = HashToken(token);
            var session = await context.RefreshSession.AsNoTracking()
                .Where(x => x.TokenHash == hash)
                .Select(x => new { x.MyUserId, x.LoginSessionId, x.ClientKind })
                .FirstOrDefaultAsync(cancellationToken);
            if (session is null)
            {
                return (null, null);
            }

            loginSessionId = session.LoginSessionId;
            userId = session.MyUserId;
            clientKind = session.ClientKind;
        }

        if (!string.IsNullOrEmpty(loginSessionId))
        {
            await RevokeByLoginSessionAsync(loginSessionId, reason, cancellationToken);
        }
        else
        {
            await RevokeFamilyAsync(token, reason, cancellationToken);
        }

        return (userId, clientKind);
    }

    public async Task<int> RevokeByLoginSessionAsync(string loginSessionId, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(loginSessionId))
        {
            return 0;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = UtcNow();
        var families = await context.RefreshSession.AsNoTracking()
            .Where(x => x.LoginSessionId == loginSessionId && x.RevokedAtUtc == null)
            .Select(x => x.FamilyId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var rows = await context.RefreshSession
            .Where(x => x.LoginSessionId == loginSessionId && x.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, now).SetProperty(x => x.RevokedReason, reason), cancellationToken);
        foreach (var family in families)
        {
            activeFamilies.TryRemove(family, out _);
        }

        if (rows > 0)
        {
            logger.LogInformation("Refresh tokens revoked for a web login. Rows={Rows}, Reason={Reason}", rows, reason);
        }

        return rows;
    }

    public async Task<int> PurgeExpiredAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.RefreshSession.Where(x => x.ExpiresAtUtc < cutoffUtc).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<RetentionStats> GetRetentionStatsAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var total = await context.RefreshSession.CountAsync(cancellationToken);
        var oldest = await context.RefreshSession.MinAsync(x => (DateTime?)x.CreatedAtUtc, cancellationToken);
        var expired = await context.RefreshSession.CountAsync(x => x.ExpiresAtUtc < cutoffUtc, cancellationToken);
        return new RetentionStats(total, oldest, expired);
    }

    /// <summary>已被換掉的 Token 又被用：寬限期內只回「已被取代」，超過就是重用、整串撤銷。</summary>
    private async Task<RefreshRotationResult> ReplacedTokenUsedAsync(BackendDBContext context, RefreshSession session, DateTime now, CancellationToken cancellationToken)
    {
        if (session.RevokedAtUtc is { } replacedAt && now - replacedAt <= ReuseGracePeriod)
        {
            logger.LogInformation("A just-rotated refresh token was used again within the grace period. UserId={UserId}", session.MyUserId);
            return new RefreshRotationResult(RefreshRotationStatus.Superseded, session.MyUserId);
        }

        await RevokeFamilyAsync(context, session.FamilyId, now, "Reused", cancellationToken);
        activeFamilies.TryRemove(session.FamilyId, out _);

        // 同一次網頁登入的其他串（新分頁 fork 出來的）也一起撤銷（0.10.8 獨立審查）：否則偷到刷新 Token 的人先 fork 一串自己的，
        // 之後即使被偵測到重用，也只撤銷被偵測到的那一串，偷開的那串照常可用。
        if (!string.IsNullOrEmpty(session.LoginSessionId))
        {
            var siblings = await context.RefreshSession.AsNoTracking()
                .Where(x => x.LoginSessionId == session.LoginSessionId && x.RevokedAtUtc == null)
                .Select(x => x.FamilyId)
                .Distinct()
                .ToListAsync(cancellationToken);
            await context.RefreshSession
                .Where(x => x.LoginSessionId == session.LoginSessionId && x.RevokedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, now).SetProperty(x => x.RevokedReason, "Reused"), cancellationToken);
            foreach (var family in siblings)
            {
                activeFamilies.TryRemove(family, out _);
            }
        }

        logger.LogWarning("A rotated refresh token was used again; the whole login was revoked. UserId={UserId}", session.MyUserId);
        return new RefreshRotationResult(RefreshRotationStatus.Reused, session.MyUserId);
    }

    private static Task<int> RevokeFamilyAsync(BackendDBContext context, string familyId, DateTime now, string reason, CancellationToken cancellationToken)
        => context.RefreshSession
            .Where(x => x.FamilyId == familyId && x.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, now).SetProperty(x => x.RevokedReason, reason), cancellationToken);

    private (RefreshSession Session, string Token) NewSession(int userId, string familyId, string stamp, bool isPersistent, string clientKind, string? loginSessionId, string? deviceLabel, TimeSpan lifetime, DateTime? absoluteExpiresAtUtc)
    {
        var now = UtcNow();
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenByteLength));
        var session = new RefreshSession
        {
            MyUserId = userId,
            FamilyId = familyId,
            TokenHash = HashToken(token),
            SecurityStamp = stamp,
            IsPersistent = isPersistent,
            ClientKind = clientKind,
            LoginSessionId = loginSessionId,
            DeviceLabel = deviceLabel is { Length: > DeviceLabelMaxLength } ? deviceLabel[..DeviceLabelMaxLength] : deviceLabel,
            CreatedAtUtc = now,
            LastUsedAtUtc = now,
            ExpiresAtUtc = absoluteExpiresAtUtc is { } cap && cap < now + lifetime ? cap : now + lifetime,
            AbsoluteExpiresAtUtc = absoluteExpiresAtUtc,
        };
        return (session, token);
    }

    private static IssuedRefreshToken Issued(RefreshSession session, string token) => new(token, session.ExpiresAtUtc, session.FamilyId, session.ClientKind);

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    private static string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
