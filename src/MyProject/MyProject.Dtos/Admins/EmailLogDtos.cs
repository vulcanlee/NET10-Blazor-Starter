namespace MyProject.Dtos.Admins;

/// <summary>寄信紀錄的查詢條件（0.10.4 起，<c>POST api/v1/email-logs/search</c>）。時間是伺服器的本地時間；關鍵字比對收件者與主旨。</summary>
public class EmailLogSearchRequestDto
{
    public DateTime? FromLocal { get; set; }

    public DateTime? ToLocal { get; set; }

    /// <summary>Pending、Sent、Failed、Skipped；空白不限。</summary>
    public string? Status { get; set; }

    /// <summary>Notification、PasswordReset、PasswordChanged、ExceptionAlert、Test；空白不限。</summary>
    public string? Kind { get; set; }

    public string? Keyword { get; set; }

    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue, ErrorMessage = "頁碼必須大於或等於 1。")]
    public int PageIndex { get; set; } = 1;

    /// <summary>每頁筆數，1～200。</summary>
    [System.ComponentModel.DataAnnotations.Range(1, 200, ErrorMessage = "每頁筆數必須介於 1 到 200 之間。")]
    public int PageSize { get; set; } = 20;
}

/// <summary>寄信紀錄的一列（不含內文：內文加密，寄出或放棄後就清除）。時間已轉成伺服器的本地時間。</summary>
public class EmailLogItemDto
{
    public int Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string To { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public int Attempts { get; set; }

    public DateTime? NextAttemptAt { get; set; }

    public DateTime? SentAt { get; set; }

    public string? LastError { get; set; }
}
