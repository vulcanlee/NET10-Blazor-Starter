namespace MyProject.Web.Configuration;

/// <summary>
/// 前端（Blazor WebAssembly 靜態站台）在哪裡（0.10.10 起，WASM 遷移 M11）。
/// <para><see cref="BaseUrl"/> 留空＝同站台：這個主機一併提供前端的靜態檔，前端與 API 同一個網址。</para>
/// <para>有值＝雙站台（例如 <c>https://app.公司.com/</c>）：這個主機只提供 API，非 API 路徑回 404；
/// CORS 只允許這個來源；Google 登入導回、信中連結一律用這個網址（優先於 <c>EmailSettings:PublicBaseUrl</c>）。
/// ⚠️ 只用設定值，不採用請求的 Host 或任何參數，不可成為開放式重新導向。</para>
/// </summary>
public class ClientSettings
{
    public const string SectionName = "ClientSettings";

    /// <summary>前端網址；留空＝同站台。</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>前端是不是另一個站台（<see cref="BaseUrl"/> 有值）。</summary>
    public bool IsSeparateSite => !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>前端的來源（scheme＋主機＋連接埠，CORS 比對用）；同站台時為 null。</summary>
    public string? Origin => IsSeparateSite ? new Uri(BaseUrl.Trim()).GetLeftPart(UriPartial.Authority) : null;

    /// <summary>
    /// 前端頁面（例如 <c>/Auths/Login</c>，可帶 query 與錨點）要導去的網址：雙站台時接在 <see cref="BaseUrl"/> 後面，
    /// 同站台時照舊是本站的相對路徑。
    /// </summary>
    public string FrontendUrl(string path)
        => IsSeparateSite ? BaseUrl.Trim().TrimEnd('/') + "/" + path.TrimStart('/') : path;
}
