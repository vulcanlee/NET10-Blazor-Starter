namespace MyProject.Business.Helpers;

/// <summary>
/// UTC 與伺服器本地時間的換算（0.10.44 起，整併自各服務與 Controller 重複的寫法）。
///
/// <para>
/// 一律以 <see cref="TimeProvider.LocalTimeZone"/> 為準（正式環境就是伺服器的時區，測試可以換成指定時區）。
/// 資料庫讀回來的時間 Kind 多半是 Unspecified，所以換算前先把 Kind 釘好。
/// </para>
/// <para>
/// 送給 WASM 的時間一律「換成伺服器本地時間、不帶時差」（<see cref="ToLocalUnzoned(TimeProvider, DateTime)"/>／<see cref="Unzoned(DateTime)"/>）：
/// 帶 Local 或 Utc 的 Kind 序列化會加上時差，瀏覽器就會換成使用者電腦的時區。
/// </para>
/// </summary>
public static class LocalTime
{
    /// <summary>UTC → 本地時間（Kind 不論，一律當作 UTC）。</summary>
    public static DateTime ToLocal(this TimeProvider timeProvider, DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeProvider.LocalTimeZone);

    /// <summary>本地時間 → UTC（Kind 不論，一律當作本地時間）。</summary>
    public static DateTime ToUtc(this TimeProvider timeProvider, DateTime local)
        => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), timeProvider.LocalTimeZone);

    /// <summary>UTC → 本地時間，不帶時差（送給 WASM 用）。</summary>
    public static DateTime ToLocalUnzoned(this TimeProvider timeProvider, DateTime utc)
        => Unzoned(timeProvider.ToLocal(utc));

    /// <summary>同 <see cref="ToLocalUnzoned(TimeProvider, DateTime)"/>；null 照樣是 null。</summary>
    public static DateTime? ToLocalUnzoned(this TimeProvider timeProvider, DateTime? utc)
        => utc is { } value ? timeProvider.ToLocalUnzoned(value) : null;

    /// <summary>某個時間點 → 本地時間，不帶時差（送給 WASM 用）。</summary>
    public static DateTime ToLocalUnzoned(this TimeProvider timeProvider, DateTimeOffset value)
        => Unzoned(TimeZoneInfo.ConvertTime(value, timeProvider.LocalTimeZone).DateTime);

    /// <summary>已經是本地時間的值，只拿掉 Kind（不帶時差送出）。</summary>
    public static DateTime Unzoned(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

    /// <summary>同 <see cref="Unzoned(DateTime)"/>；null 照樣是 null。</summary>
    public static DateTime? Unzoned(DateTime? value) => value is { } v ? Unzoned(v) : null;
}
