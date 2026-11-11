using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace MyProject.Web.Auth;

/// <summary>Google 回呼已確認的身分，等前端以一次性代碼兌換。</summary>
/// <param name="RequiresSecondFactor">帳號已啟用兩步驟驗證：兌換時回待驗票證（記住過的裝置除外），不直接發 Token。</param>
/// <param name="BindingHash">發起 Google 登入的瀏覽器所產生驗證值的 SHA-256（Base64Url）；兌換時要出示原值。</param>
public sealed record GoogleLoginGrant(int UserId, string SecurityStamp, string ReturnUrl, bool RequiresSecondFactor, string BindingHash);

/// <summary>
/// Google 登入的一次性代碼（0.10.8 起，WASM 遷移 M9）：回呼不再發 Cookie，而是產生代碼、導回前端 <c>/Auths/Login#code=…</c>，
/// 前端以 <c>POST api/v1/Auth/google/exchange</c> 換 Token。
/// <list type="bullet">
/// <item>只存代碼的雜湊、60 秒、只能兌換一次（兌換時先移除再檢查，出示錯的驗證值也算用掉）。</item>
/// <item>綁定發起的瀏覽器：前端產生隨機驗證值存在 sessionStorage，只把雜湊帶給 <c>/Auths/Google/Login</c>（放進 OAuth 的 state），
/// 兌換時出示原值 —— 代碼在網址片段裡外流（瀏覽器歷史、側錄）也沒有用。</item>
/// <item>只在這個行程的記憶體：回呼與兌換要落在同一台主機（IIS 單站台即是；多台主機要黏著工作階段）。</item>
/// </list>
/// </summary>
public sealed class GoogleLoginCodes
{
    /// <summary>回呼導回的前端頁面（代碼放在網址片段，不會送到伺服器、不進日誌）。</summary>
    public const string FrontendPath = "/Auths/Login";

    /// <summary>AuthenticationProperties.Items 裡放瀏覽器綁定雜湊的鍵。</summary>
    public const string BindingItemKey = "MyProject.GoogleBinding";

    internal static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private const int CodeByteLength = 32;
    private const int MaxPending = 10_000;

    private readonly TimeProvider timeProvider;
    private readonly ILogger<GoogleLoginCodes> logger;
    private readonly ConcurrentDictionary<string, (GoogleLoginGrant Grant, DateTimeOffset ExpiresAt)> pending = new(StringComparer.Ordinal);

    public GoogleLoginCodes(TimeProvider timeProvider, ILogger<GoogleLoginCodes> logger)
    {
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>瀏覽器綁定值的格式：SHA-256 的 Base64Url（43 字）。不符合時視為沒有帶（回登入頁重新開始）。</summary>
    public static bool IsValidBindingHash(string? value)
        => value is { Length: 43 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>驗證值的雜湊（前端用同一個演算法：SHA-256 → Base64Url）。</summary>
    public static string HashVerifier(string verifier) => Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));

    public string Issue(GoogleLoginGrant grant)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var expired in pending.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToList())
        {
            pending.TryRemove(expired, out _);
        }

        if (pending.Count >= MaxPending)
        {
            pending.Clear();
        }

        var code = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(CodeByteLength));
        pending[Hash(code)] = (grant, now.Add(Lifetime));
        return code;
    }

    /// <summary>兌換（不論成敗代碼都作廢）；不存在、過期、瀏覽器驗證值不符一律回 null。</summary>
    public GoogleLoginGrant? Redeem(string? code, string? verifier)
    {
        if (string.IsNullOrWhiteSpace(code) || !pending.TryRemove(Hash(code), out var entry))
        {
            logger.LogInformation("Google login code rejected because it is unknown or already used.");
            return null;
        }

        if (entry.ExpiresAt <= timeProvider.GetUtcNow())
        {
            logger.LogInformation("Google login code rejected because it expired. UserId={UserId}", entry.Grant.UserId);
            return null;
        }

        if (string.IsNullOrEmpty(verifier)
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(HashVerifier(verifier)), Encoding.ASCII.GetBytes(entry.Grant.BindingHash)))
        {
            logger.LogWarning("Google login code rejected because it was presented by a different browser. UserId={UserId}", entry.Grant.UserId);
            return null;
        }

        return entry.Grant;
    }

    private static string Hash(string code) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}
