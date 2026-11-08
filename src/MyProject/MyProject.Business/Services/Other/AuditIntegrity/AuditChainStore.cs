using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.DataAccess;
using MyProject.Models.Systems;

namespace MyProject.Business.Services.Other.AuditIntegrity;

/// <summary>
/// 稽核紀錄雜湊鏈的寫入端（0.9.175 起）：<b>全專案唯一可以新增或刪除 <see cref="AuditLog"/> 的地方</b>
/// （速查表 §6.31，<c>AuditIntegrityConventionTests</c> 守門）。
///
/// 每次新增、清除、清空都在同一個 <c>BEGIN IMMEDIATE</c> 交易裡讀鏈頭 → 改資料 → 更新鏈頭並重新簽章，
/// 所以兩個行程（IIS 重疊回收）同時寫也不會拿到同一個序號。前面再加一道 static 的
/// <see cref="SemaphoreSlim"/>，讓同一行程內的寫入排隊，不必靠 SQLite 的忙碌重試。
///
/// <b>不把竄改洗成正常</b>：鏈頭簽章不對、或金鑰代號被改（而金鑰其實沒換）時，鏈頭標成 <see cref="TamperedMac"/>，
/// 之後的寫入照常進行但不再替它簽章；清除也不推進這條鏈的起點。只有「清空全部」會重新起算。
///
/// 註冊為 singleton、只依賴 singleton：換頁稽核是射後不理，circuit 結束後寫入仍要能完成。
/// </summary>
public sealed class AuditChainStore
{
    /// <summary>鏈頭不可信任時寫入的標記：之後不再替它重新簽章，以免把竄改洗成正常。</summary>
    public const string TamperedMac = "tampered";

    /// <summary>等鎖的上限：清除改成分批後每批都很短，等超過這個時間代表有東西卡住，寧可這一筆稽核失敗（記 Error）也不要卡住業務操作。</summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly IAuditIntegrityKeyStore keyStore;
    private readonly AuditChainCheckpointStore checkpointStore;
    private readonly ILogger<AuditChainStore> logger;

    public AuditChainStore(
        IDbContextFactory<BackendDBContext> contextFactory,
        IAuditIntegrityKeyStore keyStore,
        AuditChainCheckpointStore checkpointStore,
        ILogger<AuditChainStore> logger)
    {
        this.contextFactory = contextFactory;
        this.keyStore = keyStore;
        this.checkpointStore = checkpointStore;
        this.logger = logger;
    }

    /// <summary>清除每批的筆數：每批一個交易、批與批之間放開鎖，大量清除時業務操作的稽核寫入不會被卡住。</summary>
    public int PurgeBatchSize { get; init; } = 5000;

    /// <summary>簽章並新增一筆。<paramref name="entry"/> 的鏈欄位由這裡填；失敗時拋出（由呼叫端決定要不要吞）。</summary>
    public async Task AppendAsync(AuditLog entry, CancellationToken cancellationToken = default)
    {
        var key = keyStore.GetKey();
        var chain = AuditChains.Of(entry.Action);
        entry.Chain = chain;

        await LockedAsync(async context =>
        {
            var (heads, trustedHeads) = await PrepareHeadsAsync(context, key, cancellationToken);
            var head = heads[chain];
            var trusted = trustedHeads[chain];

            // 鏈頭的終點被改小（或被換回舊的）時，照它的序號寫會撞唯一索引、稽核整個停擺：從實際最後一筆接續，並標記鏈頭不可信。
            var last = await context.AuditLog.Where(x => x.Chain == chain && x.Seq != null).OrderByDescending(x => x.Seq)
                .Select(x => new { Seq = x.Seq!.Value, x.Hash }).FirstOrDefaultAsync(cancellationToken);
            if (last is not null && last.Seq > head.LastSeq)
            {
                logger.LogError(
                    "Audit chain head is behind the stored rows; continuing after the last row. Chain={Chain}, HeadLastSeq={HeadLastSeq}, RowLastSeq={RowLastSeq}",
                    chain, head.LastSeq, last.Seq);
                head.LastSeq = last.Seq;
                head.LastHash = last.Hash ?? string.Empty;
                trusted = false;
            }

            entry.Seq = head.LastSeq + 1;
            entry.PrevHash = head.LastHash;
            entry.KeyId = key.KeyId;
            entry.Hash = AuditHashV1.ComputeRowHash(key.Bytes, entry);
            context.AuditLog.Add(entry);

            head.LastSeq = entry.Seq.Value;
            head.LastHash = entry.Hash;
            Seal(head, key, trusted);
            return 0;
        }, cancellationToken);
    }

