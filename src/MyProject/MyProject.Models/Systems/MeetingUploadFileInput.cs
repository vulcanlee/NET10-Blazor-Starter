namespace MyProject.Models.Systems;

public class MeetingUploadFileInput
{
    public string FileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = "application/octet-stream";

    public long FileSize { get; set; }

    public Stream Content { get; set; } = Stream.Null;
}
