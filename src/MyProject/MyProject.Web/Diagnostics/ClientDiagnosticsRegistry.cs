using System.Collections.Concurrent;

namespace MyProject.Web.Diagnostics;

/// <summary>
/// 前端診斷記錄的對象（0.10.37 起，單例）：管理員在「日誌等級設定」頁對某位使用者開啟，限定時間、到期自動失效。
/// 開啟期間，那位使用者瀏覽器裡的日誌（Debug 或 Trace 以上）批次送到 <c>api/v1/shell/diagnostic-logs</c>，依原等級寫進日誌檔。
///
/// 只存在記憶體，重新啟動就全部關閉 —— 與「日誌等級設定」的執行期等級相同（<see cref="LogLevelRuntimeState"/>），
/// 只是短時間排錯用。單機設計（多台主機時每台各一份）。
/// </summary>
public sealed class ClientDiagnosticsRegistry
{
    /// <summary>可以選的時間（分鐘）。</summary>
    public static readonly IReadOnlyList<int> DurationMinutes = [15, 30, 60, 120, 240, 480];

    private readonly ConcurrentDictionary<int, ClientDiagnosticsSession> sessions = new();
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ClientDiagnosticsRegistry> logger;

    public ClientDiagnosticsRegistry(TimeProvider timeProvider, ILogger<ClientDiagnosticsRegistry> logger)
    {
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>開啟（同一位使用者已經開著就以這次的設定取代）。</summary>
    public ClientDiagnosticsSession Enable(int userId, string account, LogLevel level, TimeSpan duration, string enabledByAccount)
    {
        var now = timeProvider.GetUtcNow();
        var session = new ClientDiagnosticsSession(userId, account, level, now, now + duration, enabledByAccount);
        sessions[userId] = session;
        logger.LogInformation(
            "Client diagnostics enabled. UserId={UserId}, Account={Account}, Level={Level}, ExpiresAt={ExpiresAt}, EnabledBy={EnabledBy}",
            userId, account, level, session.ExpiresAt, enabledByAccount);
        return session;
    }

    /// <summary>提前關閉；本來就沒開（或已到期）回 false。</summary>
    public bool Disable(int userId)
    {
        var active = Find(userId) is not null;
        sessions.TryRemove(userId, out _);
        if (active)
        {
            logger.LogInformation("Client diagnostics disabled. UserId={UserId}", userId);
        }

        return active;
    }

    /// <summary>這位使用者目前的診斷記錄；沒開或已到期回 null（到期的順手移除）。</summary>
    public ClientDiagnosticsSession? Find(int userId)
    {
        if (!sessions.TryGetValue(userId, out var session))
        {
            return null;
        }

        if (session.ExpiresAt > timeProvider.GetUtcNow())
        {
            return session;
        }

        sessions.TryRemove(new KeyValuePair<int, ClientDiagnosticsSession>(userId, session));
        logger.LogInformation("Client diagnostics expired. UserId={UserId}", userId);
        return null;
    }

    /// <summary>開著的全部（依到期時間排序）。</summary>
    public IReadOnlyList<ClientDiagnosticsSession> List()
        => sessions.Keys.Select(Find).OfType<ClientDiagnosticsSession>().OrderBy(x => x.ExpiresAt).ToList();
}

/// <summary>一位使用者的前端診斷記錄。時間是 UTC。</summary>
public sealed record ClientDiagnosticsSession(int UserId, string Account, LogLevel Level, DateTimeOffset StartedAt, DateTimeOffset ExpiresAt, string EnabledByAccount);
