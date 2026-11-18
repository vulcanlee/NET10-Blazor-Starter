using MyProject.Business.Services.Other;
using MyProject.Dtos.Shell;

namespace MyProject.Web.Diagnostics;

/// <summary>
/// 瀏覽器端的非錯誤事件（0.10.36 起）：一段連線中斷、資源載入失敗、內容安全政策（CSP）擋下的內容。
/// 只寫日誌檔（Warning／Information），<b>不帶例外物件</b> —— 系統例外紀錄只收程式缺陷，使用者端的網路問題不能把它淹沒。
///
/// 日誌檔的版面沒有帳號欄，所以帳號與 UserId 寫在訊息裡（日誌檔可以記的身分只有這兩項）。
/// 前端送來的一律不信任：路徑與網址去掉查詢字串、控制字元並截短（<see cref="BrowserReportContext"/>）。
/// </summary>
public sealed class BrowserEventReporter
{
    internal const int MaxPaths = 10;
    private const int MaxEventPathLength = 100;
    private const int MaxDirectiveLength = 100;

    private readonly ILogger<BrowserEventReporter> logger;
    private readonly CurrentUserService currentUserService;
    private readonly TimeProvider timeProvider;

    public BrowserEventReporter(ILogger<BrowserEventReporter> logger, CurrentUserService currentUserService, TimeProvider timeProvider)
    {
        this.logger = logger;
        this.currentUserService = currentUserService;
        this.timeProvider = timeProvider;
    }

    /// <summary>是不是這裡處理的事件種類（其餘由 <see cref="BrowserErrorReporter"/> 當錯誤處理）。</summary>
    public static bool Handles(string? kind) => kind is "network" or "resource" or "csp";

    /// <summary>依種類寫一筆日誌；種類不對時不寫並回 false。</summary>
    public bool Report(ClientReportDto item)
    {
        switch (item.Kind)
        {
            case "network":
                ReportNetwork(item);
                return true;
            case "resource":
                logger.LogWarning(
                    "Browser failed to load a resource. Element={Element}, Url={Url}, Page={Page}, Account={Account}, UserId={UserId}",
                    BrowserReportContext.Clean(item.Message, MaxDirectiveLength),
                    BrowserReportContext.StripQuery(item.Source),
                    BrowserReportContext.StripQuery(item.Path),
                    Account, UserId);
                return true;
            case "csp":
                logger.LogWarning(
                    "Browser blocked content by the security policy. Directive={Directive}, BlockedUrl={BlockedUrl}, Page={Page}, Account={Account}, UserId={UserId}",
                    BrowserReportContext.Clean(item.Message, MaxDirectiveLength),
                    BrowserReportContext.StripQuery(item.Source),
                    BrowserReportContext.StripQuery(item.Path),
                    Account, UserId);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// 一段連線中斷記一筆：瀏覽器一直有網路卻連不上伺服器 → Warning（伺服器、網路設備或中間的代理出問題，維運要看一眼）；
    /// 瀏覽器自己離線 → Information（使用者端的網路，系統沒有問題）。
    /// </summary>
    private void ReportNetwork(ClientReportDto item)
    {
        var paths = string.Join(", ", (item.Paths ?? [])
            .Select(path => BrowserReportContext.StripQuery(path))
            .Select(path => path.Length <= MaxEventPathLength ? path : path[..MaxEventPathLength])
            .Distinct(StringComparer.Ordinal)
            .Take(MaxPaths));

        var failedCalls = Math.Max(0, item.FailedCalls ?? 0);
        if (item.BrowserOnline == false)
        {
            logger.LogInformation(
                "Browser was offline. StartedAt={StartedAt}, LastFailureAt={LastFailureAt}, RecoveredAt={RecoveredAt}, FailedCalls={FailedCalls}, Paths={Paths}, Account={Account}, UserId={UserId}",
                Local(item.StartedAt), Local(item.LastFailureAt), Local(item.RecoveredAt), failedCalls, paths, Account, UserId);
        }
        else
        {
            logger.LogWarning(
                "Browser could not reach the server. StartedAt={StartedAt}, LastFailureAt={LastFailureAt}, RecoveredAt={RecoveredAt}, FailedCalls={FailedCalls}, Paths={Paths}, Account={Account}, UserId={UserId}",
                Local(item.StartedAt), Local(item.LastFailureAt), Local(item.RecoveredAt), failedCalls, paths, Account, UserId);
        }
    }

    private string? Account => currentUserService.CurrentUser.Id > 0 ? currentUserService.CurrentUser.Account : null;

    private int? UserId => currentUserService.CurrentUser.Id > 0 ? currentUserService.CurrentUser.Id : null;

    private string Local(DateTimeOffset? value)
        => value is { } time ? TimeZoneInfo.ConvertTime(time, timeProvider.LocalTimeZone).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) : "(unknown)";
}
