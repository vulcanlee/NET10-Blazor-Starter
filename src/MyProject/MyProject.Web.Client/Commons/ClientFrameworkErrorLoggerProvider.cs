using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MyProject.Web.Client.Commons;

/// <summary>
/// 錯誤邊界以外的 .NET 未處理例外（0.10.36 起）：沒有被任何 <see cref="ClientErrorBoundary"/> 接住的例外
/// （例如根元件底下的狀態列、射後不理的事件處理），Blazor 只會寫一筆 Error／Critical 日誌並顯示黃色的「系統發生未預期的錯誤」列。
/// 這裡只接 <c>Microsoft.AspNetCore.Components.*</c>（框架）的日誌、Error 以上且帶例外物件的，轉給 <see cref="ClientErrorCapture"/> 回報；
/// 錯誤邊界已經回報過的同一個例外不會再送。本專案自己的 <c>LogError</c> 不在這裡處理（頁面 catch 到的由 <c>ViewNotification.UnexpectedError</c> 回報）。
///
/// <see cref="ClientErrorCapture"/> 在第一次用到時才取（它相依 JS 執行環境，日誌系統建立時還沒有）。
/// </summary>
public sealed class ClientFrameworkErrorLoggerProvider : ILoggerProvider
{
    internal const string FrameworkCategoryPrefix = "Microsoft.AspNetCore.Components";

    private readonly IServiceProvider services;

    public ClientFrameworkErrorLoggerProvider(IServiceProvider services)
    {
        this.services = services;
    }

    public ILogger CreateLogger(string categoryName)
        => categoryName.StartsWith(FrameworkCategoryPrefix, StringComparison.Ordinal) ? new FrameworkErrorLogger(this) : NullLoggerInstance;

    public void Dispose()
    {
        // 沒有持有任何資源。
    }

    private static readonly ILogger NullLoggerInstance = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    internal void OnError(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return;
        }

        try
        {
            services.GetService<ClientErrorCapture>()?.Capture("unhandled", exception);
        }
        catch (Exception ex)
        {
            // 回報失敗不可以讓框架的錯誤處理再丟例外；也不能寫日誌（寫到框架分類會回到這裡），只留在主控台。
            Console.Error.WriteLine($"Failed to report an unhandled framework exception: {ex.GetType().Name}");
        }
    }

    private sealed class FrameworkErrorLogger(ClientFrameworkErrorLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error && exception is not null)
            {
                provider.OnError(exception);
            }
        }
    }
}
