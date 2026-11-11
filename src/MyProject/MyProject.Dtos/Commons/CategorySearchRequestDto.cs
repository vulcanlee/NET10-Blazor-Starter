namespace MyProject.Dtos.Commons;

/// <summary>
/// 分類搜尋請求參數
/// </summary>
public class CategorySearchRequestDto : SearchRequestBaseDto
{
    /// <summary>
    /// 是否啟用篩選 (null 表示不篩選)
    /// </summary>
    public bool? IsEnabled { get; set; }

    /// <summary>
    /// true：查「顯示已刪除」的清單（0.10.2 起，需要刪除權限的畫面才會帶），固定依刪除時間由新到舊。
    /// </summary>
    public bool Deleted { get; set; }

    /// <summary>
    /// true：這次是使用者按下「搜尋」（0.10.2 起）。伺服器寫一筆搜尋稽核；換頁、排序、重新整理不帶，不寫。
    /// </summary>
    public bool RecordSearch { get; set; }
}
