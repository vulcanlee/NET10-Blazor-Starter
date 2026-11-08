using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;

namespace MyProject.Business.Services.Other.AuditIntegrity;

/// <summary>完整性檢查發現的一個問題。</summary>
/// <param name="Code">問題種類（英文代碼，測試與日誌用）。</param>
/// <param name="Message">給管理員看的說明。</param>
public sealed record AuditIntegrityIssue(string Code, string Message);

/// <summary>完整性檢查的結果。</summary>
public sealed class AuditIntegrityReport
{
    /// <summary>結果最多列出的問題數；超過的只計數。</summary>
    public const int MaxListedIssues = 50;

    /// <summary>檢查時間（UTC）。</summary>
    public DateTime VerifiedAtUtc { get; init; }
    /// <summary>檢查了幾筆有簽章的紀錄。</summary>
    public int SignedRows { get; internal set; }
    /// <summary>其中用目前金鑰驗過簽章、內容正確的筆數。</summary>
    public int VerifiedRows { get; internal set; }
    /// <summary>別的金鑰簽的（金鑰遺失或換主機沒帶金鑰）：只能確認序號連續，無法驗內容。屬於警告，不算問題。</summary>
    public int KeyMismatchRows { get; internal set; }
    /// <summary>升級前的舊紀錄（沒有簽章），不納入驗證，不算問題。</summary>
    public int LegacyRows { get; internal set; }
    /// <summary>問題總數。</summary>
    public int IssueCount { get; private set; }
    /// <summary>前 <see cref="MaxListedIssues"/> 個問題。</summary>
    public List<AuditIntegrityIssue> Issues { get; } = [];

    public bool HasIssues => IssueCount > 0;

    internal void Add(string code, string message)
    {
        IssueCount++;
        if (Issues.Count < MaxListedIssues)
        {
            Issues.Add(new AuditIntegrityIssue(code, message));
        }
    }

    /// <summary>沒有逐一列出、只計數的問題（例如超過清單上限的未簽章紀錄）。</summary>
    internal void AddUnlisted(int count) => IssueCount += count;

    /// <summary>一句話的摘要（排程結果、稽核摘要與通知共用）。</summary>
    public string Summary()
    {
        var extras = new List<string>();
        if (LegacyRows > 0)
        {
            extras.Add($"{LegacyRows} 筆升級前的紀錄未納入");
        }

        if (KeyMismatchRows > 0)
        {
            extras.Add($"{KeyMismatchRows} 筆是別的金鑰簽的、無法驗證內容");
        }

        var tail = extras.Count == 0 ? string.Empty : $"（{string.Join("；", extras)}）";
        return HasIssues
            ? $"檢查 {SignedRows} 筆，發現 {IssueCount} 個問題{tail}。"
            : $"檢查 {SignedRows} 筆，全部正確{tail}。";
    }
}

/// <summary>
/// 稽核紀錄完整性檢查（0.9.175 起）：重算每一筆的簽章、確認序號連續、前後筆接得上、頭尾和鏈頭一致、
/// 內容檔沒被改，並和資料庫以外的檢查點比對。
///
/// 讀取在唯讀快照交易（DEFERRED）裡進行，不會擋住同時發生的稽核寫入；內容檔在快照結束後才檢查，
/// 檔案不見時再確認那筆是否還在，避免把檢查途中剛被正常清除的紀錄當成異常。
/// 發現問題一律記 Error：通知與排程紀錄都在資料庫裡，能改資料庫的人可以刪掉，日誌檔在資料庫以外。
/// </summary>
public sealed class AuditIntegrityVerifier
{
    private const int BatchSize = 5000;

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly IAuditIntegrityKeyStore keyStore;
    private readonly AuditChainCheckpointStore checkpointStore;
    private readonly AuditContentFileStore contentFileStore;
    private readonly ILogger<AuditIntegrityVerifier> logger;

    public AuditIntegrityVerifier(
        IDbContextFactory<BackendDBContext> contextFactory,
        IAuditIntegrityKeyStore keyStore,
        AuditChainCheckpointStore checkpointStore,
        AuditContentFileStore contentFileStore,
        ILogger<AuditIntegrityVerifier> logger)
    {
        this.contextFactory = contextFactory;
        this.keyStore = keyStore;
        this.checkpointStore = checkpointStore;
        this.contentFileStore = contentFileStore;
        this.logger = logger;
    }

