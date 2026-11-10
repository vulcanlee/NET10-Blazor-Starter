using System.ComponentModel.DataAnnotations;

namespace MyProject.AccessDatas.Models;

/// <summary>
/// 刷新 Token 的伺服器端紀錄（0.10.0 起，WASM 遷移 M1）。一筆就是一次發出的刷新 Token。
///
/// <para>⚠️ 只存 Token 的 SHA-256（十六進位），**不存原值**：資料庫外洩時拿到的雜湊無法拿來換存取 Token。</para>
///
/// <para>同一次登入之後每次刷新都換一筆新的（輪替），新舊同屬一個 <see cref="FamilyId"/>。
/// 已被換掉的舊 Token 又被拿來用（<see cref="ReplacedById"/> 有值），代表 Token 可能外洩，整串一起撤銷。</para>
///
/// <para>刷新時比對 <see cref="SecurityStamp"/>（發出當時的工作階段版本）與使用者目前的版本，
/// 改密碼、停用、強制登出、改角色等換了版本的操作，都會讓之後的刷新失敗並撤銷整串，不必在每個地方另外撤銷。</para>
///
/// <para>過期的紀錄由 <c>RefreshSessionRetentionJob</c> 清除；撤銷但未過期的要保留，重用偵測需要它們。</para>
/// </summary>
public class RefreshSession
{
    public int Id { get; set; }

    public int MyUserId { get; set; }
    public MyUser? MyUser { get; set; }

    /// <summary>同一次登入輪替出來的一串 Token 共用的識別（Guid "N"）。</summary>
    [Required]
    [MaxLength(32)]
    public string FamilyId { get; set; } = string.Empty;

    /// <summary>Token 原值的 SHA-256（十六進位小寫）。</summary>
    [Required]
    [MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>發出當時使用者的工作階段版本。</summary>
    [Required]
    [MaxLength(64)]
    public string SecurityStamp { get; set; } = string.Empty;

    /// <summary>登入時是否勾「記住我」：前端據此決定存 localStorage（true）或 sessionStorage（false）。</summary>
    public bool IsPersistent { get; set; }

    /// <summary>發出的管道：<c>api</c>（API 登入）或 <c>web-bridge</c>（網頁以 Cookie 換 Token）。</summary>
    [Required]
    [MaxLength(20)]
    public string ClientKind { get; set; } = string.Empty;

    /// <summary>網頁登入工作階段識別（Cookie 的 <c>login_session</c>）；從網頁登出時用它撤銷這次登入換出的 Token。</summary>
    [MaxLength(32)]
    public string? LoginSessionId { get; set; }

    /// <summary>裝置說明（User-Agent 截斷），預留給日後的「我的登入裝置」清單。</summary>
    [MaxLength(200)]
    public string? DeviceLabel { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime LastUsedAtUtc { get; set; }

    /// <summary>這一筆的到期時間；每次輪替從當下重算，但不超過 <see cref="AbsoluteExpiresAtUtc"/>。</summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>
    /// 整串的最晚到期時間（可為空＝不設上限）。網頁以 Cookie 換出的 Token 取 Cookie 的到期時間：
    /// Cookie 過期（閒置逾時或「記住我」期限到）之後，WASM 端也不能再靠刷新延長登入。
    /// </summary>
    public DateTime? AbsoluteExpiresAtUtc { get; set; }

    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>撤銷原因，例如 <c>Rotated</c>、<c>Reused</c>、<c>Logout</c>、<c>SessionRevoked</c>。</summary>
    [MaxLength(40)]
    public string? RevokedReason { get; set; }

    /// <summary>輪替後接手的那一筆；有值代表這筆已被換掉，再被使用就是重用。</summary>
    public int? ReplacedById { get; set; }
}
