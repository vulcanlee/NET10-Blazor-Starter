using System;
using System.Collections.Generic;
using System.Text;

namespace MyProject.Dtos.Commons;

/// <summary>
/// 專案搜尋請求參數
/// </summary>
public class ProjectSearchRequestDto : SearchRequestBaseDto
{
    /// <summary>
    /// 擁有者
    /// </summary>
    public string? Owner { get; set; }

    /// <summary>
    /// 專案狀態
    /// </summary>
    public string? Status { get; set; }

    /// <summary>
    /// 優先順序
    /// </summary>
    public string? Priority { get; set; }

    /// <summary>
    /// 開始日期(起)
    /// </summary>
    public DateTime? StartDateFrom { get; set; }

    /// <summary>
    /// 開始日期(迄)
    /// </summary>
    public DateTime? StartDateTo { get; set; }

    /// <summary>
    /// 完成百分比(最小)
    /// </summary>
    public int? CompletionPercentageMin { get; set; }

    /// <summary>
    /// 完成百分比(最大)
    /// </summary>
    public int? CompletionPercentageMax { get; set; }

    /// <summary>分類篩選（0.10.3 起，與畫面的「分類過濾」相同：貼了任一個就算）。</summary>
    public List<string> Categories { get; set; } = [];

    /// <summary>團隊篩選（0.10.3 起，與畫面的「團隊過濾」相同：貼了任一個就算）。</summary>
    public List<string> Teams { get; set; } = [];

    /// <summary>
    /// true：查「顯示已刪除」的清單（0.10.3 起，需要刪除權限），固定依刪除時間由新到舊。
    /// </summary>
    public bool Deleted { get; set; }

    /// <summary>
    /// true：這次是使用者按下「搜尋」（0.10.3 起）。伺服器寫一筆搜尋稽核；換頁、排序、篩選、重新整理不帶，不寫。
    /// </summary>
    public bool RecordSearch { get; set; }
}
