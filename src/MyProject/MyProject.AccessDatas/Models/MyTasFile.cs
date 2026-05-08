using System.ComponentModel.DataAnnotations;

namespace MyProject.AccessDatas.Models;

public class MyTasFile
{
    public int Id { get; set; }

    [Required]
    public int MyTasId { get; set; }

    [Required]
    public string OriginalFileName { get; set; } = string.Empty;

    [Required]
    public string StoredFileName { get; set; } = string.Empty;

    [Required]
    public string RelativePath { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/octet-stream";

    public long FileSize { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public MyTask? MyTas { get; set; }
}
