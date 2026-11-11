namespace MyProject.Dtos.Commons;

/// <summary>
/// 一次性下載票證（0.10.3 起）：瀏覽器以 <see cref="Url"/> 原生下載（不經 WASM 記憶體、不帶 Token）。
/// 只能用一次，<see cref="ExpiresInSeconds"/> 秒內有效；使用時伺服器會再檢查一次帳號與權限。
/// </summary>
public class DownloadTicketDto
{
    /// <summary>下載網址（相對於網站根目錄，例如 <c>/api/v1/downloads/{票證}</c>）。</summary>
    public string Url { get; set; } = string.Empty;

    public int ExpiresInSeconds { get; set; }
}
