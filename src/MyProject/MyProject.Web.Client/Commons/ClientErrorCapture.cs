using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MyProject.Web.Client.Commons;

/// <summary>
/// 前端 .NET 這一側的錯誤回報入口（0.10.36 起）：把例外、API 呼叫與換頁交給 <c>wwwroot/js/client-error-reporter.js</c>。
/// 那支 JS 是唯一的回報管線：先存進瀏覽器（localStorage），登入後由 <c>Layout/ClientErrorReporter</c> 批次送到伺服器，
/// 所以登入前、離線時的錯誤也不會遺失。
///
/// <list type="bullet">
/// <item><see cref="Capture"/> 在這裡產生 8 碼追蹤碼（與伺服器 <c>TraceCode</c> 同一組字元）並立即回傳，畫面可以馬上顯示；
/// 伺服器收到時沿用同一個碼，管理員拿使用者看到的碼就查得到。同一個例外只回報一次（錯誤邊界與框架日誌可能先後看到同一個）。</item>
/// <item>全部同步呼叫、失敗一律吞掉（只寫主控台）：回報本身不可以讓畫面再出錯。</item>
/// </list>
/// 單例：WASM 的 <see cref="IJSRuntime"/> 是單例，靜態的 <c>ViewNotification</c> 與框架日誌接收器也要用得到。
/// </summary>
public sealed class ClientErrorCapture
{
    /// <summary>與伺服器 <c>MyProject.Web.Diagnostics.TraceCode</c> 相同（Crockford Base32，去掉 I／L／O／U；<c>TraceCodeAlphabetTests</c> 守門）。</summary>
    internal const string TraceCodeAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int TraceCodeLength = 8;

    private readonly IJSRuntime js;
    private readonly ILogger<ClientErrorCapture> logger;
    private readonly ConditionalWeakTable<Exception, string> captured = new();

    public ClientErrorCapture(IJSRuntime js, ILogger<ClientErrorCapture> logger)
    {
        this.js = js;
        this.logger = logger;
    }

    /// <summary>
    /// 回報一個前端例外，回傳它的追蹤碼（同一個例外再回報時回傳第一次的碼，不再送）。
    /// </summary>
    /// <param name="kind"><c>boundary</c>（錯誤邊界）、<c>unhandled</c>（框架記下的未處理例外）、<c>caught</c>（頁面 catch 到、以 UnexpectedError 通知的）。</param>
    public string Capture(string kind, Exception exception)
    {
        if (captured.TryGetValue(exception, out var existing))
        {
            return existing;
        }

        var code = NewTraceCode();
        captured.AddOrUpdate(exception, code);
        try
        {
            // 錯誤邊界的畫面（ErrorContent）只拿得到例外，追蹤碼記在例外上才顯示得出來。
            exception.Data[ExceptionDataKey] = code;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            // 少數例外型別的 Data 是唯讀的：只是錯誤邊界畫面上少了追蹤碼，回報照送。
            logger.LogDebug(ex, "Failed to attach the trace code to the exception.");
        }

        Invoke("appClientErrors.report", kind, exception.Message, exception.ToString(), exception.GetType().FullName, code);
        return code;
    }

    private const string ExceptionDataKey = "MyProject.TraceCode";

    /// <summary>這個例外回報時的追蹤碼；沒有回報過回 null（錯誤邊界的畫面用）。</summary>
    public static string? TraceCodeOf(Exception? exception)
        => exception?.Data.Contains(ExceptionDataKey) == true ? exception.Data[ExceptionDataKey] as string : null;

    /// <summary>一次 API 呼叫完成（操作軌跡；<paramref name="status"/> 為 0＝連不上伺服器，JS 依此判斷連線中斷的開始與結束）。</summary>
    public void Api(string method, string url, int status, long elapsedMilliseconds, string? traceCode)
        => Invoke("appClientErrors.api", method, url, status, elapsedMilliseconds, traceCode);

    /// <summary>換頁（操作軌跡）。</summary>
    public void Navigated(string path) => Invoke("appClientErrors.nav", path);

    /// <summary>前端程式的指紋（附在每筆回報上）。</summary>
    public void SetAppBuild(string appBuild) => Invoke("appClientErrors.setContext", new { appBuild });

    internal static string NewTraceCode()
    {
        Span<byte> bytes = stackalloc byte[TraceCodeLength];
        RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[TraceCodeLength];
        for (var index = 0; index < TraceCodeLength; index++)
        {
            chars[index] = TraceCodeAlphabet[bytes[index] % TraceCodeAlphabet.Length];
        }

        return new string(chars);
    }

    private void Invoke(string identifier, params object?[] args)
    {
        try
        {
            if (js is IJSInProcessRuntime inProcess)
            {
                inProcess.InvokeVoid(identifier, args);
            }
            else
            {
                _ = InvokeAsync(identifier, args);
            }
        }
        catch (Exception ex)
        {
            // client-error-reporter.js 沒載入（被擋下）、執行環境正在關閉或其他任何失敗：少了一筆回報，畫面照常（回報不可以讓畫面再出錯）。
            logger.LogDebug(ex, "Browser error reporter is not available. Function={Function}", identifier);
        }
    }

    private async Task InvokeAsync(string identifier, object?[] args)
    {
        try
        {
            await js.InvokeVoidAsync(identifier, args);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Browser error reporter is not available. Function={Function}", identifier);
        }
    }
}
