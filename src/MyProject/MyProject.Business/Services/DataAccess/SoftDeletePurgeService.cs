using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;

namespace MyProject.Business.Services.DataAccess;

/// <summary>被永久刪除的一筆資料（稽核用）。</summary>
public sealed record PurgedRecord(int Id, string Name);

/// <summary>一種資料這次清除的結果。</summary>
public sealed class SoftDeletePurgeTypeResult(string entityType)
{
    /// <summary><c>Project</c>／<c>Category</c>／<c>Team</c>／<c>MyUser</c>／<c>RoleView</c>。</summary>
    public string EntityType { get; } = entityType;

    public List<PurgedRecord> Removed { get; } = [];

    /// <summary>候選之後被還原、已被手動永久刪除、或刪除時版本號不符。</summary>
    public int Skipped { get; set; }

    /// <summary>角色仍被使用者（含尚未到期的已刪除使用者）當作主要角色，留到下次。</summary>
    public int InUse { get; set; }

    /// <summary>support 帳號與「預設角色」永遠不自動刪除。</summary>
    public int Protected { get; set; }

    /// <summary>資料列已刪除，但附件實體檔沒刪掉（孤兒檔）。</summary>
    public int FileDeleteFailures { get; set; }

    /// <summary>發生未預期的錯誤而中止這種資料（已刪除的仍列在 <see cref="Removed"/>）。</summary>
    public bool Failed { get; set; }
}

/// <summary>整次清除的結果。</summary>
public sealed class SoftDeletePurgeResult
{
    public List<SoftDeletePurgeTypeResult> Types { get; } = [];

    /// <summary>收到取消（網站關閉）而在筆與筆之間停下；已刪除的仍列在結果裡。</summary>
    public bool Cancelled { get; set; }

    public bool AnyFailed => Types.Any(x => x.Failed);
}

/// <summary>
/// 依保留天數永久刪除已軟刪除的資料（0.9.97 起，由排程作業「已刪除資料清理」呼叫）。
///
/// ⚠️ 這是<b>系統層級</b>的清除，刻意<b>不套用團隊範圍</b>：背景作業沒有登入者，各服務的 <c>PurgeAsync</c>
/// 會把有團隊的專案全部擋下。速查表「刪除、還原、永久刪除都要檢查團隊範圍」對它是明文的例外。
///
/// 設計上的幾個重點（都有測試釘住）：
/// <list type="bullet">
/// <item>順序：專案 → 分類 → 團隊 → <b>使用者 → 角色</b>。使用者先刪，同一輪中它們引用的角色才刪得掉。</item>
/// <item><b>每一筆用新的 DbContext</b>：共用 context 時，一筆存檔失敗的實體會留在追蹤裡，之後每一筆都跟著失敗。</item>
/// <item>追蹤中 <c>Remove</c>：DELETE 帶著版本號，候選之後被還原 → 版本號不符 → 略過。⚠️ 不可改用 <c>ExecuteDelete</c>
/// （它不比對版本號，會刪掉剛被還原的資料；也會套用軟刪除過濾器而刪 0 筆）。</item>
/// <item>專案的附件實體檔在<b>提交成功後</b>才經 <see cref="ProjectFileStore"/> 刪除。</item>
/// <item>support 帳號與「預設角色」不刪：若它們被其他途徑軟刪除，啟動時的種子資料會還原它們。</item>
/// <item>取消時<b>不丟例外</b>：停在筆與筆之間並回傳部分結果，讓作業先把已刪除的寫進稽核。</item>
/// </list>
/// </summary>
public class SoftDeletePurgeService
{
    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly ProjectFileStore fileStore;
    private readonly BootstrapSettings bootstrapSettings;
    private readonly ILogger<SoftDeletePurgeService> logger;

    public SoftDeletePurgeService(
        IDbContextFactory<BackendDBContext> contextFactory,
        ProjectFileStore fileStore,
        IOptions<BootstrapSettings> bootstrapOptions,
        ILogger<SoftDeletePurgeService> logger)
    {
        this.contextFactory = contextFactory;
        this.fileStore = fileStore;
        bootstrapSettings = bootstrapOptions.Value;
        this.logger = logger;
    }

