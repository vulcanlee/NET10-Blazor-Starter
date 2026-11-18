using Microsoft.Extensions.Logging;

namespace MyProject.Web.Client.Commons;

/// <summary>
/// 前端診斷記錄的狀態（0.10.37 起，單例）：管理員在「日誌等級設定」頁對這位使用者開啟時，伺服器的 API 回應帶
/// <c>X-Client-Diagnostics: 等級;到期時間 HH:mm;剩幾秒</c>（例如 <c>Debug;15:30;3540</c>）。<c>ApiClient</c> 每次帶 Token 的呼叫都交給 <see cref="Update"/>：
/// 有這個標頭就開啟（<see cref="ClientDiagnosticsLoggerProvider"/> 開始把日誌送去伺服器），沒有就關閉。
/// 到期時間在這裡依「剩幾秒」換算，不依賴瀏覽器時鐘，到期就自己停止（不必等下一次 API 回應）。
/// </summary>
public sealed class ClientDiagnosticsState
{
    private LogLevel? level;
    private long expiresAt;

    /// <summary>開啟中的門檻等級；沒開回 null。</summary>
    public LogLevel? Level => IsActive ? level : null;

    /// <summary>到期時間（伺服器本地時間 HH:mm），狀態列顯示用。</summary>
    public string Until { get; private set; } = string.Empty;

    public bool IsActive => level is not null && Environment.TickCount64 < expiresAt;

    /// <summary>開啟或關閉了。</summary>
    public event Action? Changed;

    /// <summary>依伺服器回應的標頭更新；<paramref name="header"/> 為 null（或格式不對）＝已關閉。</summary>
    public void Update(string? header)
    {
        var wasActive = IsActive;
        var previousLevel = level;
        var previousUntil = Until;
        if (TryParse(header, out var parsedLevel, out var until, out var seconds))
        {
            level = parsedLevel;
            Until = until;
            expiresAt = Environment.TickCount64 + seconds * 1000L;
        }
        else
        {
            level = null;
            Until = string.Empty;
        }

        if (wasActive != IsActive || previousLevel != level || previousUntil != Until)
        {
            Changed?.Invoke();
        }
    }

    internal static bool TryParse(string? header, out LogLevel level, out string until, out int seconds)
    {
        level = LogLevel.None;
        until = string.Empty;
        seconds = 0;
        var parts = header?.Split(';');
        if (parts is not { Length: 3 }
            || !Enum.TryParse(parts[0], ignoreCase: true, out level)
            || level is not (LogLevel.Trace or LogLevel.Debug)
            || !int.TryParse(parts[2], out seconds)
            || seconds <= 0)
        {
            return false;
        }

        until = parts[1].Length <= 5 ? parts[1] : parts[1][..5];
        return true;
    }
}
