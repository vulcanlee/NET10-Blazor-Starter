namespace MyProject.Models.AdapterModel;

public class MeetingFileAdapterModel
{
    public int Id { get; set; }

    public int MeetingId { get; set; }

    public string OriginalFileName { get; set; } = string.Empty;

    public string StoredFileName { get; set; } = string.Empty;

    public string RelativePath { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/octet-stream";

    public long FileSize { get; set; }

    public DateTime CreatedAt { get; set; }
}
