using System.Globalization;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Shell;

namespace MyProject.Web.Client.Commons;

/// <summary>
/// WASM 頁面的語系（0.10.2 起，WASM 遷移 M3）：與伺服器相同，一律 zh-TW（不跟著瀏覽器語言走），
/// 日期時間格式再套用伺服器的那一組（<c>GET /api/v1/shell/culture</c>）——瀏覽器內建的地區資料與伺服器不同，
/// 例如同樣是 zh-TW，瀏覽器的時間是「上午9:00:00」、伺服器是「上午 09:00:00」。
/// ⚠️ 實測（M3）：即使在 <c>RunAsync</c> 之前設定，WASM 畫面渲染時的 <c>CultureInfo.CurrentCulture</c> 仍是瀏覽器內建的 zh-TW，
/// 只有預設值（<c>DefaultThreadCurrentCulture</c>）換得掉。所以清單上的時間欄位一律明確指定 <see cref="DateTimeFormat"/>
/// （<c>&lt;Column … Format="@ClientCulture.DateTimeFormat" /&gt;</c>），不依賴執行緒的語系。
/// </summary>
public static class ClientCulture
{
    /// <summary>
    /// 日期選擇器的值當成「伺服器的本地時間」送出（0.10.4 起）：去掉時區標記，序列化時就不會帶瀏覽器的時差，
    /// 伺服器收到後照自己的時區解讀，與 Server 版相同。
    /// </summary>
    public static DateTime? Unzoned(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Unspecified) : null;

    public const string Name = "zh-TW";

    /// <summary>
    /// 清單上日期時間欄位的格式（伺服器的「簡短日期 + 完整時間」，等同 Server 版 <c>DateTime.ToString()</c>）；
    /// 啟動時由 <see cref="LoadAsync"/> 依伺服器設定，讀不到時是瀏覽器 zh-TW 的對應格式。
    /// </summary>
    public static string DateTimeFormat { get; private set; } = "yyyy/M/d tt hh:mm:ss";

    /// <summary>
    /// 啟動時（<c>RunAsync</c> 之前）讀伺服器的日期時間格式並套用；連不上或回應異常時用 zh-TW 本身的格式，不擋住啟動。
    /// </summary>
    public static async Task LoadAsync(HttpClient http, ILogger logger)
    {
        DateTimeFormatDto? format = null;
        try
        {
            format = (await http.GetFromJsonAsync<ApiResult<DateTimeFormatDto>>("api/v1/shell/culture"))?.Data;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            // 斷線不卡死：沿用瀏覽器內建的 zh-TW 格式。
            logger.LogWarning(ex, "Failed to load the server date/time format; using the browser's zh-TW format.");
        }

        var culture = Build(format);
        DateTimeFormat = DisplayFormatOf(culture);
        Use(culture);
    }

    /// <summary>「簡短日期 + 完整時間」（<c>DateTime.ToString()</c> 的預設格式 "G"）。</summary>
    internal static string DisplayFormatOf(CultureInfo culture)
        => $"{culture.DateTimeFormat.ShortDatePattern} {culture.DateTimeFormat.LongTimePattern}";

    /// <summary>zh-TW 加上伺服器的日期時間格式（不改目前執行緒的語系，可直接測試）。</summary>
    internal static CultureInfo Build(DateTimeFormatDto? format)
    {
        var culture = (CultureInfo)new CultureInfo(Name).Clone();
        if (format is not null)
        {
            var target = culture.DateTimeFormat;
            target.ShortDatePattern = Pick(format.ShortDatePattern, target.ShortDatePattern);
            target.LongDatePattern = Pick(format.LongDatePattern, target.LongDatePattern);
            target.ShortTimePattern = Pick(format.ShortTimePattern, target.ShortTimePattern);
            target.LongTimePattern = Pick(format.LongTimePattern, target.LongTimePattern);
            target.AMDesignator = Pick(format.AMDesignator, target.AMDesignator);
            target.PMDesignator = Pick(format.PMDesignator, target.PMDesignator);
        }

        return culture;
    }

    private static string Pick(string value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;

    private static void Use(CultureInfo culture)
    {
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