    /// <summary>
    /// 刪除 <paramref name="kind"/> 那一類、早於 <paramref name="thresholdUtc"/> 的紀錄，回傳筆數與要刪的內容檔
    /// （由呼叫端在刪除成功後刪檔）。
    ///
    /// 已簽章的紀錄只從每條鏈最舊的一端刪，<b>刪之前先驗簽章與序號</b>：有人把紀錄的時間改舊、想讓清除替他刪掉證據時，
    /// 這條鏈就停在那裡不刪（記 Error），每天的完整性檢查會回報那幾筆被改過。鏈頭不可信任時這條鏈也不刪。
    /// 每批一個交易，鏈頭起點隨每批往前推，所以中途失敗也不會留下對不上的鏈。
    /// </summary>
    public async Task<(int Removed, List<string> ContentFiles)> PurgeAsync(
        DateTime thresholdUtc, AuditLogKind kind, CancellationToken cancellationToken = default)
    {
        var key = keyStore.GetKey();
        var removed = 0;
        var files = new List<string>();

        foreach (var chain in ChainsOf(kind))
        {
            while (true)
            {
                var batch = await LockedAsync(context => PurgeChainBatchAsync(context, key, chain, thresholdUtc, files, cancellationToken), cancellationToken);
                removed += batch;
                if (batch < PurgeBatchSize)
                {
                    break;
                }
            }
        }

        while (true)
        {
            var batch = await LockedAsync(context => PurgeLegacyBatchAsync(context, key, kind, thresholdUtc, files, cancellationToken), cancellationToken);
            removed += batch;
            if (batch < PurgeBatchSize)
            {
                break;
            }
        }

        return (removed, files);
    }

    private async Task<int> PurgeChainBatchAsync(
        BackendDBContext context, AuditIntegrityKey key, int chain, DateTime thresholdUtc, List<string> files, CancellationToken cancellationToken)
    {
        if (await context.AuditLog.AnyAsync(x => x.Chain == chain && x.Seq != null, cancellationToken) == false)
        {
            return 0;
        }

        var (heads, trustedHeads) = await PrepareHeadsAsync(context, key, cancellationToken);
        var head = heads[chain];
        if (trustedHeads[chain] == false)
        {
            logger.LogError("Audit chain head is not trusted; skipping purge of this chain. Chain={Chain}", chain);
            return 0;
        }

        var firstKeep = await FirstKeepAsync(context, chain, thresholdUtc, cancellationToken);
        var rows = await context.AuditLog.AsNoTracking()
            .Where(x => x.Chain == chain && x.Seq != null && (firstKeep == null || x.Seq < firstKeep))
            .OrderBy(x => x.Seq)
            .Take(PurgeBatchSize)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return 0;
        }

        // 刪之前先確認這一段完好：從鏈頭起點連續、前後筆接得上、目前金鑰簽的都驗得過。
        var expectedSeq = head.StartSeq;
        var expectedPrev = head.StartPrevHash;
        foreach (var row in rows)
        {
            var mustVerify = row.KeyId == key.KeyId || row.Seq >= head.KeySinceSeq;
            if (row.Seq != expectedSeq || row.PrevHash != expectedPrev
                || (mustVerify && (row.KeyId != key.KeyId || AuditHashV1.ComputeRowHash(key.Bytes, row) != row.Hash)))
            {
                logger.LogError(
                    "Audit rows to purge failed verification; this chain is not purged further. Chain={Chain}, Seq={Seq}, Id={Id}",
                    chain, row.Seq, row.Id);
                return 0;
            }

            expectedSeq++;
            expectedPrev = row.Hash ?? string.Empty;
        }

