using System.Collections.Concurrent;

namespace MyProject.Web.Diagnostics;

/// <summary>
/// 以「使用者＋用途」為單位的每分鐘次數上限（0.10.1 起，singleton）。WASM 頁面的瀏覽器錯誤回報與開頁稽核是一般 API 請求，
/// 每個請求一個 scope，<see cref="BrowserErrorReporter"/> 原本「每個 Server 連線」的上限在這裡不成立，改由這裡跨請求計數，
/// 避免某個前端錯誤反覆觸發（或有人寫迴圈）把系統例外紀錄與稽核灌爆。
/// </summary>
public sealed class PerUserRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<(string Purpose, int UserId), Queue<DateTimeOffset>> recent = new();
    private readonly TimeProvider timeProvider;
    private readonly ILogger<PerUserRateLimiter> logger;

    public PerUserRateLimiter(TimeProvider timeProvider, ILogger<PerUserRateLimiter> logger)
    {
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>這一分鐘內還沒超過 <paramref name="maxPerMinute"/> 次就記一次並回 true。</summary>
    public bool TryAcquire(string purpose, int userId, int maxPerMinute)
    {
        var now = timeProvider.GetUtcNow();
        var queue = recent.GetOrAdd((purpose, userId), _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= Window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= maxPerMinute)
            {
                logger.LogDebug("Rate limit reached. Purpose={Purpose}, UserId={UserId}, MaxPerMinute={MaxPerMinute}", purpose, userId, maxPerMinute);
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }
}
