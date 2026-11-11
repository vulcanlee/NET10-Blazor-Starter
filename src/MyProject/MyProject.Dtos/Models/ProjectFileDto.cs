namespace MyProject.Dtos.Models;

/// <summary>專案附件（0.10.3 起，<c>GET api/v1/Project/{id}</c> 的 <c>Files</c>）。下載要先申請一次性票證（<c>POST files/{id}/ticket</c>）。</summary>
public class ProjectFileDto
{
    public int Id { get; set; }

    /// <summary>上傳時的檔名（下載時的檔名）。</summary>
    public string OriginalFileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>大小（位元組）。</summary>
    public long FileSize { get; set; }

    public DateTime CreatedAt { get; set; }
}
