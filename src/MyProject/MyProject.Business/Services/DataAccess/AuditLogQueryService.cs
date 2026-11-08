using System.Linq.Expressions;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Factories;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Business.Services.Other.AuditIntegrity;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;

namespace MyProject.Business.Services.DataAccess;

/// <summary>
/// 稽核紀錄的讀取端。
///
/// 寫入端是另一支 <c>AuditLogService</c>（<c>Services/Other</c>）。兩者都注入 <see cref="IDbContextFactory{TContext}"/>、
/// 每個方法用完即棄 —— Blazor 的 DI scope 等同整條 SignalR circuit，共用 scoped DbContext 會累積追蹤實體。
///
/// 詳細內容檔（0.9.131 起）的刪除也在這裡：清除與清空都要連同 <see cref="AuditContentFileStore"/> 的檔案一起處理。
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
    private readonly AuditContentFileStore contentFileStore;
    private readonly AuditChainStore chainStore;

    public IMapper Mapper { get; }
    public ILogger<AuditLogQueryService> Logger { get; }

    public AuditLogQueryService(
        IDbContextFactory<BackendDBContext> contextFactory,
        IMapper mapper,
        ILogger<AuditLogQueryService> logger,
        AuditContentFileStore contentFileStore,
        AuditChainStore chainStore)
    {
        this.contextFactory = contextFactory;
        Mapper = mapper;
        Logger = logger;
        this.contentFileStore = contentFileStore;
        this.chainStore = chainStore;
    }

    /// <summary>用編號讀一筆（0.9.159 起，系統管理助理看明細時取動作代碼寫稽核）；不存在時回 null。</summary>
    public async Task<AuditLogAdapterModel?> GetByIdAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var item = await context.AuditLog.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return item is null ? null : Mapper.Map<AuditLogAdapterModel>(item);
    }

    /// <summary>
    /// 讀取一筆稽核的詳細內容（修改差異、查詢條件）。沒有內容檔、或檔案已不存在時回 null。
    /// </summary>
    public async Task<string?> GetContentAsync(int id)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var relativePath = await context.AuditLog
            .Where(x => x.Id == id)
            .Select(x => x.ContentFile)
            .FirstOrDefaultAsync();

        return await contentFileStore.ReadAsync(relativePath);
    }

    /// <summary>
    /// 每位使用者最後一次成功登入（本地時間，0.9.157 起，系統管理助理判斷「長期未登入」用）。
    /// 使用者資料表沒有最後登入欄位，登入只記在稽核（<c>Login.Success</c>、<c>Login.Sso.Success</c>），
    /// 所以只看得到稽核保留期間內的登入；沒有出現在結果裡的使用者＝保留期間內沒有登入紀錄。
    /// </summary>
    public async Task<Dictionary<int, DateTime>> GetLastLoginTimesAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var rows = await context.AuditLog.AsNoTracking()
            .Where(x => x.ActorUserId != null && x.Success
                && (x.Action == AuditActions.Login.Success || x.Action == AuditActions.Login.SsoSuccess))
            .GroupBy(x => x.ActorUserId!.Value)
            .Select(group => new { UserId = group.Key, Last = group.Max(x => x.OccurredAt) })
            .ToListAsync();

        return rows.ToDictionary(row => row.UserId, row => ToLocal(row.Last));
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

        if (string.IsNullOrWhiteSpace(query.ClientIp) == false)
        {
            var ip = query.ClientIp.Trim();
            dataSource = dataSource.Where(x => x.ClientIp != null && x.ClientIp.StartsWith(ip));
        }

        // 目標類型與目標識別是精確比對（0.9.173 起）：關鍵字的 Contains 查 12 會混到 112。
        if (string.IsNullOrWhiteSpace(query.TargetType) == false)
        {
            var targetType = query.TargetType.Trim();
            dataSource = dataSource.Where(x => x.TargetType == targetType);
        }

        if (string.IsNullOrWhiteSpace(query.TargetId) == false)
        {
            var targetId = query.TargetId.Trim();
            dataSource = dataSource.Where(x => x.TargetId == targetId);
        }

        if (query.ExcludeBrowsing)
        {
            dataSource = dataSource.Where(NotBrowsing);
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
                (x.Detail != null && x.Detail.Contains(query.Keyword)) ||
                (x.ClientIp != null && x.ClientIp.Contains(query.Keyword)));
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
    /// 不是瀏覽類（0.9.173 起）：<see cref="AuditActions.IsBrowsing"/> 的 SQL 版本，異動歷程與 <see cref="AuditLogQuery.ExcludeBrowsing"/> 共用。
    /// 兩份規則一致由 <c>AuditLogQueryServiceTests.NotBrowsing_ShouldAgreeWithIsBrowsing_ForEveryActionCode</c> 守門。
    /// </summary>
    private static readonly Expression<Func<AuditLog, bool>> NotBrowsing = x =>
        x.Action != AuditActions.Page.View
        && !x.Action.EndsWith(AuditActions.SearchSuffix)
        && !x.Action.EndsWith(AuditActions.ViewSuffix);

    /// <summary>篩選下拉用的目標類型清單（0.9.173 起），同樣取自資料；沒有目標的事件（例如登入）不列。</summary>
    public async Task<List<string>> GetDistinctTargetTypesAsync()
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        return await context.AuditLog
            .AsNoTracking()
            .Where(x => x.TargetType != null && x.TargetType != string.Empty)
            .Select(x => x.TargetType!)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync();
    }

    /// <summary>
    /// 某一筆資料的異動歷程（0.9.173 起，資料頁的「異動歷程」）：目標類型＋目標識別精確比對，
    /// 排除瀏覽類（換頁、搜尋、查看，見 <see cref="AuditActions.IsBrowsing"/>），新的在前；時間已轉成本地時間。
    /// </summary>
    public async Task<DataRequestResult<AuditLogAdapterModel>> GetTargetHistoryAsync(
        string targetType, string targetId, int currentPage, int pageSize)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var dataSource = context.AuditLog.AsNoTracking()
            .Where(x => x.TargetType == targetType && x.TargetId == targetId)
            .Where(NotBrowsing)
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id);

        DataRequestResult<AuditLogAdapterModel> result = new() { Count = await dataSource.CountAsync() };
        var records = await dataSource.Skip((currentPage - 1) * pageSize).Take(pageSize).ToListAsync();
        result.Result = Mapper.Map<List<AuditLogAdapterModel>>(records);
        foreach (var item in result.Result)
        {
            item.OccurredAt = ToLocal(item.OccurredAt);
        }

        return result;
    }

    /// <summary>
    /// 刪除早於 N 天前的紀錄。
    ///
    /// ⚠️ 門檻必須用 <see cref="DateTime.UtcNow"/> 算 —— 資料表存的是 UTC。
    /// 「系統例外紀錄」的同名方法用的是 <c>DateTime.Now</c>，因為那張表存本地時間；
    /// 照抄過來會讓清除範圍整整偏掉一個時區。
    /// </summary>
    public async Task<VerifyRecordResult> PurgeAsync(int days, AuditLogKind kind = AuditLogKind.All)
    {
        Logger.LogInformation("Purging audit logs older than {Days} days. Kind={Kind}", days, kind);

        try
        {
            var removed = await PurgeBeforeAsync(DateTime.UtcNow.AddDays(-days), kind);

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

    /// <summary>
    /// 刪除早於 <paramref name="thresholdUtc"/> 的稽核紀錄，回傳刪除筆數。
    /// ⚠️ 門檻必須是 UTC（資料表存 UTC）。供頁面手動清除與自動保存期限（LOG-13）共用；失敗時拋出。
    /// </summary>
    /// <summary>
    /// 清理條件：<see cref="PurgeBeforeAsync"/> 與 <see cref="GetRetentionStatsAsync"/> 共用，
    /// 「資料保留與清理」頁的「下次會清掉幾筆」才會等於實際刪掉的筆數。
    /// </summary>
    private static Expression<Func<AuditLog, bool>> OlderThan(DateTime thresholdUtc)
        => x => x.OccurredAt < thresholdUtc;

    /// <summary>瀏覽類（<see cref="NotBrowsing"/> 的反面）。</summary>
    private static readonly Expression<Func<AuditLog, bool>> Browsing = x =>
        x.Action == AuditActions.Page.View
        || x.Action.EndsWith(AuditActions.SearchSuffix)
        || x.Action.EndsWith(AuditActions.ViewSuffix);

    /// <summary>依清除範圍（0.9.174 起）篩選：瀏覽類與其他各有保留天數。0.9.175 起 AuditChainStore 清升級前的舊紀錄也用它。</summary>
    internal static IQueryable<AuditLog> OfKind(IQueryable<AuditLog> source, AuditLogKind kind) => kind switch
    {
        AuditLogKind.Browsing => source.Where(Browsing),
        AuditLogKind.Change => source.Where(NotBrowsing),
        _ => source,
    };

    /// <summary>
    /// 筆數、最舊一筆與超過 <paramref name="thresholdUtc"/>（UTC；null＝不自動清理）的筆數，只算 <paramref name="kind"/> 那一類。
    /// ⚠️ <see cref="RetentionStats.Oldest"/> 是 UTC，顯示前由呼叫端轉本地時間。
    /// </summary>
    public async Task<RetentionStats> GetRetentionStatsAsync(DateTime? thresholdUtc, AuditLogKind kind = AuditLogKind.All)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var source = OfKind(context.AuditLog, kind);
        var total = await source.CountAsync();
        var oldest = await source.MinAsync(x => (DateTime?)x.OccurredAt);
        int? expired = thresholdUtc is { } t ? await CountExpiredAsync(context, t, kind) : null;
        return new RetentionStats(total, oldest, expired);
    }

    /// <summary>和清除同一個範圍（<see cref="AuditChainStore.ResolveExpiredAsync"/>）：已簽章的紀錄只算每條鏈最舊一端會被刪的那段。</summary>
    private static async Task<int> CountExpiredAsync(BackendDBContext context, DateTime thresholdUtc, AuditLogKind kind)
    {
        var expired = await AuditChainStore.ResolveExpiredAsync(context, thresholdUtc, kind, CancellationToken.None);
        var count = 0;
        foreach (var scope in expired.Scopes)
        {
            count += await scope.CountAsync();
        }

        return count;
    }

    /// <summary>
    /// 只算 <paramref name="kind"/> 那一類早於 <paramref name="thresholdUtc"/> 的筆數（與 <see cref="PurgeBeforeAsync(DateTime, AuditLogKind)"/> 同條件）。
    /// 總筆數與最舊一筆不分類，用 <see cref="GetRetentionStatsAsync"/> 一次取得即可，不必每類各掃一次。
    /// </summary>
    public async Task<int> CountExpiredAsync(DateTime thresholdUtc, AuditLogKind kind)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await CountExpiredAsync(context, thresholdUtc, kind);
    }

    public Task<int> PurgeBeforeAsync(DateTime thresholdUtc) => PurgeBeforeAsync(thresholdUtc, AuditLogKind.All);

    /// <summary>
    /// 刪除 <paramref name="kind"/> 那一類、早於 <paramref name="thresholdUtc"/> 的紀錄與它們的內容檔，回傳筆數。
    /// ⚠️ 只清單一類時不收月份目錄：瀏覽類的門檻（90 天）收月份目錄，會連 90～365 天前的修改差異檔一起刪掉；
    /// 排程作業清完兩類後，用較舊的門檻另外呼叫 <see cref="DeleteContentMonthsBeforeAsync"/>。
    /// </summary>
    public async Task<int> PurgeBeforeAsync(DateTime thresholdUtc, AuditLogKind kind)
    {
        // 刪除與鏈頭起點在 AuditChainStore 的同一個交易裡完成（0.9.175 起）。
        // 內容檔等資料列刪成功後才刪 —— 反過來的話，資料列刪失敗會留下指向不存在檔案的紀錄。
        var (removed, files) = await chainStore.PurgeAsync(thresholdUtc, kind);

        foreach (var file in files)
        {
            contentFileStore.Delete(file);
        }

        if (kind == AuditLogKind.All)
        {
            // 收尾：空的月份目錄，以及資料列沒建起來時留下的孤兒檔。
            await DeleteContentMonthsBeforeAsync(thresholdUtc);
        }

        return removed;
    }

    /// <summary>
    /// 收掉 <paramref name="thresholdUtc"/> 所在月份之前的內容檔月份目錄（空目錄與孤兒檔）。門檻要用兩類中較舊的那個。
    ///
    /// 邊界保護（0.9.175 起）：清除為了不在鏈中間刪出缺口會少刪幾筆，時鐘被調過也會留下時間較舊的紀錄；
    /// 邊界取「門檻」與「仍有內容檔的紀錄中最早的時間」較早者，月份目錄名稱和紀錄時間是同一個值，
    /// 所以還在的紀錄不會被收掉內容檔（否則完整性檢查會報「內容檔遺失」）。
    /// </summary>
    public async Task DeleteContentMonthsBeforeAsync(DateTime thresholdUtc)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var oldestWithContent = await context.AuditLog
            .Where(x => x.ContentFile != null)
            .MinAsync(x => (DateTime?)x.OccurredAt);

        var boundary = oldestWithContent is { } oldest && oldest < thresholdUtc ? oldest : thresholdUtc;
        contentFileStore.DeleteMonthsBefore(boundary);
    }

    /// <summary>清空全部紀錄。</summary>
    public async Task<VerifyRecordResult> ClearAllAsync()
    {
        Logger.LogInformation("Clearing all audit logs.");

        try
        {
            // 兩條雜湊鏈都從下一筆重新起算（0.9.175 起），見 AuditChainStore.ClearAllAsync。
            // 只刪被清掉的紀錄的內容檔，不清整個目錄：同時可能有寫入已寫好內容檔、正在等鎖，整個清掉會讓它指向不存在的檔。
            var (removed, files) = await chainStore.ClearAllAsync();
            foreach (var file in files)
            {
                contentFileStore.Delete(file);
            }

            await DeleteContentMonthsBeforeAsync(DateTime.UtcNow);

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
