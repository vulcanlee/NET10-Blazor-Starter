namespace MyProject.Web.Client.Api;

/// <summary>
/// API 主機的網址（0.10.10 起，WASM 遷移 M11）：<c>wwwroot/appsettings.json</c> 的 <c>ApiBaseUrl</c>。
/// 留空＝與網頁同一個網址（同站台）；有值＝獨立的 API 主機（雙站台，例如 <c>https://api.公司.com/</c>）。
/// <para>所有連到伺服器的網址都從這裡來：呼叫 API 的 HttpClient（<see cref="CreateHttpClient"/>）、
/// 整頁導到伺服器端點的網址（<see cref="GoogleLogin"/>）、交給瀏覽器直接送出的上傳與下載網址（<see cref="Absolute"/>）。
/// ⚠️ 不要在畫面裡寫死伺服器端點的路徑（ServerUrlConventionTests 守門）：同站台的開發機上看不出錯，雙站台時會導到前端的靜態站。</para>
/// </summary>
public sealed class ApiAddress
{
    public const string SettingKey = "ApiBaseUrl";

    private ApiAddress(Uri baseAddress) => BaseAddress = baseAddress;

    /// <summary>API 主機的網址（結尾一定有斜線，相對路徑 <c>api/v1/…</c> 才會接在它後面）。</summary>
    public Uri BaseAddress { get; }

    /// <param name="configured">設定的 <c>ApiBaseUrl</c>。</param>
    /// <param name="hostBaseAddress">網頁本身的網址（<c>WebAssemblyHostEnvironment.BaseAddress</c>）。</param>
    /// <exception cref="InvalidOperationException">設定的值不是完整的 http(s) 網址（部署設定寫錯，啟動時就失敗並在瀏覽器主控台說明）。</exception>
    public static ApiAddress Resolve(string? configured, string hostBaseAddress)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return new ApiAddress(new Uri(hostBaseAddress));
        }

        var value = configured.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
        {
            throw new InvalidOperationException(
                $"wwwroot/appsettings.json 的 {SettingKey} 必須是以 http:// 或 https:// 開頭的完整網址、不帶 ? 或 #（例如 https://api.example.com/），目前是「{configured}」；與網頁同一個網址請留空。");
        }

        return new ApiAddress(value.EndsWith('/') ? uri : new Uri(value + "/"));
    }

    /// <summary>呼叫 API 用的 HttpClient（相對路徑 <c>api/v1/…</c> 接在 <see cref="BaseAddress"/> 後面）。</summary>
    public HttpClient CreateHttpClient() => new() { BaseAddress = BaseAddress };

    /// <summary>伺服器給的路徑（例如下載票證的 <c>/api/v1/downloads/…</c>）或相對路徑換成 API 主機的完整網址。</summary>
    public Uri Absolute(string url) => new(BaseAddress, url);

    /// <summary>Google 登入的起點（整頁導到 API 主機，回呼後由主機導回前端的登入頁）。</summary>
    public string GoogleLogin(string? returnUrl, string binding)
    {
        var query = returnUrl is null
            ? $"binding={binding}"
            : $"returnUrl={Uri.EscapeDataString(returnUrl)}&binding={binding}";
        return Absolute("Auths/Google/Login?" + query).AbsoluteUri;
    }
}