    private sealed record ContentCheck(int Id, string Label, string File, string? Hash);

    /// <param name="updateCheckpoint">每天的排程傳 true；手動驗證只比對、不更新檢查點。</param>
    public async Task<AuditIntegrityReport> VerifyAsync(bool updateCheckpoint, CancellationToken cancellationToken = default)
    {
        var report = new AuditIntegrityReport { VerifiedAtUtc = DateTime.UtcNow };
        var key = keyStore.GetKey();
        if (checkpointStore.TryLoad(out var checkpoint) == false)
        {
            report.Add("CheckpointUnreadable", "檢查點檔讀不懂（被改壞或被換掉），這一次無法比對「資料庫有沒有被倒回舊版」；排程會重寫一份新的檢查點。");
        }

        var observed = new List<AuditChainCheckpointEntry>();
        var contentChecks = new List<ContentCheck>();

        switch (keyStore.CheckFile())
        {
            case AuditKeyFileStatus.Restored:
                report.Add("KeyFileRestored", "金鑰檔不見了，已用執行中的金鑰寫回去。請查明是誰刪的，並重新備份金鑰檔。");
                break;
            case AuditKeyFileStatus.Replaced:
                report.Add("KeyFileReplaced", "金鑰檔被換成別的內容。網站下次重新啟動就會改用新的金鑰，之前的紀錄將無法驗證內容。");
                break;
        }

        if (checkpoint is not null && checkpoint.KeyId != key.KeyId)
        {
            report.Add("KeyChanged", "金鑰和上次檢查時不同（金鑰檔遺失後重新產生，或被更換）；之前的紀錄只能確認序號連續，無法驗證內容。");
        }

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            await context.Database.OpenConnectionAsync(cancellationToken);
            var connection = (SqliteConnection)context.Database.GetDbConnection();
            await using var snapshot = connection.BeginTransaction(deferred: true);
            await context.Database.UseTransactionAsync(snapshot, cancellationToken);

            var heads = await context.AuditChainHead.AsNoTracking().ToDictionaryAsync(x => x.Chain, cancellationToken);
            await CheckUnsignedRowsAsync(context, heads, report, cancellationToken);

            foreach (var chain in AuditChains.All)
            {
                var checkpointEntry = checkpoint?.Chains.Find(x => x.Chain == chain);
                if (heads.TryGetValue(chain, out var head) == false)
                {
                    var c = chain;
                    if (await context.AuditLog.AnyAsync(x => x.Chain == c && x.Seq != null, cancellationToken))
                    {
                        report.Add("HeadMissing", $"{AuditChains.Name(chain)}：鏈頭不見了（被刪除）。");
                    }
                    else if (checkpointEntry is { LastSeq: > 0 })
                    {
                        report.Add("HeadMissing", $"{AuditChains.Name(chain)}：鏈頭和紀錄都不見了（上次檢查時到 #{checkpointEntry.LastSeq}）。");
                    }

                    continue;
                }

                var checkpointHash = await CheckChainAsync(
                    context, head, key, checkpoint?.KeyId, checkpointEntry?.LastSeq, report, contentChecks, cancellationToken);
                CompareWithCheckpoint(head, checkpointEntry, checkpointHash, report);
                observed.Add(new AuditChainCheckpointEntry(chain, head.LastSeq, head.LastHash, head.RebuiltAt));
            }
        }

        await CheckContentFilesAsync(contentChecks, report, cancellationToken);

        if (updateCheckpoint)
        {
            checkpointStore.Save(new AuditChainCheckpoint(key.KeyId, report.VerifiedAtUtc, observed));
        }

        if (report.HasIssues)
        {
            logger.LogError(
                "Audit integrity check found problems. Issues={IssueCount}, First={FirstIssues}",
                report.IssueCount,
                string.Join(" | ", report.Issues.Take(5).Select(x => $"{x.Code}: {x.Message}")));
        }
        else
        {
            logger.LogInformation("Audit integrity check passed. SignedRows={SignedRows}", report.SignedRows);
        }

