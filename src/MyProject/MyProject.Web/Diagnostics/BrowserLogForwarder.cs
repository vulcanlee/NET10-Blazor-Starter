using System.Globalization;
using System.Text.RegularExpressions;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Shell;

namespace MyProject.Web.Diagnostics;

/// <summary>
/// 前端診斷記錄的日誌（0.10.37 起）：把那位使用者瀏覽器送來的日誌依<b>原等級</b>寫進日誌檔，
/// 分類是 <c>MyProject.Browser.</c> 加上前端分類去掉 <c>MyProject.</c>（例如 <c>MyProject.Browser.Web.Client.Api.ApiClient</c>），
/// 在 /logs 可以依 Logger 篩選。nlog.config 為 <c>MyProject.Browser.*</c> 另設規則（Trace 以上、final），
/// 所以不受「日誌等級設定」的 <c>*</c> 規則影響，也不會重複寫一次。
///
/// 不帶例外物件（只把例外型別寫在訊息裡），不會進系統例外紀錄 —— 錯誤本身已經由錯誤回報（LOG-23）記過。
/// 前端送來的一律不信任：分類只接受英數字與點、訊息去掉控制字元並截短、低於這次診斷等級的捨棄。
/// </summary>
public sealed partial class BrowserLogForwarder
{
    internal const string CategoryPrefix = "MyProject.Browser.";
    internal const int MaxMessageLength = 2000;

    private readonly ILoggerFactory loggerFactory;
    private readonly CurrentUserService currentUserService;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<BrowserLogForwarder> logger;

    public BrowserLogForwarder(ILoggerFactory loggerFactory, CurrentUserService currentUserService, TimeProvider timeProvider, ILogger<BrowserLogForwarder> logger)
    {
        this.loggerFactory = loggerFactory;
        this.currentUserService = currentUserService;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <returns>寫了幾筆。</returns>
    public int Forward(ClientLogBatchDto batch, LogLevel minimumLevel, int maxItems)
    {
        var user = currentUserService.CurrentUser;
        if (batch.Dropped > 0)
        {
            logger.LogWarning("Browser diagnostic log buffer overflowed. Dropped={Dropped}, Account={Account}, UserId={UserId}", batch.Dropped, user.Account, user.Id);
        }

        var written = 0;
        foreach (var entry in batch.Items.Take(maxItems))
        {
            if (!Enum.TryParse<LogLevel>(entry.Level, ignoreCase: true, out var level) || level is LogLevel.None || level < minimumLevel)
            {
                continue;
            }

            var target = loggerFactory.CreateLogger(CategoryOf(entry.Category));
            target.Log(level,
                "Browser log: {BrowserMessage} (ExceptionType={ExceptionType}, Page={Page}, ClientTime={ClientTime}, Account={Account}, UserId={UserId})",
                BrowserReportContext.Clean(entry.Message, MaxMessageLength),
                BrowserReportContext.Clean(entry.ExceptionType, 200),
                BrowserReportContext.StripQuery(entry.Path),
                entry.ClientTime is { } time ? TimeZoneInfo.ConvertTime(time, timeProvider.LocalTimeZone).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) : string.Empty,
                user.Account,
                user.Id);
            written++;
        }

        return written;
    }

    /// <summary>前端分類換成 <c>MyProject.Browser.*</c>；不是 <c>MyProject.</c> 開頭或含其他字元的一律歸到 <c>MyProject.Browser.Other</c>（分類會被快取，不能任由前端產生）。</summary>
    internal static string CategoryOf(string? category)
        => category is not null && ValidCategory().IsMatch(category)
            ? CategoryPrefix + category["MyProject.".Length..]
            : CategoryPrefix + "Other";

    [GeneratedRegex(@"^MyProject\.[A-Za-z0-9_.]{1,150}$")]
    private static partial Regex ValidCategory();
}
