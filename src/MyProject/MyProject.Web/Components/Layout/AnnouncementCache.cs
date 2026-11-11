using MyProject.Business.Services.DataAccess;

namespace MyProject.Web.Components.Layout;

/// <summary>
/// 公告橫幅用的快取（singleton，0.9.100 起）：所有連線共用一份「還沒結束的公告」，每次換頁不必查資料庫。
/// 管理頁存檔時 <see cref="Invalidate"/>；另一個行程（IIS 重疊回收）的修改最晚 60 秒後重新讀取。
/// 是否在顯示期間內、是否為對象，在回給使用者時以 <see cref="Select"/> 判斷（快取裡也有尚未開始的公告）。
/// </summary>
public sealed class AnnouncementCache
{
    internal static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AnnouncementCache> logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private IReadOnlyList<ActiveAnnouncement> items = [];
    // 以 tick 存放，跨執行緒讀寫是原子的（DateTimeOffset 是 16 位元組，會讀到一半）。
    private long expiresAtTicks;

    public AnnouncementCache(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, ILogger<AnnouncementCache> logger)
    {
        this.scopeFactory = scopeFactory;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<IReadOnlyList<ActiveAnnouncement>> GetAsync()
    {
        if (timeProvider.GetUtcNow().UtcTicks < Volatile.Read(ref expiresAtTicks))
        {
            return items;
        }

        await gate.WaitAsync();
        try
        {
            if (timeProvider.GetUtcNow().UtcTicks >= Volatile.Read(ref expiresAtTicks))
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                items = await scope.ServiceProvider.GetRequiredService<AnnouncementService>().GetCurrentAndUpcomingAsync();
                Volatile.Write(ref expiresAtTicks, (timeProvider.GetUtcNow() + TimeToLive).UtcTicks);
            }
        }
        catch (Exception ex)
        {
            // 讀不到就沿用舊的一份，下一次再試；公告不顯示不該讓整個版面壞掉。
            logger.LogError(ex, "Failed to load announcements; keeping the previous list.");
            Volatile.Write(ref expiresAtTicks, (timeProvider.GetUtcNow() + TimeToLive).UtcTicks);
        }
        finally
        {
            gate.Release();
        }

        return items;
    }

    public void Invalidate() => Volatile.Write(ref expiresAtTicks, 0);

    /// <summary>從公告清單挑出這個人要看到的（時間、對象、沒被關閉），新的在前（0.10.9 起從已刪除的 Server 版公告橫幅搬來）。</summary>
    internal static List<ActiveAnnouncement> Select(
        IEnumerable<ActiveAnnouncement> all, DateTime nowUtc, IReadOnlyCollection<int> roleIds, IReadOnlyCollection<string> teamNames, IReadOnlySet<int> dismissed)
        => all.Where(x => x.IsShowingAt(nowUtc) && x.IsFor(roleIds, teamNames) && !dismissed.Contains(x.Id))
            .OrderByDescending(x => x.StartAtUtc)
            .ToList();
}