    /// <summary>永久刪除 <c>DeletedAt</c> 早於 <paramref name="cutoffLocal"/>（本地時間）的已刪除資料。</summary>
    public async Task<SoftDeletePurgeResult> PurgeExpiredAsync(DateTime cutoffLocal, CancellationToken cancellationToken)
    {
        var result = new SoftDeletePurgeResult();

        await PurgeTypeAsync<Project>(result, nameof(Project), cutoffLocal, x => x.Id, x => x.Title,
            isProtected: null, isInUse: null, include: q => q.Include(x => x.Files),
            attachmentsOf: x => x.Files.Select(f => f.RelativePath).ToList(), order: null, cancellationToken);

        await PurgeTypeAsync<Category>(result, nameof(Category), cutoffLocal, x => x.Id, x => x.Name,
            isProtected: null, isInUse: null, include: null, attachmentsOf: null, order: null, cancellationToken);

        // 部門樹（0.9.105 起）：仍被當作上層（含已刪除的下屬）就不能刪（外鍵 Restrict）；先刪最深層，同一次就能把整串清掉。
        await PurgeTypeAsync<Team>(result, nameof(Team), cutoffLocal, x => x.Id, x => x.Name,
            isProtected: null, isInUse: IsTeamParentOfAnyAsync, include: null, attachmentsOf: null, order: DeepestTeamsFirstAsync, cancellationToken);

        await PurgeTypeAsync<MyUser>(result, nameof(MyUser), cutoffLocal, x => x.Id, x => x.Account,
            isProtected: x => string.Equals(x.Account, bootstrapSettings.SupportAccount, StringComparison.OrdinalIgnoreCase),
            isInUse: null, include: null, attachmentsOf: null, order: null, cancellationToken);

        await PurgeTypeAsync<RoleView>(result, nameof(RoleView), cutoffLocal, x => x.Id, x => x.Name,
            isProtected: x => x.Name == MagicObjectHelper.預設角色,
            isInUse: IsRoleInUseAsync, include: null, attachmentsOf: null, order: null, cancellationToken);

        return result;
    }

