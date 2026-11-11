using System.Collections.Concurrent;
using System.Net;
using MyProject.Business.Services.Other;
using MyProject.Models.Others;
using MyProject.Web.Auth;

namespace MyProject.Web.Ai.Workflows;

/// <summary>
/// 跨請求存活的工作流程執行（0.10.7 起，WASM 遷移 M8）。
///
/// <para>
/// Server 版的執行物件跟著畫面（電路）活著；改成 Web API 之後，一次執行要跨好幾個請求（開始 → 停下來等使用者 → 回覆 → …），
/// 所以放在這個 singleton 裡：每筆記下擁有者、執行物件、<b>自己的</b> <see cref="AsyncServiceScope"/>（執行器、工具、權限檢查、記帳都從這個 scope 取，
/// scope 裡的 <see cref="CurrentUserService"/> 在建立時填好目前使用者）與最後活動時間。
/// </para>
/// <para>
/// 規則：一個使用者同時只有一次執行；只有擁有者能操作（別人一律當作找不到）；同一次執行一次只能有一個請求在讀（<see cref="TryEnter"/>），
/// 停止（<see cref="RequestStop"/>）與中斷（<see cref="Interrupt"/>）可以從其他請求送來；
/// 閒置超過 <see cref="IdleTimeout"/> 的執行由背景計時器結束成「中斷」並釋放 scope。
/// </para>
/// <para>
/// ⚠️ 只在單一行程有效：網站重新啟動或 IIS 回收時執行就沒了（紀錄留在「我的執行紀錄」，最後一次存檔的內容）；多台伺服器也不共用。
/// </para>
/// </summary>
public sealed class AiWorkflowRunRegistry : IAsyncDisposable
{
    /// <summary>停下來等使用者超過這麼久沒有動作，就結束成「中斷」並釋放資源。</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>中斷正在讀取的執行時，最多等它收尾多久。</summary>
    private static readonly TimeSpan InterruptWait = TimeSpan.FromSeconds(30);

    /// <summary>網站關閉時等正在讀取的那一輪收尾的上限（短一點，不拖住 IIS 回收）。</summary>
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<Guid, AiWorkflowRunEntry> runs = new();
    private readonly object addGate = new();
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<AiWorkflowRunRegistry> logger;
    private readonly Timer timer;

