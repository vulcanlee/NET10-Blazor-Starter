namespace MyProject.AccessDatas.Models;

/// <summary>
/// 稽核雜湊鏈的鏈頭（0.9.175 起）：每條鏈一列，記下「目前應該從第幾筆到第幾筆」，整列以 <see cref="Mac"/> 簽章。
///
/// - 起點（<see cref="StartSeq"/>、<see cref="StartPrevHash"/>）：清除舊紀錄時跟著往前推，驗證才分得出「正常清除」和「有人偷刪最舊的幾筆」。
/// - 終點（<see cref="LastSeq"/>、<see cref="LastHash"/>）：下一筆從這裡接，刪掉最新幾筆會接不上。
/// - 只由 AuditChainStore 讀寫（速查表 §6.31）。
/// </summary>
public class AuditChainHead
{
    /// <summary>鏈別（主鍵）：0＝異動與安全事件，1＝瀏覽類。</summary>
    public int Chain { get; set; }
    /// <summary>目前保留的第一筆序號。</summary>
    public long StartSeq { get; set; }
    /// <summary>第一筆的 PrevHash 應有的值（全新的鏈為 64 個 0；清除後為被刪掉的最後一筆的雜湊）。</summary>
    public string StartPrevHash { get; set; } = string.Empty;
    /// <summary>最後一筆的序號（全新的鏈為 0）。</summary>
    public long LastSeq { get; set; }
    /// <summary>最後一筆的雜湊（全新的鏈為 64 個 0）。</summary>
    public string LastHash { get; set; } = string.Empty;
    /// <summary>目前金鑰的代號。</summary>
    public string KeyId { get; set; } = string.Empty;
    /// <summary>
    /// 從這個序號起都由目前金鑰簽章。更早的紀錄若是別的金鑰簽的，只能歸類為「金鑰不同」；
    /// 這個序號之後的紀錄 KeyId 不對就是偽造。
    /// </summary>
    public long KeySinceSeq { get; set; }
    /// <summary>建立鏈頭時升級前紀錄的最大 Id；之後出現沒有簽章、Id 又比它大的紀錄，就是繞過寫入服務塞進來的。</summary>
    public int LegacyMaxId { get; set; }
    /// <summary>建立時間（UTC）。</summary>
    public DateTime CreatedAt { get; set; }
    /// <summary>鏈頭遺失後重建的時間（UTC）；正常為 null。</summary>
    public DateTime? RebuiltAt { get; set; }
    /// <summary>以上欄位的 HMAC-SHA256。</summary>
    public string Mac { get; set; } = string.Empty;
}
