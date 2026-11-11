using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace MyProject.Web.Downloads;

/// <summary>
/// 票證記下的內容：下載什麼（<see cref="Kind"/>＋<see cref="Target"/>）、替誰申請、申請當時的工作階段版本。
/// <see cref="Target"/> 是附件的 Id（數字字串）或備份的檔名（0.10.6 起），兌換時由各種類自己解讀與檢查。
/// </summary>
public sealed record DownloadTicket(string Kind, string Target, int UserId, string? SecurityStamp, DateTimeOffset ExpiresAt);

/// <summary>
/// 一次性下載票證（0.10.3 起，WASM 遷移 M4）。WASM 頁面只有存取 Token，瀏覽器的原生下載（連結、<c>location</c>）帶不了 Token；
/// 把大檔讀進 WASM 再存檔又會吃光記憶體。所以先以 Token 申請票證，再讓瀏覽器以 <c>GET api/v1/downloads/{票證}</c> 直接下載。
///
/// 票證是 32 位元組亂數（不可猜）、<see cref="Lifetime"/> 內有效、**取出即刪除**（只能用一次，重放一律 404）。
/// 這裡只負責發與收；兌換時由 <see cref="Controllers.DownloadsController"/> 以票證上的使用者重新載入帳號與權限，再交給各資料的服務檢查。
///
/// ⚠️ 存在記憶體裡、只在單一行程有效：IIS 的應用程式集區要維持一個工作行程（預設值）。
/// 應用程式集區回收時未用的票證會失效，使用者重新點一次即可（票證本來就只活 60 秒）。
/// </summary>
public sealed class DownloadTicketService
{
    /// <summary>專案附件（目標是附件 Id）。</summary>
    public const string ProjectFileKind = "ProjectFile";

    /// <summary>系統備份（0.10.6 起，目標是備份檔名；兌換時再確認是管理員、檔名合格且檔案在備份目錄裡）。</summary>
    public const string BackupKind = "Backup";

    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, DownloadTicket> tickets = new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider;

    public DownloadTicketService(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
    }

    /// <summary>發一張票證，回傳票證字串（網址安全的 Base64）。</summary>
    public string Issue(string kind, string target, int userId, string? securityStamp)
    {
        RemoveExpired();
        var ticket = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        tickets[ticket] = new DownloadTicket(kind, target, userId, securityStamp, timeProvider.GetUtcNow() + Lifetime);
        return ticket;
    }

    /// <summary>以數字 Id 為目標（附件）。</summary>
    public string Issue(string kind, int targetId, int userId, string? securityStamp)
        => Issue(kind, targetId.ToString(CultureInfo.InvariantCulture), userId, securityStamp);

    /// <summary>取出並作廢票證；不存在、已用過或已過期回 null。同一張票證同時兌換兩次，只有一次拿得到。</summary>
    public DownloadTicket? TryRedeem(string? ticket)
    {
        if (string.IsNullOrEmpty(ticket) || !tickets.TryRemove(ticket, out var redeemed))
        {
            return null;
        }

        return redeemed.ExpiresAt > timeProvider.GetUtcNow() ? redeemed : null;
    }

    /// <summary>申請了卻沒用的票證，在下一次發票時清掉，記憶體不會一直長。</summary>
    private void RemoveExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var (key, value) in tickets)
        {
            if (value.ExpiresAt <= now)
            {
                tickets.TryRemove(key, out _);
            }
        }
    }
}
