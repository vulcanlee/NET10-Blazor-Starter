using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;

namespace MyProject.Web.Auth;

/// <summary>密碼（或 Google）已通過、等待第二步的網頁登入（待驗票證的內容）。</summary>
public sealed record TwoFactorTicket(string Id, int UserId, string SecurityStamp, bool RememberMe, string ReturnUrl, string? Provider);

/// <summary>
/// 網頁以 Token 登入時的兩步驟驗證（0.10.8 起，WASM 遷移 M9；取代 Server 版登入頁的待驗 Cookie 與記住裝置 Cookie），都以 Data Protection 加密，
/// 到期時間寫在加密內容裡、以注入的 <see cref="TimeProvider"/> 判斷（見 <see cref="ExpiringProtector"/>）：
/// <list type="bullet">
/// <item><b>待驗票證</b>：密碼對了、還沒輸入驗證碼的那 5 分鐘（使用者 Id、工作階段版本、記住我、返回網址、登入方式）。驗證成功一次就作廢；
/// 驗證碼錯了票證仍可用（錯誤次數算進登入失敗、達門檻鎖定），與 Server 版的待驗 Cookie 相同。</item>
/// <item><b>記住裝置 Token</b>：使用者 Id＋工作階段版本，<see cref="TwoFactorSettings.RememberDeviceDays"/> 天；⚠️ 版本換掉（改密碼、強制登出、重設兩步驟驗證）就失效。</item>
/// </list>
/// 「已用過」只記在這個行程的記憶體（與下載票證、Google 一次性代碼相同）：多台主機時同一張票證最多在每台各用一次，
/// 但每次都還要通過驗證碼（驗證碼本身同一個時間步只能用一次）。
/// </summary>
public sealed class TwoFactorLoginTickets
{
    internal static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(5);

    private readonly ExpiringProtector pendingProtector;
    private readonly ExpiringProtector deviceProtector;
    private readonly IOptionsMonitor<TwoFactorSettings> options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<TwoFactorLoginTickets> logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> consumed = new(StringComparer.Ordinal);

    public TwoFactorLoginTickets(IDataProtectionProvider dataProtectionProvider, IOptionsMonitor<TwoFactorSettings> options, TimeProvider timeProvider, ILogger<TwoFactorLoginTickets> logger)
    {
        pendingProtector = new ExpiringProtector(dataProtectionProvider.CreateProtector("MyProject.TwoFactorTicket.v1"), timeProvider);
        deviceProtector = new ExpiringProtector(dataProtectionProvider.CreateProtector("MyProject.TwoFactorDeviceToken.v1"), timeProvider);
        this.options = options;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public int RememberDeviceDays => options.CurrentValue.RememberDeviceDays;

    public string Issue(int userId, string securityStamp, bool rememberMe, string returnUrl, string? provider)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var payload = string.Join('\n', id, userId, securityStamp, rememberMe, provider ?? string.Empty, returnUrl);
        return pendingProtector.Protect(payload, timeProvider.GetUtcNow().Add(PendingLifetime));
    }

    /// <summary>讀出票證（不作廢）；格式錯、過期、已用過一律回 null。</summary>
    public TwoFactorTicket? Read(string? ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket) || pendingProtector.Unprotect(ticket) is not { } payload)
        {
            return null;
        }

        var parts = payload.Split('\n', 6);
        if (parts.Length != 6 || !int.TryParse(parts[1], out var userId) || !bool.TryParse(parts[3], out var rememberMe))
        {
            return null;
        }

        PurgeExpired();
        return consumed.ContainsKey(parts[0])
            ? null
            : new TwoFactorTicket(parts[0], userId, parts[2], rememberMe, parts[5], parts[4].Length == 0 ? null : parts[4]);
    }

    /// <summary>作廢；已經被別的請求作廢時回 false。</summary>
    public bool TryConsume(TwoFactorTicket ticket)
    {
        var accepted = consumed.TryAdd(ticket.Id, timeProvider.GetUtcNow().Add(PendingLifetime));
        if (!accepted)
        {
            logger.LogWarning("Two-factor ticket rejected because it was already used. UserId={UserId}", ticket.UserId);
        }

        return accepted;
    }

    /// <summary>「記住這台裝置」的 Token；設定為 0 天（不提供）時回 null。</summary>
    public string? IssueDeviceToken(int userId, string securityStamp)
    {
        var days = RememberDeviceDays;
        return days <= 0 ? null : deviceProtector.Protect($"{userId}\n{securityStamp}", timeProvider.GetUtcNow().AddDays(days));
    }

    /// <summary>這台裝置在期限內被這個人記住過，而且他的工作階段版本沒換過。</summary>
    public bool IsDeviceRemembered(string? token, int userId, string currentSecurityStamp)
    {
        if (RememberDeviceDays <= 0 || string.IsNullOrWhiteSpace(token) || deviceProtector.Unprotect(token) is not { } payload)
        {
            return false;
        }

        var parts = payload.Split('\n');
        return parts.Length == 2 && int.TryParse(parts[0], out var rememberedUserId) && rememberedUserId == userId
            && SecurityStamps.Matches(parts[1], currentSecurityStamp);
    }

    private void PurgeExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var expired in consumed.Where(x => x.Value < now).Select(x => x.Key).ToList())
        {
            consumed.TryRemove(expired, out _);
        }
    }

}

