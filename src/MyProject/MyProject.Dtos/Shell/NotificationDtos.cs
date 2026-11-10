namespace MyProject.Dtos.Shell;

/// <summary>通知鈴鐺（<c>GET /api/v1/notifications/summary</c>）：未讀數、最新幾則、有沒有已讀的可清除。</summary>
public class NotificationSummaryDto
{
    public int UnreadCount { get; set; }

    public bool HasRead { get; set; }

    public List<NotificationItemDto> Latest { get; set; } = [];
}

public class NotificationItemDto
{
    public int Id { get; set; }

    public string Category { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Body { get; set; }

    /// <summary>站內連結；伺服器端已過濾成安全的相對網址，不安全時為 null。</summary>
    public string? Link { get; set; }

    public DateTime CreatedAtLocal { get; set; }

    public bool IsRead { get; set; }
}

/// <summary>公告橫幅（<c>GET /api/v1/announcements/active</c>）要顯示的一則公告。</summary>
public class ActiveAnnouncementDto
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;
}