        var ids = rows.Select(x => x.Id).ToList();
        await context.AuditLog.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(cancellationToken);
        files.AddRange(rows.Where(x => x.ContentFile != null).Select(x => x.ContentFile!));

        var lastDeleted = rows[^1];
        head.StartSeq = lastDeleted.Seq!.Value + 1;
        head.StartPrevHash = lastDeleted.Hash ?? string.Empty;
        Seal(head, key, trusted: true);
        return rows.Count;
    }

    /// <summary>升級前的舊紀錄（沒有序號）依類別＋時間分批清除；清光之後把鏈頭的 LegacyMaxId 歸零，空出來的 Id 不能再冒充舊紀錄。</summary>
    private async Task<int> PurgeLegacyBatchAsync(
        BackendDBContext context, AuditIntegrityKey key, AuditLogKind kind, DateTime thresholdUtc, List<string> files, CancellationToken cancellationToken)
    {
        var rows = await AuditLogQueryService.OfKind(context.AuditLog.Where(x => x.Seq == null), kind)
            .Where(x => x.OccurredAt < thresholdUtc)
            .OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.ContentFile })
            .Take(PurgeBatchSize)
            .ToListAsync(cancellationToken);
        if (rows.Count > 0)
        {
            var ids = rows.Select(x => x.Id).ToList();
            await context.AuditLog.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(cancellationToken);
            files.AddRange(rows.Where(x => x.ContentFile != null).Select(x => x.ContentFile!));
        }

        await ResetLegacyMaxIdIfNoneLeftAsync(context, key, cancellationToken);
        return rows.Count;
    }

    private async Task ResetLegacyMaxIdIfNoneLeftAsync(BackendDBContext context, AuditIntegrityKey key, CancellationToken cancellationToken)
    {
        if (await context.AuditChainHead.AnyAsync(x => x.LegacyMaxId != 0, cancellationToken) == false
            || await context.AuditLog.AnyAsync(x => x.Seq == null, cancellationToken))
        {
            return;
        }

        var (heads, trustedHeads) = await PrepareHeadsAsync(context, key, cancellationToken);
        foreach (var head in heads.Values)
        {
            head.LegacyMaxId = 0;
            Seal(head, key, trustedHeads[head.Chain]);
        }
    }

    /// <summary>
    /// 清空全部紀錄：兩條鏈都從下一筆重新起算（序號接續、不歸零），以目前金鑰重新簽章鏈頭，LegacyMaxId 歸零。
    /// 回傳刪掉的筆數與它們的內容檔：只刪這些檔，不清整個目錄 —— 清空的同時可能有寫入已經寫好內容檔、正在等鎖。
    /// </summary>
    public async Task<(int Removed, List<string> ContentFiles)> ClearAllAsync(CancellationToken cancellationToken = default)
    {
        var key = keyStore.GetKey();
        return await LockedAsync(async context =>
        {
            var heads = await LoadHeadsAsync(context, key, cancellationToken);
            var files = await context.AuditLog.Where(x => x.ContentFile != null).Select(x => x.ContentFile!).ToListAsync(cancellationToken);
            var removed = await context.AuditLog.ExecuteDeleteAsync(cancellationToken);

            foreach (var head in heads.Values)
            {
                head.StartSeq = head.LastSeq + 1;
                head.StartPrevHash = head.LastHash;
                head.KeyId = key.KeyId;
                head.KeySinceSeq = head.StartSeq;
                head.LegacyMaxId = 0;
                head.RebuiltAt = null;
                Seal(head, key, trusted: true);
            }

            return (removed, files);
        }, cancellationToken);
    }

    /// <summary>清除範圍：每條鏈要刪的序號範圍＋升級前的舊紀錄（統計用）。</summary>
    internal sealed record ExpiredScope(List<IQueryable<AuditLog>> Scopes);

    /// <summary>
    /// 統計（<see cref="AuditLogQueryService.GetRetentionStatsAsync"/>）用的範圍，和清除用同一個保留起點，
    /// 「資料保留與清理」頁的「下次會清掉幾筆」才會等於實際刪掉的筆數（紀錄被竄改、清除停在中途時例外）。
    /// </summary>
    internal static async Task<ExpiredScope> ResolveExpiredAsync(
        BackendDBContext context, DateTime thresholdUtc, AuditLogKind kind, CancellationToken cancellationToken)
    {
        var scopes = new List<IQueryable<AuditLog>>();
        foreach (var chain in ChainsOf(kind))
        {
            var c = chain;
            var firstKeep = await FirstKeepAsync(context, c, thresholdUtc, cancellationToken);
            scopes.Add(context.AuditLog.Where(x => x.Chain == c && x.Seq != null && (firstKeep == null || x.Seq < firstKeep)));
        }

        scopes.Add(AuditLogQueryService.OfKind(context.AuditLog.Where(x => x.Seq == null), kind)
            .Where(x => x.OccurredAt < thresholdUtc));
        return new ExpiredScope(scopes);
    }

    /// <summary>
    /// 每條鏈的保留起點＝門檻之後最早的那一筆（MIN(Seq) WHERE OccurredAt &gt;= 門檻），只刪序號比它小的：
    /// 時間在進鎖前取，序號和時間偶爾會交錯幾毫秒，寧可少刪一兩筆，也不在鏈中間刪出缺口。整條鏈都過期時為 null。
    /// </summary>
    private static Task<long?> FirstKeepAsync(BackendDBContext context, int chain, DateTime thresholdUtc, CancellationToken cancellationToken)
        => context.AuditLog
            .Where(x => x.Chain == chain && x.Seq != null && x.OccurredAt >= thresholdUtc)
            .MinAsync(x => x.Seq, cancellationToken);

    private static int[] ChainsOf(AuditLogKind kind) => kind switch
    {
        AuditLogKind.Change => [AuditChains.Change],
        AuditLogKind.Browsing => [AuditChains.Browsing],
        _ => AuditChains.All,
    };

    /// <summary>排隊（同一行程）→ IMMEDIATE 交易（跨行程）→ 執行 → SaveChanges → 提交。</summary>
    private async Task<T> LockedAsync<T>(Func<BackendDBContext, Task<T>> work, CancellationToken cancellationToken)
    {
        if (await Gate.WaitAsync(LockTimeout, cancellationToken) == false)
        {
            throw new TimeoutException($"等待稽核寫入鎖超過 {LockTimeout.TotalSeconds:N0} 秒。");
        }

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var result = await work(context);
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// 讀兩條鏈的鏈頭（追蹤中，呼叫端改完由 <see cref="LockedAsync{T}"/> 存檔）。
    /// - 都沒有（第一次寫入）：一起建立，記下升級前紀錄的最大 Id。
    /// - 有簽章紀錄卻沒有鏈頭：鏈頭被刪了，依現有紀錄重建並標記 <see cref="AuditChainHead.RebuiltAt"/>，記 Error。
    /// </summary>
    private async Task<Dictionary<int, AuditChainHead>> LoadHeadsAsync(
        BackendDBContext context, AuditIntegrityKey key, CancellationToken cancellationToken)
    {
        var heads = await context.AuditChainHead.ToDictionaryAsync(x => x.Chain, cancellationToken);
        if (heads.Count == AuditChains.All.Length)
        {
            return heads;
        }

        var legacyMaxId = heads.Values.Select(x => (int?)x.LegacyMaxId).FirstOrDefault()
            ?? await context.AuditLog.Where(x => x.Seq == null).MaxAsync(x => (int?)x.Id, cancellationToken)
            ?? 0;
        var now = DateTime.UtcNow;

        foreach (var chain in AuditChains.All.Where(c => heads.ContainsKey(c) == false))
        {
            var c = chain;
            var first = await context.AuditLog.Where(x => x.Chain == c && x.Seq != null).OrderBy(x => x.Seq)
                .Select(x => new { Seq = x.Seq!.Value, x.PrevHash }).FirstOrDefaultAsync(cancellationToken);
            var last = await context.AuditLog.Where(x => x.Chain == c && x.Seq != null).OrderByDescending(x => x.Seq)
                .Select(x => new { Seq = x.Seq!.Value, x.Hash }).FirstOrDefaultAsync(cancellationToken);

            var head = new AuditChainHead
            {
                Chain = c,
                StartSeq = first?.Seq ?? 1,
                StartPrevHash = first?.PrevHash ?? AuditHashV1.Genesis,
                LastSeq = last?.Seq ?? 0,
                LastHash = last?.Hash ?? AuditHashV1.Genesis,
                KeyId = key.KeyId,
                KeySinceSeq = (last?.Seq ?? 0) + 1,
                LegacyMaxId = legacyMaxId,
                CreatedAt = now,
                RebuiltAt = last is null ? null : now,
            };

            if (last is not null)
            {
                logger.LogError(
                    "Audit chain head was missing and has been rebuilt from existing rows. Chain={Chain}, StartSeq={StartSeq}, LastSeq={LastSeq}",
                    c, head.StartSeq, head.LastSeq);
            }

            Seal(head, key, trusted: true);
            context.AuditChainHead.Add(head);
            heads[c] = head;
        }

        return heads;
    }

    /// <summary>
    /// 讀鏈頭並判斷每條鏈可不可以信任；金鑰換了的話<b>兩條鏈一起</b>改用目前金鑰並重新簽章 ——
    /// 只改有新紀錄的那條，另一條（例如瀏覽開關關著、一直沒有新紀錄）會停在舊金鑰代號，之後被誤判成竄改。
    /// </summary>
    private async Task<(Dictionary<int, AuditChainHead> Heads, Dictionary<int, bool> Trusted)> PrepareHeadsAsync(
        BackendDBContext context, AuditIntegrityKey key, CancellationToken cancellationToken)
    {
        var heads = await LoadHeadsAsync(context, key, cancellationToken);
        var trusted = heads.ToDictionary(x => x.Key, x => IsTrusted(x.Value, key));
        foreach (var head in heads.Values.Where(x => x.KeyId != key.KeyId))
        {
            AdoptCurrentKey(head, key);
            Seal(head, key, trusted[head.Chain]);
        }

        return (heads, trusted);
    }

    /// <summary>
    /// 鏈頭可以信任：沒被標記、而且
    /// - 金鑰代號相同時簽章正確；
    /// - 金鑰代號不同時，必須是金鑰真的換了：本行程剛產生新金鑰，或資料庫以外的檢查點記的是另一把金鑰。
    ///   否則就是有人改了鏈頭的金鑰代號，想跳過簽章檢查。
    /// </summary>
    private bool IsTrusted(AuditChainHead head, AuditIntegrityKey key)
    {
        if (head.Mac == TamperedMac)
        {
            return false;
        }

        if (head.KeyId == key.KeyId)
        {
            return head.Mac == AuditHashV1.ComputeHeadMac(key.Bytes, head);
        }

        return keyStore.CreatedNewKey
            || (checkpointStore.TryLoad(out var checkpoint) && checkpoint is not null && checkpoint.KeyId != key.KeyId);
    }

    /// <summary>金鑰換了：之前的紀錄只能歸類為「金鑰不同」，從下一筆起都由目前金鑰簽章。</summary>
    private void AdoptCurrentKey(AuditChainHead head, AuditIntegrityKey key)
    {
        if (head.KeyId == key.KeyId)
        {
            return;
        }

        logger.LogError(
            "Audit integrity key differs from the chain head. Chain={Chain}, HeadKeyId={HeadKeyId}, KeyId={KeyId}, FromSeq={FromSeq}",
            head.Chain, head.KeyId, key.KeyId, head.LastSeq + 1);
        head.KeyId = key.KeyId;
        head.KeySinceSeq = head.LastSeq + 1;
    }

    /// <summary>重新簽章；不可信任的鏈頭維持 <see cref="TamperedMac"/>，讓驗證持續回報，直到清空全部。</summary>
    private void Seal(AuditChainHead head, AuditIntegrityKey key, bool trusted)
    {
        if (trusted == false)
        {
            if (head.Mac != TamperedMac)
            {
                logger.LogError("Audit chain head is not trusted; it will stay flagged until cleared. Chain={Chain}", head.Chain);
            }

            head.Mac = TamperedMac;
            return;
        }

        head.Mac = AuditHashV1.ComputeHeadMac(key.Bytes, head);
    }
}
