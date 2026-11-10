using System.Buffers.Text;
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

    /// <summary>網頁已用 Cookie 登入，WASM 頁面以 Cookie 換 Token（WASM 遷移過渡期）。</summary>
    public const string WebBridge = "web-bridge";
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
public sealed record IssuedRefreshToken(string Token, DateTime ExpiresAtUtc);

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

    /// <summary>撤銷這個 Token 所屬的整串（登出）；回傳擁有者，Token 不認得時回 null。</summary>
    Task<int?> RevokeFamilyAsync(string token, string reason, CancellationToken cancellationToken = default);

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

    private const int TokenByteLength = 32;
    private const int DeviceLabelMaxLength = 200;

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<RefreshTokenStore> logger;

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
        return new IssuedRefreshToken(token, session.ExpiresAtUtc);
    }

    public async Task<RefreshRotationResult> RotateAsync(string token, TimeSpan lifetime, CancellationToken cancellationToken = default)
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
            return new RefreshRotationResult(RefreshRotationStatus.UserInactive, session.MyUserId);
        }

        if (!SecurityStamps.Matches(session.SecurityStamp, user.SecurityStamp))
        {
            await RevokeFamilyAsync(context, session.FamilyId, now, "SessionRevoked", cancellationToken);
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

        var (next, nextToken) = NewSession(user.Id, session.FamilyId, user.SecurityStamp, session.IsPersistent, session.ClientKind, session.LoginSessionId, session.DeviceLabel, lifetime, session.AbsoluteExpiresAtUtc);
        context.RefreshSession.Add(next);
        await context.SaveChangesAsync(cancellationToken);
        await context.RefreshSession
            .Where(x => x.Id == session.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReplacedById, next.Id), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new RefreshRotationResult(RefreshRotationStatus.Rotated, user.Id, user, new IssuedRefreshToken(nextToken, next.ExpiresAtUtc), session.IsPersistent);
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
        return session.MyUserId;
    }

    public async Task<int> RevokeByLoginSessionAsync(string loginSessionId, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(loginSessionId))
        {
            return 0;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = UtcNow();
        var rows = await context.RefreshSession
            .Where(x => x.LoginSessionId == loginSessionId && x.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAtUtc, now).SetProperty(x => x.RevokedReason, reason), cancellationToken);
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

    private DateTime UtcNow() => timeProvider.GetUtcNow().UtcDateTime;

    private static string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
