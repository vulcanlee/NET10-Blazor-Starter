using MyProject.Business.Services.DataAccess;
using MyProject.Web.SystemReports;

namespace MyProject.Web.Scheduling.Jobs;

/// <summary>
/// 容量快照（0.10.39 起，預設每天 05:00）：記下各磁碟、資料庫與資料目錄的用量，系統巡檢報告用它算成長速度與「預估幾天後用完」。
/// 同一天重跑會覆蓋當天的快照。日報停用時快照照樣累積，之後重新開啟報告就有歷史可以預估。
/// </summary>
public sealed class CapacitySnapshotJob : IScheduledJob
{
    public const string JobName = "CapacitySnapshot";

    private readonly CapacityProbe probe;
    private readonly CapacitySnapshotService snapshotService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<CapacitySnapshotJob> logger;

    public CapacitySnapshotJob(CapacityProbe probe, CapacitySnapshotService snapshotService, TimeProvider timeProvider, ILogger<CapacitySnapshotJob> logger)
    {
        this.probe = probe;
        this.snapshotService = snapshotService;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<ScheduledJobResult> ExecuteAsync(ScheduledJobContext context, CancellationToken cancellationToken)
    {
        var result = await probe.MeasureAsync(cancellationToken);
        var now = timeProvider.GetLocalNow().DateTime;
        var rows = result.ToSnapshotRows(now.Date);
        await snapshotService.SaveDayAsync(now.Date, now, rows.ToList());
        var partial = result.Folders.Count(x => x.Status == Models.Systems.CapacitySnapshotKinds.StatusPartial);
        if (partial > 0)
        {
            logger.LogWarning("Capacity snapshot measured some folders only partially. PartialFolders={PartialFolders}", partial);
        }

        return ScheduledJobResult.Success(
            $"記錄 {result.Volumes.Count} 顆磁碟、{result.Folders.Count} 個資料目錄"
            + (partial > 0 ? $"（{partial} 個目錄量測逾時，數字是下限）" : string.Empty) + "。");
    }
}
