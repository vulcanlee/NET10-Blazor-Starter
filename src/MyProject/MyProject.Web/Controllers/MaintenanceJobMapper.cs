using MyProject.Dtos.Admins;
using MyProject.Web.Scheduling;

namespace MyProject.Web.Controllers;

/// <summary>
/// 系統備份、資料保留頁的排程作業狀態轉成 DTO（0.10.6 起，WASM 遷移 M7）。
/// ⚠️ 本地時間一律以不帶時差送出（<see cref="DateTimeKind.Unspecified"/>），否則 WASM 會換成瀏覽器的時區。
/// </summary>
internal static class MaintenanceJobMapper
{
    public static MaintenanceJobDto? ToDto(ScheduledJobOverviewItem? job, ScheduledJobOverviewService overviewService) => job is null ? null : new MaintenanceJobDto
    {
        Name = job.Name,
        DisplayName = job.DisplayName,
        IsEnabled = job.IsEnabled,
        NextRunLocal = Unzoned(job.NextRunLocal),
        IsQueued = job.IsQueued,
        LastRun = job.LastRun is { } run ? new MaintenanceJobRunDto
        {
            Status = run.Status,
            StartedAtLocal = Unzoned(overviewService.ToLocal(run.StartedAtUtc)),
            Message = run.Message,
        } : null,
    };

    public static DateTime Unzoned(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

    public static DateTime? Unzoned(DateTime? value) => value is { } v ? Unzoned(v) : null;
}
