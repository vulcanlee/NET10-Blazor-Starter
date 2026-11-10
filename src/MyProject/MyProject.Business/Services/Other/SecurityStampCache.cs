namespace MyProject.Business.Services.Other;

/// <summary>
/// 工作階段版本的行程內快取（0.10.0 起獨立出來，singleton）。
/// 由 <see cref="SecurityStampService"/> 讀寫，<see cref="SecurityStampInvalidationInterceptor"/> 在版本被改時清除；
/// 獨立成一個類別是為了打破相依循環：攔截器掛在 DbContext 選項上，而 <see cref="SecurityStampService"/> 本身又需要 DbContext 工廠。
///
/// ⚠️ 世代計數：查資料庫前先記下 <see cref="Generation"/>，寫回時用 <see cref="TrySet"/>。查詢途中若版本剛好被換掉、快取被清過，
/// 世代已經變了，這次讀到的舊版本就不寫回 —— 否則舊登入又能用到快取過期（最多 <c>ValidationIntervalMinutes</c>）。
/// </summary>
public sealed class SecurityStampCache
{
    private readonly Dictionary<int, (UserSessionState State, DateTimeOffset FetchedAt)> entries = [];
    private readonly Lock sync = new();
    private long generation;

    /// <summary>每次清除（全部或單一使用者）都加一。</summary>
    public long Generation
    {
        get
        {
            lock (sync)
            {
                return generation;
            }
        }
    }

    public bool TryGet(int userId, out UserSessionState state, out DateTimeOffset fetchedAt)
    {
        lock (sync)
        {
            if (entries.TryGetValue(userId, out var entry))
            {
                state = entry.State;
                fetchedAt = entry.FetchedAt;
                return true;
            }
        }

        state = new UserSessionState(string.Empty, false);
        fetchedAt = default;
        return false;
    }

    /// <summary>寫入查到的狀態；<paramref name="readGeneration"/> 是查詢開始前的世代，期間被清過就不寫並回 false。</summary>
    public bool TrySet(int userId, UserSessionState state, DateTimeOffset fetchedAt, long readGeneration)
    {
        lock (sync)
        {
            if (readGeneration != generation)
            {
                return false;
            }

            entries[userId] = (state, fetchedAt);
            return true;
        }
    }

    public void Remove(int userId)
    {
        lock (sync)
        {
            entries.Remove(userId);
            generation++;
        }
    }

    /// <summary>全部清掉。只在版本被改時呼叫，代價只是之後幾次查詢回到資料庫。</summary>
    public void Clear()
    {
        lock (sync)
        {
            entries.Clear();
            generation++;
        }
    }
}
