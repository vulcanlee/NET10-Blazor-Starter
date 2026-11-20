namespace MyProject.AccessDatas.Models;

/// <summary>
/// 容量快照（0.10.39 起）：每天記一次各磁碟、資料庫與資料目錄的用量，給系統巡檢報告算成長速度與「預估幾天後用完」。
///
/// 一天一個 <see cref="Key"/> 只有一列（同一天重記會覆蓋）。<see cref="Key"/> 是固定的代碼
/// （磁碟根目錄如 <c>C:\</c>、設定名稱如 <c>ProjectFilePath</c>、<c>Database.Main</c>），不放使用者輸入的內容。
/// 時間為本地時間；自動過期依 <see cref="SnapshotDate"/>。
/// </summary>
public class CapacitySnapshot
{
    public int Id { get; set; }

    /// <summary>快照日期（本地時間的日期，時間部分為 0）。</summary>
    public DateTime SnapshotDate { get; set; }

    /// <summary>實際量測時間（本地時間）。</summary>
    public DateTime CapturedAt { get; set; }

    /// <summary>種類：Volume（磁碟）／Folder（資料目錄）／Database（資料庫檔）。</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>識別代碼，見類別說明。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>已用位元組（磁碟為已用空間；目錄與資料庫為大小）。</summary>
    public long? UsedBytes { get; set; }

    /// <summary>總容量（只有磁碟有）。</summary>
    public long? TotalBytes { get; set; }

    /// <summary>可用空間（只有磁碟有）。</summary>
    public long? FreeBytes { get; set; }

    /// <summary>檔案數（只有目錄有）。</summary>
    public long? ItemCount { get; set; }

    /// <summary>量測狀態：Ok／Partial（逾時，數字是下限）／Missing（目錄不存在）／Unavailable（讀不到）。</summary>
    public string Status { get; set; } = string.Empty;
}
