using Microsoft.Extensions.Logging;
using MyProject.Dtos.Shell;

namespace MyProject.Web.Client.Commons;

/// <summary>
/// 前端診斷記錄的日誌接收器（0.10.37 起，單例）：只在 <see cref="ClientDiagnosticsState"/> 開啟時，把本專案（<c>MyProject.*</c>）
/// 達到門檻等級的日誌暫存起來，每 5 秒（或滿 100 筆）一批交給送件者（<see cref="UseSender"/>，由 <c>ClientMainLayout</c> 在登入後設定），
/// 送到 <c>api/v1/shell/diagnostic-logs</c>，伺服器依原等級寫進日誌檔。平常（沒開啟）什麼都不做，瀏覽器主控台照舊只顯示 Information 以上。
///
/// 不帶例外物件（只帶「型別: 訊息」），錯誤本身已由錯誤回報（<see cref="ClientErrorCapture"/>）記過。
/// 回報管線與送件相關的分類不收，以免送出時產生的日誌又被送出去。緩衝最多 500 筆，滿了丟最舊的並計數。
/// </summary>
public sealed class ClientDiagnosticsLoggerProvider : ILoggerProvider
{
    internal const int MaxBuffered = 500;
    internal const int BatchSize = 100;
    internal const int MaxPerBatch = 200;
    internal const int MaxMessageLength = 2000;
    internal static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    /// <summary>不收的分類：自己、錯誤回報管線（送出時的日誌不能再送出去）。</summary>
    private static readonly string[] ExcludedCategories =
    [
        typeof(ClientDiagnosticsLoggerProvider).FullName!,
        typeof(ClientErrorCapture).FullName!,
        "MyProject.Web.Client.Layout.ClientErrorReporter",
    ];

    private readonly ClientDiagnosticsState state;
    private readonly Queue<ClientLogEntryDto> buffer = new();
    private readonly Lock gate = new();
    private Func<ClientLogBatchDto, Task<bool?>>? sender;
    private Func<string>? currentPath;
    private Timer? timer;
    private int dropped;
    private bool flushing;

    public ClientDiagnosticsLoggerProvider(ClientDiagnosticsState state)
    {
        this.state = state;
        state.Changed += OnStateChanged;
    }

    /// <summary>
    /// 登入後由外框設定送件者：<paramref name="send"/> 回 true＝伺服器還開著、false＝已關閉（停止送）、null＝沒送出（這批捨棄並計數）。
    /// </summary>
    public void UseSender(Func<ClientLogBatchDto, Task<bool?>> send, Func<string> path)
    {
        sender = send;
        currentPath = path;
    }

    public ILogger CreateLogger(string categoryName)
        => categoryName.StartsWith("MyProject.", StringComparison.Ordinal) && !ExcludedCategories.Contains(categoryName)
            ? new DiagnosticsLogger(this, categoryName)
            : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>暫存了幾筆（測試用）。</summary>
    internal int BufferedCount
    {
        get
        {
            lock (gate)
            {
                return buffer.Count;
            }
        }
    }

    internal bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && state.Level is { } threshold && logLevel >= threshold;

    internal void Add(LogLevel logLevel, string category, string message, Exception? exception)
    {
        var entry = new ClientLogEntryDto
        {
            Level = logLevel.ToString(),
            Category = category,
            Message = message.Length <= MaxMessageLength ? message : message[..MaxMessageLength],
            ExceptionType = exception is null ? null : Clip($"{exception.GetType().FullName}: {exception.Message}", 200),
            Path = CurrentPath(),
            ClientTime = DateTimeOffset.UtcNow,
        };

        bool full;
        lock (gate)
        {
            buffer.Enqueue(entry);
            while (buffer.Count > MaxBuffered)
            {
                buffer.Dequeue();
                dropped++;
            }

            full = buffer.Count >= BatchSize;
        }

        if (full)
        {
            _ = FlushAsync();
        }
    }

    /// <summary>送出一批（計時器每 5 秒、或滿 100 筆時）。</summary>
    internal async Task FlushAsync()
    {
        if (sender is not { } send || !state.IsActive)
        {
            if (!state.IsActive)
            {
                Clear();
            }

            return;
        }

        ClientLogBatchDto batch;
        lock (gate)
        {
            if (flushing || (buffer.Count == 0 && dropped == 0))
            {
                return;
            }

            flushing = true;
            batch = new ClientLogBatchDto { Dropped = dropped };
            while (buffer.Count > 0 && batch.Items.Count < MaxPerBatch)
            {
                batch.Items.Add(buffer.Dequeue());
            }

            dropped = 0;
        }

        try
        {
            var active = await send(batch);
            if (active == false)
            {
                // 伺服器說已經關閉（或到期）：停止，不等下一次 API 回應。
                state.Update(null);
            }
            else if (active is null)
            {
                lock (gate)
                {
                    dropped += batch.Items.Count;
                }
            }
        }
        catch (Exception)
        {
            // 送出失敗不可再寫日誌（寫了又會被收進來）；這批算捨棄，下一批的 Dropped 會帶到伺服器。
            lock (gate)
            {
                dropped += batch.Items.Count;
            }
        }
        finally
        {
            lock (gate)
            {
                flushing = false;
            }
        }
    }

    private string? CurrentPath()
    {
        try
        {
            return currentPath?.Invoke();
        }
        catch (InvalidOperationException)
        {
            // 外框還沒初始化完成：沒有頁面路徑。
            return null;
        }
    }

    private void OnStateChanged()
    {
        if (state.IsActive)
        {
            timer ??= new Timer(_ => _ = FlushAsync(), null, FlushInterval, FlushInterval);
        }
        else
        {
            timer?.Dispose();
            timer = null;
            Clear();
        }
    }

    private void Clear()
    {
        lock (gate)
        {
            buffer.Clear();
            dropped = 0;
        }
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];

    public void Dispose()
    {
        state.Changed -= OnStateChanged;
        timer?.Dispose();
    }

    private sealed class DiagnosticsLogger(ClientDiagnosticsLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (provider.IsEnabled(logLevel))
            {
                provider.Add(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
