using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Factories;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;

namespace MyProject.Business.Services.DataAccess;

/// <summary>
/// 稽核紀錄的讀取端。
///
/// 寫入端是另一支 <c>AuditLogService</c>（<c>Services/Other</c>）：它被各業務流程夾帶呼叫，
/// 注入的是 scoped <c>BackendDBContext</c>。本服務只服務 Blazor 畫面，
/// 因此改注入 <see cref="IDbContextFactory{TContext}"/>、每個方法用完即棄
/// —— Blazor 的 DI scope 等同整條 SignalR circuit，共用 scoped DbContext 會累積追蹤實體。
///
/// ⚠️ <b>時區是本服務唯一的職責重點。</b>資料表的 <c>OccurredAt</c> 存的是 UTC
/// （與帳號鎖定的 <c>LockoutEndUtc</c> 一致），而畫面的 DatePicker 與顯示格式都是本地時間。
/// 換算<b>全部在這裡做完</b>：查詢條件本地→UTC、回傳結果 UTC→本地。呼叫端不要再轉第二次。
/// 這與「系統例外紀錄」不同 —— 那張表存的是 <c>DateTime.Now</c>（本地），所以它整條路徑都不換算。
/// </summary>
public class AuditLogQueryService
{
    /// <summary>
    /// 匯出時的取用上限。匯出的是「目前查詢條件下的全部資料」而非當頁，
    /// 但仍需要一個上限，否則稽核表長到數十萬列時會把整個 circuit 拖垮。
    /// </summary>
    public const int MaxExportRows = 10000;

    private readonly IDbContextFactory<BackendDBContext> contextFactory;

    public IMapper Mapper { get; }
    public ILogger<AuditLogQueryService> Logger { get; }

    public AuditLogQueryService(
        IDbContextFactory<BackendDBContext> contextFactory,
        IMapper mapper,
        ILogger<AuditLogQueryService> logger)
    {
        this.contextFactory = contextFactory;
        Mapper = mapper;
        Logger = logger;
    }

    /// <summary>本地時間 → UTC。DatePicker 給的 Kind 多半是 Unspecified，先釘成 Local 再換算。</summary>
    public static DateTime ToUtc(DateTime local)
        => DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();

    /// <summary>UTC → 本地時間。SQLite 讀回來的 Kind 是 Unspecified，先釘成 Utc 再換算。</summary>
    public static DateTime ToLocal(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();

    public async Task<DataRequestResult<AuditLogAdapterModel>> GetAsync(AuditLogQuery query)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogDebug(
            "Loading audit logs. Action={Action}, CurrentPage={CurrentPage}, PageSize={PageSize}",
            query.Action,
            query.CurrentPage,
            query.PageSize);

        DataRequestResult<AuditLogAdapterModel> result = new();
        IQueryable<AuditLog> dataSource = context.AuditLog.AsNoTracking();

        // 條件端的時間是本地時間，資料表存的是 UTC —— 比對前一定要先換算，
        // 否則查詢區間會整整偏掉一個時區（台灣是 8 小時）。
        if (query.StartTime.HasValue)
        {
            var start = ToUtc(query.StartTime.Value);
            dataSource = dataSource.Where(x => x.OccurredAt >= start);
        }

        if (query.EndTime.HasValue)
        {
            var end = ToUtc(query.EndTime.Value);
            dataSource = dataSource.Where(x => x.OccurredAt <= end);
        }

        if (string.IsNullOrWhiteSpace(query.Account) == false)
        {
            dataSource = dataSource.Where(x => x.ActorAccount != null && x.ActorAccount.Contains(query.Account));
        }

        if (string.IsNullOrWhiteSpace(query.Action) == false)
        {
            dataSource = dataSource.Where(x => x.Action == query.Action);
        }

        if (query.Success.HasValue)
        {
            var success = query.Success.Value;
            dataSource = dataSource.Where(x => x.Success == success);
        }

        if (string.IsNullOrWhiteSpace(query.Keyword) == false)
        {
            dataSource = dataSource.Where(x =>
                x.Action.Contains(query.Keyword) ||
                (x.ActorAccount != null && x.ActorAccount.Contains(query.Keyword)) ||
                (x.TargetType != null && x.TargetType.Contains(query.Keyword)) ||
                (x.TargetId != null && x.TargetId.Contains(query.Keyword)) ||
                (x.Detail != null && x.Detail.Contains(query.Keyword)));
        }

        IOrderedQueryable<AuditLog>? sorted = null;

        if (query.SortField == nameof(AuditLogAdapterModel.OccurredAt))
        {
            sorted = query.SortDescending == true
                ? dataSource.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
                : query.SortDescending == false
                    ? dataSource.OrderBy(x => x.OccurredAt).ThenBy(x => x.Id)
                    : null;
        }

        // Skip/Take 之前一定要有 OrderBy，否則 SQLite 不保證回傳順序，分頁會重複或漏資料。
        // 未指定欄位、欄位不認得、方向為 null —— 三種情況一律退回預設排序。
        dataSource = sorted ?? dataSource.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id);