    public AiWorkflowRunRegistry(IServiceScopeFactory scopeFactory, ILogger<AiWorkflowRunRegistry> logger)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
        timer = new Timer(_ => _ = SweepSafelyAsync(), null, SweepInterval, SweepInterval);
    }

    /// <summary>目前登錄的執行數（測試與診斷用）。</summary>
    public int Count => runs.Count;

    /// <summary>
    /// 建立一次執行專用的 scope，填好目前使用者與來源 IP：記帳、工具的權限檢查與稽核都以這個人計算，跟 Server 版的畫面一樣。
    /// </summary>
    public AsyncServiceScope CreateScope(CurrentUser user, IPAddress? clientIp)
    {
        var scope = scopeFactory.CreateAsyncScope();
        ApplyActor(scope.ServiceProvider, user, clientIp);
        return scope;
    }

    /// <summary>每次回覆時以這個請求載入的最新資料更新目前使用者（權限被改了，下一步就照新的算）。</summary>
    public static void RefreshActor(AiWorkflowRunEntry entry, CurrentUser user, IPAddress? clientIp)
        => ApplyActor(entry.Services, user, clientIp);

    /// <summary>登錄一次執行；這個使用者已經有一次執行時回 false（呼叫端自己收掉剛建立的執行與 scope）。</summary>
    public bool TryAdd(AiWorkflowRunEntry entry)
    {
        lock (addGate)
        {
            if (runs.Values.Any(item => item.OwnerUserId == entry.OwnerUserId))
            {
                return false;
            }

            runs[entry.RunId] = entry;
            return true;
        }
    }

    /// <summary>找這個使用者的這次執行；不存在或不是他的都回 null。</summary>
    public AiWorkflowRunEntry? Find(Guid runId, int userId)
        => runs.TryGetValue(runId, out var entry) && entry.OwnerUserId == userId && entry.Removed == false ? entry : null;

    /// <summary>這個使用者的執行（一個人同時只有一次）。</summary>
    public AiWorkflowRunEntry? FindByOwner(int userId)
        => runs.Values.FirstOrDefault(entry => entry.OwnerUserId == userId && entry.Removed == false);

    /// <summary>
    /// 取得這次執行的使用權（讀取一輪、看快照、放棄都要）；已經有別的請求在用時回 false。
    /// 取得後一定要呼叫 <see cref="ReleaseAsync"/>。
    /// </summary>
    public bool TryEnter(AiWorkflowRunEntry entry)
    {
        if (entry.Gate.Wait(0) == false)
        {
            return false;
        }

        if (entry.Removed)
        {
            entry.Gate.Release();
            return false;
        }

        entry.Touch();
        return true;
    }

    /// <summary>這一輪讀取用的「停止」權杖（<see cref="RequestStop"/> 會取消它）。在 <see cref="TryEnter"/> 之後呼叫。</summary>
    public CancellationToken BeginRound(AiWorkflowRunEntry entry)
    {
        lock (entry.Sync)
        {
            entry.Stop?.Dispose();
            entry.Stop = new CancellationTokenSource();
            return entry.Stop.Token;
        }
    }

    /// <summary>
    /// 放開使用權：流程已結束（或被要求中斷）就收尾、移出登錄並釋放 scope；停下來等使用者的留著，重新計算閒置時間。
    /// </summary>
    public async Task ReleaseAsync(AiWorkflowRunEntry entry)
    {
        try
        {
            lock (entry.Sync)
            {
                entry.Stop?.Dispose();
                entry.Stop = null;
            }

            if (entry.InterruptRequested && entry.Run.Result is null)
            {
                // 剛好停在等使用者的那一刻被中斷：讀取那一輪沒收尾，這裡收（存成「中斷」）。
                await entry.Run.DisposeAsync();
            }

            if (entry.Run.Result is not null)
            {
                await RemoveAsync(entry);
            }
            else
            {
                entry.Touch();
            }
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>「停止」：取消正在讀取的那一輪（流程存成「已停止」）。沒有在讀取時什麼都不做。</summary>
    public void RequestStop(AiWorkflowRunEntry entry)
    {
        lock (entry.Sync)
        {
            entry.Stop?.Cancel();
        }
    }

    /// <summary>瀏覽器在讀取途中斷線：視同離開頁面，流程存成「中斷」（收尾在 <see cref="ReleaseAsync"/>）。</summary>
    /// <summary>有沒有請求正在使用這次執行（例如另一個分頁正在串流）。</summary>
    public bool IsBusy(AiWorkflowRunEntry entry) => entry.Gate.CurrentCount == 0;

    public void Interrupt(AiWorkflowRunEntry entry)
    {
        entry.InterruptRequested = true;
        entry.Run.Interrupt();
    }

    /// <summary>
    /// 放棄這次執行（使用者確認放棄、刪除它的紀錄，或離開頁面）：存成「中斷」、移出登錄並釋放 scope。
    /// 正在讀取時先中斷它，再等那一輪收尾（最多 <see cref="InterruptWait"/>）。
    /// </summary>
    public async Task AbandonAsync(AiWorkflowRunEntry entry)
    {
        if (TryEnter(entry))
        {
            entry.InterruptRequested = true;
            await ReleaseAsync(entry);
            return;
        }

        Interrupt(entry);
        if (await entry.Gate.WaitAsync(InterruptWait))
        {
            // 讀取那一輪已在自己的 ReleaseAsync 收尾並移出登錄。
            entry.Gate.Release();
        }
    }

    /// <summary>結束閒置超過 <see cref="IdleTimeout"/> 的執行（存成「中斷」並釋放 scope）。正在讀取的不算閒置。回傳結束了幾筆。</summary>
    public async Task<int> SweepAsync(DateTime nowUtc)
    {
        var count = 0;
        foreach (var entry in runs.Values)
        {
            if (nowUtc - entry.LastActivityUtc < IdleTimeout || entry.Gate.Wait(0) == false)
            {
                continue;
            }

            try
            {
                if (entry.Removed)
                {
                    continue;
                }

                logger.LogInformation(
                    "AI workflow run timed out while waiting for the user. ConversationId={ConversationId}, UserId={UserId}",
                    entry.RunId, entry.OwnerUserId);
                await entry.Run.DisposeAsync();
                await RemoveAsync(entry);
                count++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to end an idle AI workflow run. ConversationId={ConversationId}", entry.RunId);
            }
            finally
            {
                entry.Gate.Release();
            }
        }

        return count;
    }

    /// <summary>網站關閉：盡量把還在的執行存成「中斷」並釋放 scope（失敗只記日誌）。</summary>
    public async ValueTask DisposeAsync()
    {
        await timer.DisposeAsync();
        foreach (var entry in runs.Values)
        {
            try
            {
                if (entry.Gate.Wait(0))
                {
                    await entry.Run.DisposeAsync();
                }
                else
                {
                    // 正在讀取的那一輪：先中斷，再等它收尾存檔（最多 InterruptWait）才釋放 scope ——
                    // 直接釋放的話，那一輪存成「中斷」時會碰到已釋放的服務（0.10.7 獨立審查）。
                    entry.Run.Interrupt();
                    if (await entry.Gate.WaitAsync(ShutdownWait))
                    {
                        entry.Gate.Release();
                    }
                }

                entry.Removed = true;
                await entry.Scope.DisposeAsync();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Ignoring failure while ending an AI workflow run at shutdown. ConversationId={ConversationId}", entry.RunId);
            }
        }

        runs.Clear();
    }

    private async Task RemoveAsync(AiWorkflowRunEntry entry)
    {
        entry.Removed = true;
        runs.TryRemove(new KeyValuePair<Guid, AiWorkflowRunEntry>(entry.RunId, entry));
        try
        {
            await entry.Scope.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to dispose the service scope of an AI workflow run. ConversationId={ConversationId}", entry.RunId);
        }
    }

    private async Task SweepSafelyAsync()
    {
        try
        {
            await SweepAsync(DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to sweep idle AI workflow runs.");
        }
    }

    private static void ApplyActor(IServiceProvider services, CurrentUser user, IPAddress? clientIp)
    {
        CurrentUserLoader.ApplyTo(services.GetRequiredService<CurrentUserService>(), user);

        // 排程、背景與跨請求的執行沒有可靠的 HttpContext：來源 IP 直接填進去（同 Blazor 電路的做法）。
        services.GetRequiredService<ClientIpProvider>().SetCircuitClientIp(clientIp);
    }
}

/// <summary>登錄表裡的一次執行。</summary>
public sealed class AiWorkflowRunEntry
{
    private long lastActivityTicks;

    public AiWorkflowRunEntry(int ownerUserId, AiWorkflowRun run, AsyncServiceScope scope)
    {
        OwnerUserId = ownerUserId;
        Run = run;
        Scope = scope;
        Touch();
    }

    public Guid RunId => Run.ConversationId;

    public int OwnerUserId { get; }

    public AiWorkflowRun Run { get; }

    public IServiceProvider Services => Scope.ServiceProvider;

    /// <summary>最後一次有請求用它（開始、回覆、接回來）的時間（UTC）。</summary>
    public DateTime LastActivityUtc => new(Interlocked.Read(ref lastActivityTicks), DateTimeKind.Utc);

    internal AsyncServiceScope Scope { get; }

    /// <summary>同一次執行一次只能有一個請求在用（<see cref="AiWorkflowRun"/> 不是執行緒安全的）。</summary>
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    internal object Sync { get; } = new();

    /// <summary>目前這一輪讀取的「停止」；沒有在讀取時為 null。</summary>
    internal CancellationTokenSource? Stop { get; set; }

    internal volatile bool InterruptRequested;

    internal volatile bool Removed;

    /// <summary>測試用：把最後活動時間設成指定的時間。</summary>
    internal void SetLastActivity(DateTime utc) => Interlocked.Exchange(ref lastActivityTicks, utc.Ticks);

    internal void Touch() => SetLastActivity(DateTime.UtcNow);
}
