using Microsoft.Extensions.Options;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Web.Configuration;

namespace MyProject.Web.Diagnostics;

/// <summary>
/// 接收瀏覽器端錯誤（0.9.79 起，LOG-20）：WASM 的 <c>ClientErrorReporter</c> 送到 <c>api/v1/shell/client-errors</c>，由 <c>ShellController</c> 呼叫 <see cref="Report"/>。
///
/// Scoped（＝每個請求一份）。每人每分鐘的上限由 <c>ShellController</c> 控管（<c>PerUserRateLimiter</c>）；
/// 0.10.9 以前這裡另有給 Blazor Server 連線用的 <c>[JSInvokable]</c> 與每個實例的計數，0.10.10 起移除。
/// 只有已登入的人能回報；帳號取自 <see cref="CurrentUserService"/>（<c>ApiActorMiddleware</c> 已載入）。
///
/// 前端送來的東西一律不信任：長度在這裡再截一次、頁面只取路徑並截短。
/// 以 Error 記錄並帶 <see cref="BrowserScriptException"/>，進系統例外紀錄（來源「瀏覽器」）。
/// </summary>
public sealed class BrowserErrorReporter
{
    private const int MaxMessageLength = 1000;
    private const int MaxStackLength = 4000;
    private const int MaxSourceLength = 500;
    private const int MaxPathLength = 200;

    private readonly ILogger<BrowserErrorReporter> logger;
    private readonly ExceptionContextAccessor contextAccessor;
    private readonly CurrentUserService currentUserService;
    private readonly IOptionsMonitor<ClientErrorReportingSettings> options;

    public BrowserErrorReporter(
        ILogger<BrowserErrorReporter> logger,
        ExceptionContextAccessor contextAccessor,
        CurrentUserService currentUserService,
        IOptionsMonitor<ClientErrorReportingSettings> options)
    {
        this.logger = logger;
        this.contextAccessor = contextAccessor;
        this.currentUserService = currentUserService;
        this.options = options;
    }

    public bool IsEnabled => options.CurrentValue.Enabled;

    /// <returns>是否記錄了（關閉回報時為 false）。</returns>
    public bool Report(string? kind, string? message, string? stack, string? source, string? path)
    {
        var settings = options.CurrentValue;
        if (settings.Enabled == false)
        {
            return false;
        }

        var safeKind = kind is "unhandledrejection" ? "unhandledrejection" : "error";
        var exception = new BrowserScriptException(
            safeKind,
            Truncate(string.IsNullOrWhiteSpace(message) ? "(no message)" : message, MaxMessageLength),
            Truncate(stack, MaxStackLength),
            Truncate(source, MaxSourceLength));

        var user = currentUserService.CurrentUser;
        var previous = contextAccessor.Current;
        try
        {
            contextAccessor.Set(new ExceptionContext(
                ExceptionSources.Browser,
                NormalizePath(path),
                user.Id > 0 ? user.Account : null,
                user.Id > 0 ? user.Id : null));

            // 樣板固定，簽章才不會因參數不同而失控；訊息差異已在例外本身。
            logger.LogError(exception, "Browser script error. Kind={Kind}", safeKind);
        }
        finally
        {
            if (previous is null)
            {
                contextAccessor.Clear();
            }
            else
            {
                contextAccessor.Set(previous);
            }
        }

        return true;
    }

    /// <summary>只留路徑：去掉查詢字串與片段（可能含個資或 token），確保以 / 開頭，並截短。</summary>
    internal static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var cut = path.IndexOfAny(['?', '#']);
        var value = (cut >= 0 ? path[..cut] : path).Trim();
        if (value.StartsWith('/') == false)
        {
            value = "/" + value;
        }

        return Truncate(value, MaxPathLength);
    }

    private static string Truncate(string? value, int maxLength)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Length <= maxLength ? value : value[..maxLength];
}
