using MyProject.Models.Systems;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Client.Views.Admins;

/// <summary>
/// 排程作業執行紀錄的顯示文字與色調（0.10.45 起，整併自排程作業、系統備份、資料保留三頁相同的寫法）。
/// 代碼來自伺服器（<see cref="JobRunStatuses"/>、<see cref="JobRunTriggers"/>）。
/// </summary>
internal static class JobRunText
{
    public static string Status(string status) => status switch
    {
        JobRunStatuses.Running => "執行中",
        JobRunStatuses.Succeeded => "成功",
        JobRunStatuses.Failed => "失敗",
        JobRunStatuses.Interrupted => "中斷",
        JobRunStatuses.Skipped => "略過",
        _ => status,
    };

    public static StatusTone Tone(string status) => status switch
    {
        JobRunStatuses.Succeeded => StatusTone.Positive,
        JobRunStatuses.Running => StatusTone.Accent,
        JobRunStatuses.Failed or JobRunStatuses.Interrupted => StatusTone.Warning,
        _ => StatusTone.Muted,
    };

    /// <summary>觸發方式；不認得的代碼（或沒有）回 null，各頁決定怎麼顯示（備份頁顯示「—」、排程頁顯示原代碼）。</summary>
    public static string? Trigger(string? trigger) => trigger switch
    {
        JobRunTriggers.Schedule => "排程",
        JobRunTriggers.CatchUp => "補跑",
        JobRunTriggers.Manual => "手動",
        _ => null,
    };
}
