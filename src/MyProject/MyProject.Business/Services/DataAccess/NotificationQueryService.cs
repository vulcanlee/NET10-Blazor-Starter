using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.Other;
using MyProject.Models.AdapterModel;

namespace MyProject.Business.Services.DataAccess;

/// <summary>
/// 鈴鐺的查詢、標為已讀與刪除（0.9.100 起；刪除 0.9.125 起）。
/// ⚠️ <c>userId</c> 一律由呼叫端從 <c>CurrentUserService</c> 取得，每一條 UPDATE／DELETE 都帶 <c>RecipientUserId = userId</c>：
/// 不可以讓人把別人的通知標為已讀或刪掉。
/// 0.9.122 起標為已讀與刪除（真的改到資料時）會發出這位使用者的訊號，鈴鐺、首頁「我的通知」與他的其他分頁一起更新。
/// </summary>
public class NotificationQueryService
{
    private const int PurgeBatchSize = 1000;

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly INotificationSignal signal;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<NotificationQueryService> logger;

    public NotificationQueryService(IDbContextFactory<BackendDBContext> contextFactory, INotificationSignal signal, TimeProvider timeProvider, ILogger<NotificationQueryService> logger)
    {
        this.contextFactory = contextFactory;
        this.signal = signal;
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
        var changed = await context.Notification
            .Where(x => x.Id == notificationId && x.RecipientUserId == userId && x.ReadAtUtc == null)
            .ExecuteUpdateAsync(x => x.SetProperty(n => n.ReadAtUtc, nowUtc));
        if (changed > 0)
        {
            signal.Publish([userId]);
        }
    }

    public async Task<int> MarkAllReadAsync(int userId)
    {
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        await using var context = await contextFactory.CreateDbContextAsync();
        var changed = await context.Notification
            .Where(x => x.RecipientUserId == userId && x.ReadAtUtc == null)
            .ExecuteUpdateAsync(x => x.SetProperty(n => n.ReadAtUtc, nowUtc));
        if (changed > 0)
        {
            signal.Publish([userId]);
        }

        return changed;
    }

    /// <summary>刪除一則自己的通知（0.9.125 起）；不是自己的或已不存在時不動，回傳 false。</summary>
    public async Task<bool> DeleteAsync(int userId, int notificationId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var changed = await context.Notification
            .Where(x => x.Id == notificationId && x.RecipientUserId == userId)
            .ExecuteDeleteAsync();
        if (changed > 0)
        {
            signal.Publish([userId]);
        }

        return changed > 0;
    }

    /// <summary>刪除自己所有已讀的通知（0.9.125 起），未讀的不動。</summary>
    public async Task<int> DeleteReadAsync(int userId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var changed = await context.Notification
            .Where(x => x.RecipientUserId == userId && x.ReadAtUtc != null)
            .ExecuteDeleteAsync();
        if (changed > 0)
        {
            signal.Publish([userId]);
        }

        return changed;
    }

    /// <summary>刪除建立時間早於門檻的通知（不論已讀未讀），分批進行，批次之間可取消。</summary>
    public Task<int> PurgeBeforeAsync(DateTime cutoffUtc, CancellationToken cancellationToken)
        => PurgeAsync(x => x.CreatedAtUtc < cutoffUtc, cancellationToken);

    /// <summary>刪除標為已讀的時間早於門檻的通知（0.9.125 起），分批進行，批次之間可取消。</summary>
    public Task<int> PurgeReadBeforeAsync(DateTime readCutoffUtc, CancellationToken cancellationToken)
        => PurgeAsync(x => x.ReadAtUtc != null && x.ReadAtUtc < readCutoffUtc, cancellationToken);

    private async Task<int> PurgeAsync(Expression<Func<Notification, bool>> predicate, CancellationToken cancellationToken)
    {
        var total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var ids = await context.Notification
                .Where(predicate)
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
