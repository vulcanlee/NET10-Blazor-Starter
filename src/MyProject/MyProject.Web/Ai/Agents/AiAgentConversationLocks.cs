namespace MyProject.Web.Ai.Agents;

/// <summary>
/// 每段 Agent 對話一把鎖（0.10.7 起，WASM 遷移 M8；singleton）。WASM 版每個請求都重新接續對話、執行、存檔，
/// 兩個分頁同時對同一段對話送出時，後到的<b>排隊等前一個做完</b>（不拒絕），接續時才讀得到前一個存下的內容；
/// 否則兩邊各自從同一份舊狀態出發，後存的蓋掉先存的，同一筆待核准也可能被核准兩次。
///
/// <para>
/// ⚠️ 只在同一個行程內有效（IIS 重疊回收的那幾秒、或多台主機時不保證）；核准另外以資料庫的條件式更新搶占
/// （<c>AiAgentConversationService.TryClaimPendingApprovalAsync</c>），跨行程也不會執行兩次。
/// 等待順序大致先到先得（<see cref="SemaphoreSlim"/> 不保證嚴格 FIFO）。
/// </para>
/// </summary>
public sealed class AiAgentConversationLocks
{
    private readonly Dictionary<Guid, Entry> entries = [];
    private readonly object gate = new();

    /// <summary>目前有人持有或等待中的對話數（測試用：用完的鎖要清掉，不會一直累積）。</summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                return entries.Count;
            }
        }
    }

    /// <summary>不等待：鎖得到回傳一張租約（用完 Dispose），正被別人持有時回 null。</summary>
    public IDisposable? TryAcquire(Guid conversationId)
    {
        var entry = Reference(conversationId);
        if (entry.Semaphore.Wait(0))
        {
            return new Lease(this, conversationId, entry);
        }

        Release(conversationId, entry, held: false);
        return null;
    }

    /// <summary>排隊等到鎖為止（<paramref name="cancellationToken"/> 取消時丟 <see cref="OperationCanceledException"/>，不佔鎖）。</summary>
    public async Task<IDisposable> AcquireAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var entry = Reference(conversationId);
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            Release(conversationId, entry, held: false);
            throw;
        }

        return new Lease(this, conversationId, entry);
    }

    private Entry Reference(Guid conversationId)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(conversationId, out var entry))
            {
                entry = new Entry();
                entries[conversationId] = entry;
            }

            entry.References++;
            return entry;
        }
    }

    private void Release(Guid conversationId, Entry entry, bool held)
    {
        lock (gate)
        {
            if (held)
            {
                entry.Semaphore.Release();
            }

            entry.References--;
            if (entry.References == 0)
            {
                entries.Remove(conversationId);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        /// <summary>持有與等待中的人數（在 <see cref="gate"/> 裡改）。</summary>
        public int References { get; set; }
    }

    /// <summary>租約：Dispose 一次就釋放，重複 Dispose 不會多放一次。</summary>
    private sealed class Lease(AiAgentConversationLocks owner, Guid conversationId, Entry entry) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                owner.Release(conversationId, entry, held: true);
            }
        }
    }
}