    private async Task PurgeTypeAsync<TEntity>(
        SoftDeletePurgeResult overall,
        string entityType,
        DateTime cutoffLocal,
        Func<TEntity, int> idOf,
        Func<TEntity, string> nameOf,
        Func<TEntity, bool>? isProtected,
        Func<int, Task<bool>>? isInUse,
        Func<IQueryable<TEntity>, IQueryable<TEntity>>? include,
        Func<TEntity, List<string>>? attachmentsOf,
        Func<List<TEntity>, Task<List<TEntity>>>? order,
        CancellationToken cancellationToken)
        where TEntity : class, ISoftDeletable
    {
        // 結果物件建在 try 之前：中途失敗時，已經刪掉的那些仍要回報給作業寫稽核。
        var result = new SoftDeletePurgeTypeResult(entityType);
        overall.Types.Add(result);
        if (overall.Cancelled)
        {
            return;
        }

        try
        {
            List<TEntity> candidates;
            await using (var context = await contextFactory.CreateDbContextAsync(CancellationToken.None))
            {
                candidates = await context.Set<TEntity>()
                    .IgnoreQueryFilters([ISoftDeletable.FilterName])
                    .AsNoTracking()
                    .Where(x => x.IsDeleted && x.DeletedAt != null && x.DeletedAt < cutoffLocal)
                    .ToListAsync(CancellationToken.None);
            }

            foreach (var candidate in order is null ? candidates.OrderBy(idOf).ToList() : await order(candidates))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    overall.Cancelled = true;
                    break;
                }

                var id = idOf(candidate);
                var name = nameOf(candidate);
                if (isProtected?.Invoke(candidate) == true)
                {
                    result.Protected++;
                    logger.LogWarning("Protected soft-deleted record was not purged. EntityType={EntityType}, RecordId={RecordId}", entityType, id);
                    continue;
                }

                if (isInUse is not null && await isInUse(id))
                {
                    result.InUse++;
                    continue;
                }

                // 每一筆用新的 context：一筆存檔失敗的實體不會拖累後面的筆。
                await using var context = await contextFactory.CreateDbContextAsync(CancellationToken.None);
                IQueryable<TEntity> query = context.Set<TEntity>().IgnoreQueryFilters([ISoftDeletable.FilterName]);
                if (include is not null)
                {
                    query = include(query);
                }

                // 重新確認完整條件：候選之後被還原、又被重新刪除（DeletedAt 變新）、或已被手動永久刪除。
                var tracked = await query
                    .Where(x => EF.Property<int>(x, "Id") == id && x.IsDeleted && x.DeletedAt != null && x.DeletedAt < cutoffLocal)
                    .FirstOrDefaultAsync(CancellationToken.None);
                if (tracked is null)
                {
                    result.Skipped++;
                    continue;
                }

                var attachments = attachmentsOf?.Invoke(tracked) ?? [];
                context.Remove(tracked);
                try
                {
                    await context.SaveChangesAsync(CancellationToken.None);
                }
                catch (DbUpdateConcurrencyException)
                {
                    // 讀出之後被還原（還原會換新版本號）：留著，不刪。
                    result.Skipped++;
                    logger.LogInformation("Soft-deleted record changed before purge; skipped. EntityType={EntityType}, RecordId={RecordId}", entityType, id);
                    continue;
                }
                catch (DbUpdateException) when (isInUse is not null)
                {
                    // 角色在檢查之後、刪除之前被指派給某人：外鍵 Restrict 擋下。留到下次，不算失敗。
                    if (await isInUse(id))
                    {
                        result.InUse++;
                        continue;
                    }

                    throw;
                }

                result.Removed.Add(new PurgedRecord(id, name));

                // 提交成功後才刪實體檔；刪不掉只是孤兒檔，記數量不算失敗。
                foreach (var relativePath in attachments)
                {
                    if (!fileStore.Delete(relativePath))
                    {
                        result.FileDeleteFailures++;
                    }
                }
            }

            logger.LogInformation(
                "Soft-deleted records purged. EntityType={EntityType}, Rows={Rows}, Skipped={Skipped}, InUse={InUse}, Protected={Protected}",
                entityType, result.Removed.Count, result.Skipped, result.InUse, result.Protected);
        }
        catch (Exception ex)
        {
            // 第一個未預期的錯誤就中止這種資料，只記一次（例如資料庫鎖住時不要一筆一筆洗版）；其他種類照常處理。
            result.Failed = true;
            logger.LogError(ex, "Failed to purge soft-deleted records. EntityType={EntityType}, RowsBeforeFailure={Rows}", entityType, result.Removed.Count);
        }
    }

    private async Task<bool> IsTeamParentOfAnyAsync(int teamId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await TeamHierarchy.IsParentOfAnyAsync(context, teamId);
    }

    /// <summary>依在整棵樹（含已刪除）裡的深度由深到淺；同深度依 Id。</summary>
    private async Task<List<Team>> DeepestTeamsFirstAsync(List<Team> candidates)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var parents = await context.Team.IgnoreQueryFilters([ISoftDeletable.FilterName]).AsNoTracking()
            .ToDictionaryAsync(x => x.Id, x => x.ParentId);
        int DepthOf(int id)
        {
            var depth = 0;
            var visited = new HashSet<int>();
            for (int? current = parents.GetValueOrDefault(id); current is { } parent && visited.Add(parent) && depth < TeamTree.MaxDepth; current = parents.GetValueOrDefault(parent))
            {
                depth++;
            }

            return depth;
        }

        return candidates.OrderByDescending(x => DepthOf(x.Id)).ThenBy(x => x.Id).ToList();
    }

    /// <summary>任何使用者（含已刪除、尚未到期的）仍以這個角色為主要角色。外鍵 Restrict 連已刪除的使用者都算。</summary>
    private async Task<bool> IsRoleInUseAsync(int roleId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        return await context.MyUser.IgnoreQueryFilters([ISoftDeletable.FilterName]).AnyAsync(x => x.RoleViewId == roleId);
    }
}