        return report;
    }

    /// <summary>沒有簽章的紀錄：Id 不超過建立鏈頭時的最大值是升級前的舊紀錄，超過的就是繞過寫入服務塞進來的。</summary>
    private static async Task CheckUnsignedRowsAsync(
        BackendDBContext context, Dictionary<int, AuditChainHead> heads, AuditIntegrityReport report, CancellationToken cancellationToken)
    {
        var legacyMaxId = heads.Count == 0 ? int.MaxValue : heads.Values.Min(x => x.LegacyMaxId);
        report.LegacyRows = await context.AuditLog.CountAsync(x => x.Seq == null && x.Id <= legacyMaxId, cancellationToken);

        var forgedCount = await context.AuditLog.CountAsync(x => x.Seq == null && x.Id > legacyMaxId, cancellationToken);
        var forged = await context.AuditLog
            .Where(x => x.Seq == null && x.Id > legacyMaxId)
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .Take(AuditIntegrityReport.MaxListedIssues)
            .ToListAsync(cancellationToken);
        foreach (var id in forged)
        {
            report.Add("Unsigned", $"編號 {id}：沒有簽章（繞過系統直接寫進資料庫）。");
        }

        report.AddUnlisted(forgedCount - forged.Count);

        var orphans = await context.AuditLog
            .Where(x => x.Seq != null && (x.Chain == null || (x.Chain != AuditChains.Change && x.Chain != AuditChains.Browsing)))
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .Take(AuditIntegrityReport.MaxListedIssues)
            .ToListAsync(cancellationToken);
        foreach (var id in orphans)
        {
            report.Add("UnknownChain", $"編號 {id}：鏈別不正確（被改過）。");
        }
    }

    /// <summary>逐筆檢查一條鏈；回傳檢查點那一筆目前的雜湊（不存在時為 null）。</summary>
    private static async Task<string?> CheckChainAsync(
        BackendDBContext context,
        AuditChainHead head,
        AuditIntegrityKey key,
        string? checkpointKeyId,
        long? checkpointSeq,
        AuditIntegrityReport report,
        List<ContentCheck> contentChecks,
        CancellationToken cancellationToken)
    {
        var name = AuditChains.Name(head.Chain);
        var headKeyMatches = head.KeyId == key.KeyId;

        // 鏈頭的金鑰代號和目前不同，但上次檢查時就已經是這把金鑰：金鑰沒換，是有人改了代號想跳過簽章檢查。
        var forgedHeadKey = headKeyMatches == false && checkpointKeyId == key.KeyId;
        var headTampered = head.Mac == AuditChainStore.TamperedMac
            || forgedHeadKey
            || (headKeyMatches && head.Mac != AuditHashV1.ComputeHeadMac(key.Bytes, head));
        if (headTampered)
        {
            report.Add("HeadTampered", $"{name}：鏈頭的簽章或金鑰代號不對（鏈頭被改過）。");
        }

        // 金鑰真的換了、而且換了之後還沒有新紀錄時，無法信任鏈頭的 KeySinceSeq：別的金鑰簽的只能歸類為「金鑰不同」。
        // 鏈頭被改過時反過來從頭嚴格檢查：每一筆都必須是目前金鑰簽的。
        var keySinceSeq = headTampered ? head.StartSeq : headKeyMatches ? head.KeySinceSeq : long.MaxValue;
        var expectedSeq = head.StartSeq;
        var expectedPrev = head.StartPrevHash;
        string? checkpointHash = null;
        var chain = head.Chain;
        var cursor = long.MinValue;
        var processed = 0;

        while (true)
        {
            var after = cursor;
            var rows = await context.AuditLog.AsNoTracking()
                .Where(x => x.Chain == chain && x.Seq != null && x.Seq > after)
                .OrderBy(x => x.Seq)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (rows.Count == 0)
            {
                break;
            }

            foreach (var row in rows)
            {
                var seq = row.Seq!.Value;
                cursor = seq;
                processed++;
                report.SignedRows++;

                if (seq < head.StartSeq)
                {
                    report.Add("BeforeStart", $"{name} #{seq}：早於鏈頭的起點 #{head.StartSeq}（鏈頭被改過）。");
                    continue;
                }

                if (seq < expectedSeq)
                {
                    // 同一個序號第二次出現（唯一索引被移除後塞入）。
                    report.Add("DuplicateSeq", $"{name} #{seq}：序號重複（唯一索引被移除後塞入）。");
                    continue;
                }

                if (seq != expectedSeq)
                {
                    report.Add("Gap", $"{name}：缺少 {Range(expectedSeq, seq - 1)}（被刪除）。");
                }
                else if (row.PrevHash != expectedPrev)
                {
                    report.Add("BrokenLink", $"{name} #{seq}：和前一筆接不上（前一筆被換掉，或這一筆被改過）。");
                }

                if (row.KeyId == key.KeyId)
                {
                    if (AuditHashV1.ComputeRowHash(key.Bytes, row) == row.Hash)
                    {
                        report.VerifiedRows++;
                    }
                    else
                    {
                        report.Add("BadHash", $"{name} #{seq}（編號 {row.Id}）：內容和簽章不符（被改過）。");
                    }
                }
                else if (seq >= keySinceSeq)
                {
                    report.Add("KeyIdForged", $"{name} #{seq}（編號 {row.Id}）：金鑰代號不對（偽造）。");
                }
                else
                {
                    report.KeyMismatchRows++;
                }

                if (row.ContentFile is not null)
                {
                    contentChecks.Add(new ContentCheck(row.Id, $"{name} #{seq}（編號 {row.Id}）", row.ContentFile, row.ContentHash));
                }

                if (seq == checkpointSeq)
                {
                    checkpointHash = row.Hash;
                }

                expectedSeq = seq + 1;
                expectedPrev = row.Hash ?? string.Empty;
            }
        }

        var stored = await context.AuditLog.CountAsync(x => x.Chain == chain && x.Seq != null, cancellationToken);
        if (stored > processed)
        {
            report.Add("DuplicateSeq", $"{name}：有 {stored - processed} 筆序號重複（唯一索引被移除後塞入）。");
        }

        if (expectedSeq - 1 < head.LastSeq)
        {
            report.Add("TailMissing", $"{name}：缺少最後的 {Range(expectedSeq, head.LastSeq)}（被刪除）。");
        }
        else if (expectedSeq - 1 > head.LastSeq)
        {
            report.Add("BeyondHead", $"{name}：有序號超過鏈頭終點 #{head.LastSeq} 的紀錄（被塞入，或鏈頭被倒回）。");
        }
        else if (expectedPrev != head.LastHash)
        {
            report.Add("TailMismatch", $"{name}：最後一筆和鏈頭對不上（被換掉）。");
        }

        return checkpointHash;
    }

    /// <summary>缺號範圍的寫法：一筆寫「#7」，多筆寫「#7～#9，共 3 筆」。</summary>
    private static string Range(long from, long to)
        => from == to ? $"#{from}" : $"#{from}～#{to}，共 {to - from + 1} 筆";

    /// <summary>和上次每日檢查的狀態比對：鏈頭重建、最新紀錄被刪或資料庫倒回舊版。</summary>
    private static void CompareWithCheckpoint(
        AuditChainHead head, AuditChainCheckpointEntry? checkpointEntry, string? checkpointHash, AuditIntegrityReport report)
    {
        var name = AuditChains.Name(head.Chain);
        if (head.RebuiltAt is { } rebuiltAt && rebuiltAt != checkpointEntry?.RebuiltAt)
        {
            report.Add("HeadRebuilt", $"{name}：鏈頭曾經不見（被刪除），已在 {rebuiltAt.ToLocalTime():yyyy/MM/dd HH:mm} 依現有紀錄重建；重建前被刪的最新紀錄無法得知。");
        }

        if (checkpointEntry is null)
        {
            return;
        }

        if (head.LastSeq < checkpointEntry.LastSeq)
        {
            report.Add("RolledBack", $"{name}：目前只到 #{head.LastSeq}，比上次檢查時的 #{checkpointEntry.LastSeq} 還舊（最新的紀錄被刪，或資料庫被倒回舊版）。");
        }
        else if (checkpointHash is not null && checkpointHash != checkpointEntry.LastHash)
        {
            report.Add("RolledBack", $"{name} #{checkpointEntry.LastSeq}：和上次檢查時不同（資料庫被換成另一份）。");
        }
    }

    private async Task CheckContentFilesAsync(List<ContentCheck> checks, AuditIntegrityReport report, CancellationToken cancellationToken)
    {
        foreach (var check in checks)
        {
            var bytes = await contentFileStore.ReadBytesAsync(check.File);
            if (bytes is null)
            {
                await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
                if (await context.AuditLog.AnyAsync(x => x.Id == check.Id, cancellationToken))
                {
                    report.Add("ContentMissing", $"{check.Label}：內容檔不見了。");
                }
            }
            else if (check.Hash is not null && AuditHashV1.Sha256Hex(bytes) != check.Hash)
            {
                report.Add("ContentModified", $"{check.Label}：內容檔被改過。");
            }
        }
    }
}
