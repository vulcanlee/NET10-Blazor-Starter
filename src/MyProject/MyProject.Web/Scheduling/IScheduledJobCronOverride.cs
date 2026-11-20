namespace MyProject.Web.Scheduling;

/// <summary>
/// 由程式（而不是設定檔）決定作業的 cron（0.10.39 起）。例：系統巡檢報告的日報／週報／月報時間在「系統參數」頁設定，
/// 由 <c>SystemReportScheduleProvider</c> 換算成 cron。
///
/// 排程器每分鐘重讀一次，回傳的 cron 改變時從現在重新排（不追溯）；管理頁顯示的執行時間也用它。
/// 註冊為 singleton；<see cref="TryGetCron"/> 要很便宜、不可以丟例外、也不可以碰資料庫。
/// 被這裡接手的作業不可以再用 <c>ScheduledJobSettings:Jobs</c> 覆寫（驗證器擋下），避免兩個地方都能改時間。
/// </summary>
public interface IScheduledJobCronOverride
{
    /// <summary>這個提供者接手的作業名稱。</summary>
    IReadOnlyCollection<string> JobNames { get; }

    /// <summary>取得作業的 cron；不是這個提供者接手的作業回 false。</summary>
    bool TryGetCron(string jobName, out string cron);
}
