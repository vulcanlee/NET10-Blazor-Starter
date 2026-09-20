namespace MyProject.Models.AdapterModel;

/// <summary>
/// 稽核紀錄的畫面繫結模型。本頁唯讀，因此不需要 ICloneable
/// （Clone() 是為了「編輯前隔離，避免雙向繫結污染來源資料」而存在，這裡沒有編輯）。
/// </summary>
public class AuditLogAdapterModel
{
    public int Id { get; set; }

    /// <summary>
    /// 事件發生時間，<b>已由查詢服務轉為本地時間</b>。
    ///
    /// ⚠️ 資料表存的是 UTC（與帳號鎖定的 LockoutEndUtc 一致），
    /// 但畫面與匯出一律顯示本地時間，轉換統一在 AuditLogQueryService 做完，
    /// 因此拿到這個模型之後<b>不可以再轉一次</b>。
    /// </summary>
    public DateTime OccurredAt { get; set; }

    public int? ActorUserId { get; set; }

    public string? ActorAccount { get; set; }

    /// <summary>動作代碼，例如 Login.Success、User.Create、Permission.Denied。</summary>
    public string Action { get; set; } = string.Empty;

    public string? TargetType { get; set; }

    public string? TargetId { get; set; }

    public string? Detail { get; set; }

    public bool Success { get; set; }

    /// <summary>動作代碼的第一節（例如 Login.Success → Login），用於清單的分類標籤。</summary>
    public string ActionCategory
    {
        get
        {
            var index = Action.IndexOf('.');
            return index > 0 ? Action[..index] : Action;
        }
    }
}
