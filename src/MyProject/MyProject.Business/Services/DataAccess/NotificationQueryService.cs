using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas;
using MyProject.Models.AdapterModel;

namespace MyProject.Business.Services.DataAccess;

/// <summary>
/// 鈴鐺的查詢與標為已讀（0.9.100 起）。
/// ⚠️ <c>userId</c> 一律由呼叫端從 <c>CurrentUserService</c> 取得，每一條 UPDATE 都帶 <c>RecipientUserId = userId</c>：
/// 不可以讓人把別人的通知標為已讀。
/// </summary>
public class NotificationQueryService
{
    private const int PurgeBatchSize = 1000;

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<NotificationQueryService> logger;

    public NotificationQueryService(IDbContextFactory<BackendDBContext> contextFactory, TimeProvider timeProvider, ILogger<NotificationQueryService> logger)
    {
        this.contextFactory = contextFactory;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<int> GetUnreadCountAsync(int userId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.Notification.CountAsync(x => x.RecipientUserId == userId && x.ReadAtUtc == null);
    }

    /// <summary>最新的幾則（新到舊）。</summary>
    public async Task<List<NotificationAdapterModel>> GetLatestAsync(int userId, int take = 10)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.Notification
            .AsNoTracking()
            .Where(x => x.RecipientUserId == userId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Id)
            .Take(take)
            .Select(x => new NotificationAdapterModel
            {
                Id = x.Id,
                Category = x.Category,
                Title = x.Title,
                Body = x.Body,
                Link = x.Link,
                CreatedAtUtc = x.CreatedAtUtc,
                ReadAtUtc = x.ReadAtUtc,
            })
            .ToListAsync();
    }

    public async Task MarkReadAsync(int userId, int notificationId)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        await using var context = await contextFactory.CreateDbContextAsync();
        await context.Notification
            .Where(x => x.Id == notificationId && x.RecipientUserId == userId && x.ReadAtUtc == null)
            .ExecuteUpdateAsync(x => x.SetProperty(n => n.ReadAtUtc, nowUtc));
    }

    public async Task<int> MarkAllReadAsync(int userId)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.Notification
            .Where(x => x.RecipientUserId == userId && x.ReadAtUtc == null)
            .ExecuteUpdateAsync(x => x.SetProperty(n => n.ReadAtUtc, nowUtc));
    }

    /// <summary>刪除建立時間早於門檻的通知（不論已讀未讀），分批進行，批次之間可取消。</summary>
    public async Task<int> PurgeBeforeAsync(DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        var total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var ids = await context.Notification
                .Where(x => x.CreatedAtUtc < cutoffUtc)
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .Take(PurgeBatchSize)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0)
            {
                break;
            }

            total += await context.Notification.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        logger.LogInformation("Notifications purged. Rows={Rows}", total);
        return total;
    }
}