        result.Count = await dataSource.CountAsync();
        dataSource = dataSource
            .Skip((query.CurrentPage - 1) * query.PageSize)
            .Take(query.PageSize);

        List<AuditLog> records = await dataSource.ToListAsync();
        result.Result = Mapper.Map<List<AuditLogAdapterModel>>(records);

        // UTC → 本地，統一在這裡做完。AutoMapper 只會原封不動搬過去，不會幫忙換算。
        foreach (var item in result.Result)
        {
            item.OccurredAt = ToLocal(item.OccurredAt);
        }

        return result;
    }

    /// <summary>
    /// 篩選下拉用的動作代碼清單，直接取自資料。
    ///
    /// 刻意不寫死清單：動作代碼目前散落在 15 個呼叫點的字串字面值裡，沒有集中的常數來源，
    /// 任何手寫清單都會在下一次有人新增稽核事件時默默過期。
    /// </summary>
    public async Task<List<string>> GetDistinctActionsAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        return await context.AuditLog
            .AsNoTracking()
            .Select(x => x.Action)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync();
    }

    /// <summary>
    /// 刪除早於 N 天前的紀錄。
    ///
    /// ⚠️ 門檻必須用 <see cref="DateTime.UtcNow"/> 算 —— 資料表存的是 UTC。
    /// 「系統例外紀錄」的同名方法用的是 <c>DateTime.Now</c>，因為那張表存本地時間；
    /// 照抄過來會讓清除範圍整整偏掉一個時區。
    /// </summary>
    public async Task<VerifyRecordResult> PurgeAsync(int days)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var threshold = DateTime.UtcNow.AddDays(-days);
        Logger.LogInformation("Purging audit logs older than {Days} days.", days);

        try
        {
            var removed = await context.AuditLog
                .Where(x => x.OccurredAt < threshold)
                .ExecuteDeleteAsync();

            if (removed == 0)
            {
                return VerifyRecordResultFactory.Build(true, "沒有符合條件的紀錄。");
            }

            Logger.LogInformation("Purged audit logs. Rows={Rows}", removed);
            return VerifyRecordResultFactory.Build(true, $"已清除 {removed} 筆紀錄。");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to purge audit logs.");
            return VerifyRecordResultFactory.Build(false, "清除稽核紀錄失敗。", ex);
        }
    }

    /// <summary>清空全部紀錄。</summary>
    public async Task<VerifyRecordResult> ClearAllAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Logger.LogInformation("Clearing all audit logs.");

        try
        {
            var removed = await context.AuditLog.ExecuteDeleteAsync();

            Logger.LogInformation("Cleared all audit logs. Rows={Rows}", removed);
            return VerifyRecordResultFactory.Build(true, $"已清空 {removed} 筆紀錄。");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to clear audit logs.");
            return VerifyRecordResultFactory.Build(false, "清空稽核紀錄失敗。", ex);
        }
    }
}