/// <summary>
/// Data Protection 加密＋到期時間（0.10.8 起）：到期時間寫在加密內容的第一行，以注入的 <see cref="TimeProvider"/> 判斷
/// （<c>ITimeLimitedDataProtector</c> 用系統時鐘，測試沒辦法推進時間）。格式錯、被竄改、過期一律回 null。
/// </summary>
internal sealed class ExpiringProtector
{
    private readonly IDataProtector protector;
    private readonly TimeProvider timeProvider;

    public ExpiringProtector(IDataProtector protector, TimeProvider timeProvider)
    {
        this.protector = protector;
        this.timeProvider = timeProvider;
    }

    public string Protect(string payload, DateTimeOffset expiresAt) => protector.Protect($"{expiresAt.ToUnixTimeMilliseconds()}\n{payload}");

    public string? Unprotect(string value)
    {
        string plain;
        try
        {
            plain = protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            return null;
        }

        var separator = plain.IndexOf('\n');
        return separator > 0 && long.TryParse(plain[..separator], out var expires) && timeProvider.GetUtcNow().ToUnixTimeMilliseconds() < expires
            ? plain[(separator + 1)..]
            : null;
    }
}

/// <summary>
/// 兩步驟驗證的設定票證（0.10.8 起）：開始設定時產生的金鑰在確認驗證碼之前不存進資料庫（與 Server 版只放在元件記憶體相同），
/// 改以 Data Protection 加密、綁定使用者、15 分鐘有效交給前端，啟用時帶回來。成功啟用一次就作廢。
/// </summary>
public sealed class TwoFactorSetupTickets
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private readonly ExpiringProtector protector;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<TwoFactorSetupTickets> logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> consumed = new(StringComparer.Ordinal);

    public TwoFactorSetupTickets(IDataProtectionProvider dataProtectionProvider, TimeProvider timeProvider, ILogger<TwoFactorSetupTickets> logger)
    {
        protector = new ExpiringProtector(dataProtectionProvider.CreateProtector("MyProject.TwoFactorSetup.v1"), timeProvider);
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public string Issue(int userId, string secret)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        return protector.Protect($"{id}\n{userId}\n{secret}", timeProvider.GetUtcNow().Add(Lifetime));
    }

    /// <summary>票證屬於這個人、未過期、未用過時回 (Id, 金鑰)；否則 null。</summary>
    public (string Id, string Secret)? Read(string? ticket, int userId)
    {
        if (string.IsNullOrWhiteSpace(ticket))
        {
            return null;
        }

        if (protector.Unprotect(ticket) is not { } payload)
        {
            logger.LogInformation("Two-factor setup ticket rejected because it is invalid or expired. UserId={UserId}", userId);
            return null;
        }

        var parts = payload.Split('\n');
        if (parts.Length != 3 || !int.TryParse(parts[1], out var owner) || owner != userId || consumed.ContainsKey(parts[0]))
        {
            return null;
        }

        return (parts[0], parts[2]);
    }

    public bool TryConsume(string id)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var expired in consumed.Where(x => x.Value < now).Select(x => x.Key).ToList())
        {
            consumed.TryRemove(expired, out _);
        }

        return consumed.TryAdd(id, now.Add(Lifetime));
    }
}
