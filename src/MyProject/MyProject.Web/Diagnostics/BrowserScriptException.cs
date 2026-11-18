namespace MyProject.Web.Diagnostics;

/// <summary>
/// 代表一個瀏覽器端的錯誤（LOG-20）：JavaScript 錯誤、未處理的 Promise 拒絕，0.10.36 起也包含前端 .NET 的例外（錯誤邊界、頁面 catch 到的）。
///
/// 系統例外紀錄只收「Error 以上且帶例外物件」的日誌，所以前端錯誤要包成例外才進得去。
/// 型別名稱固定，簽章由「訊息＋頁面＋操作」區分：同一個錯誤在同一頁重複發生會合併計次。
/// <see cref="ToString"/> 帶出瀏覽器端的堆疊與背景資訊（0.10.36 起：前端指紋、瀏覽器、操作軌跡），
/// 堆疊檔與日誌檔看到的是瀏覽器端的呼叫路徑，而不是伺服器接收端；背景資訊不在 <see cref="Exception.Message"/> 裡，所以不影響簽章。
/// </summary>
public sealed class BrowserScriptException : Exception
{
    public BrowserScriptException(string kind, string message, string? scriptStack, string? source, string? context = null)
        : base(message)
    {
        Kind = kind;
        ScriptStack = scriptStack ?? string.Empty;
        Source = source;
        Context = context ?? string.Empty;
    }

    /// <summary>錯誤種類（<c>error</c>、<c>unhandledrejection</c>、<c>boundary</c>、<c>caught</c>…，見 <c>ClientReportDto.Kind</c>）。</summary>
    public string Kind { get; }

    public string ScriptStack { get; }

    /// <summary>背景資訊（<see cref="BrowserReportContext.Format"/> 產生）；舊端點送來的沒有。</summary>
    public string Context { get; }

    public override string StackTrace => ScriptStack;

    public override string ToString()
        => $"{GetType().FullName}: {Message}{Environment.NewLine}Kind: {Kind}{Environment.NewLine}"
            + (string.IsNullOrWhiteSpace(Source) ? string.Empty : $"Source: {Source}{Environment.NewLine}")
            + ScriptStack
            + (string.IsNullOrWhiteSpace(Context) ? string.Empty : $"{Environment.NewLine}{Environment.NewLine}{Context}");
}
