using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;

namespace MyProject.Business.Services.DataAccess;

/// <summary>「寄信紀錄」頁的查詢條件（0.9.167 起）；時間是本地時間，查詢時轉成 UTC。</summary>
public sealed record EmailLogQuery(
    DateTime? FromLocal = null,
    DateTime? ToLocal = null,
    string? Status = null,
    string? Kind = null,
    string? Keyword = null,
    int PageIndex = 1,
    int PageSize = 20);

/// <summary>清單的一列（不含內文：內文加密且寄出或放棄後就清除）。時間已轉成本地時間。</summary>
public sealed record EmailLogItem(
    int Id,
    DateTime CreatedAt,
    string Kind,
    string To,
    string Subject,
    string Status,
    int Attempts,
    DateTime? NextAttemptAt,
    DateTime? SentAt,
    string? LastError);

/// <summary>
/// 寄信紀錄的查詢與清理（0.9.167 起）。資料表 <see cref="EmailOutbox"/> 同時是寄信佇列，
/// 所以清理一律跳過「待寄」：還沒寄出的信不可因為保留期限被刪掉。
/// </summary>
public class EmailOutboxQueryService
{
    private const int PurgeBatchSize = 1000;

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<EmailOutboxQueryService> logger;

    public EmailOutboxQueryService(IDbContextFactory<BackendDBContext> contextFactory, TimeProvider timeProvider, ILogger<EmailOutboxQueryService> logger)
    {
        this.contextFactory = contextFactory;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task<(IReadOnlyList<EmailLogItem> Items, int Total)> GetPageAsync(EmailLogQuery query)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var source = context.EmailOutbox.AsNoTracking();
        if (query.FromLocal is { } from)
        {
            var fromUtc = ToUtc(from);
            source = source.Where(x => x.CreatedAtUtc >= fromUtc);
        }

        if (query.ToLocal is { } to)
        {
            var toUtc = ToUtc(to);
            source = source.Where(x => x.CreatedAtUtc <= toUtc);
        }

        if (string.IsNullOrWhiteSpace(query.Status) == false)
        {
            source = source.Where(x => x.Status == query.Status);
        }

        if (string.IsNullOrWhiteSpace(query.Kind) == false)
        {
            source = source.Where(x => x.Kind == query.Kind);
        }

        if (string.IsNullOrWhiteSpace(query.Keyword) == false)
        {
            var keyword = query.Keyword.Trim();
            source = source.Where(x => x.To.Contains(keyword) || x.Subject.Contains(keyword));
        }

        var total = await source.CountAsync();
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var pageIndex = Math.Max(query.PageIndex, 1);
        var rows = await source
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id)
            .Skip((pageIndex - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new { x.Id, x.CreatedAtUtc, x.Kind, x.To, x.Subject, x.Status, x.Attempts, x.NextAttemptAtUtc, x.SentAtUtc, x.LastError })
            .ToListAsync();

        logger.LogDebug("Email log page loaded. Total={Total}, PageIndex={PageIndex}", total, pageIndex);
        return (rows.Select(x => new EmailLogItem(
            x.Id, ToLocal(x.CreatedAtUtc), x.Kind, x.To, x.Subject, x.Status, x.Attempts,
            x.NextAttemptAtUtc is { } next ? ToLocal(next) : null,
            x.SentAtUtc is { } sent ? ToLocal(sent) : null,
            x.LastError)).ToList(), total);
    }

    /// <summary>筆數、最舊一筆（建立時間，UTC）與門檻前會清掉的筆數（不含待寄）；門檻是 null（不自動清理）時 Expired 為 null。</summary>
    public async Task<RetentionStats> GetRetentionStatsAsync(DateTime? cutoffUtc)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var total = await context.EmailOutbox.CountAsync();
        var oldest = await context.EmailOutbox.MinAsync(x => (DateTime?)x.CreatedAtUtc);
        int? expired = cutoffUtc is { } cutoff ? await context.EmailOutbox.CountAsync(Expired(cutoff)) : null;
        return new RetentionStats(total, oldest, expired);
    }

    /// <summary>刪除建立時間早於門檻、而且不是「待寄」的紀錄，分批進行，批次之間可取消。</summary>
    public async Task<int> PurgeBeforeAsync(DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        var total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var ids = await context.EmailOutbox.Where(Expired(cutoffUtc))
                .OrderBy(x => x.Id).Select(x => x.Id).Take(PurgeBatchSize)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0)
            {
                break;
            }

            total += await context.EmailOutbox.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        logger.LogInformation("Email log purged. Rows={Rows}", total);
        return total;
    }

    // 清理條件：清理與「資料保留與清理」頁的「下次會清掉幾筆」共用。
    private static Expression<Func<EmailOutbox, bool>> Expired(DateTime cutoffUtc)
        => x => x.CreatedAtUtc < cutoffUtc && x.Status != EmailOutboxStatus.Pending;

    private DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeProvider.LocalTimeZone);

    private DateTime ToUtc(DateTime local) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), timeProvider.LocalTimeZone);
}
