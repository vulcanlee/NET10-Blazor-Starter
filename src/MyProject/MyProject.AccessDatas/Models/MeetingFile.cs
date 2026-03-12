using System.ComponentModel.DataAnnotations;

namespace MyProject.AccessDatas.Models;

public class MeetingFile
{
    public int Id { get; set; }

    [Required]
    public int MeetingId { get; set; }

    [Required]
    public string OriginalFileName { get; set; } = string.Empty;

    [Required]
    public string StoredFileName { get; set; } = string.Empty;

    [Required]
    public string RelativePath { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/octet-stream";

    public long FileSize { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public Meeting? Meeting { get; set; }
}
