using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MyProject.Web.Client.Api;

namespace MyProject.Web.Client.Commons;

/// <summary>使用者選的一個檔案：內容留在瀏覽器（<c>browser-files.js</c>），.NET 只知道 id、檔名與大小。</summary>
public sealed record PickedFile(string Id, string Name, long Size);

/// <summary>multipart 請求的原始回應（狀態碼與本文），交給 <see cref="Api.ApiClient.SendFormAsync{T}"/> 解析。</summary>
public sealed record RawResponse(int Status, string Body);

/// <summary>
/// 附件上傳與下載（0.10.3 起，WASM 遷移 M4）。檔案不經過 WASM 的記憶體：選檔時只拿 id／檔名／大小，
/// 存檔時由 JS 組 FormData 直接送出；下載以一次性票證網址交給瀏覽器原生下載。見 <c>wwwroot/js/browser-files.js</c>。
/// ⚠️ 這兩個請求不經過 HttpClient：網址一律在這裡經 <see cref="ApiAddress"/> 換成 API 主機的完整網址（0.10.10 起，雙站台時 API 在另一個網址）。
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "BL0016:JS interop call is not guarded with a try/catch block", Justification = "JS 互通的薄包裝：例外刻意交給呼叫端處理，在這裡吞掉反而讓呼叫端無從得知失敗（.NET 11 起的分析器）。")]
public class BrowserFiles : IAsyncDisposable
{
    /// <summary>WASM 前端專案的 wwwroot 直接掛在網站根目錄（同 <c>TokenStore</c>）。</summary>
    private const string ModulePath = "./js/browser-files.js";
    private readonly IJSRuntime jsRuntime;
    private readonly ApiAddress apiAddress;
    private IJSObjectReference? module;

    public BrowserFiles(IJSRuntime jsRuntime, ApiAddress apiAddress)
    {
        this.jsRuntime = jsRuntime;
        this.apiAddress = apiAddress;
    }

    /// <summary>收下 <c>&lt;input type=file&gt;</c> 這次選的檔案。</summary>
    public virtual async Task<IReadOnlyList<PickedFile>> CaptureAsync(ElementReference input)
        => await (await ModuleAsync()).InvokeAsync<List<PickedFile>>("capture", input);

    /// <summary>不再需要的檔案（移除、關窗、存檔完成）。</summary>
    public virtual async Task ReleaseAsync(IEnumerable<string> ids)
        => await (await ModuleAsync()).InvokeVoidAsync("release", ids.ToArray());

    /// <summary>送出 multipart（<c>data</c>＋<c>files</c>＋<c>removedFileIds</c>）。連不上伺服器時丟 <see cref="JSException"/>。</summary>
    public virtual async Task<RawResponse> SendAsync(string method, string url, string? token, string data, IEnumerable<string> fileIds, IEnumerable<int> removedFileIds)
        => await (await ModuleAsync()).InvokeAsync<RawResponse>("send", method, apiAddress.Absolute(url).AbsoluteUri, token, data, fileIds.ToArray(), removedFileIds.ToArray());

    /// <summary>
    /// 讓瀏覽器以這個網址（伺服器給的路徑）下載。成功回 null；失敗回要顯示給使用者的訊息（0.10.10 起）。
    /// 同一個網址：原生下載（&lt;a download&gt;）；跨來源（雙站台）：瀏覽器會忽略 download 屬性而整頁導過去，改由 JS 以 fetch 取回再存檔。
    /// </summary>
    public virtual async Task<string?> DownloadAsync(string url)
        => await (await ModuleAsync()).InvokeAsync<string?>("download", apiAddress.Absolute(url).AbsoluteUri);

    public async ValueTask DisposeAsync()
    {
        if (module is not null)
        {
            try
            {
                await module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // 頁面已關閉，模組隨之釋放。
            }
        }

        GC.SuppressFinalize(this);
    }

    private async Task<IJSObjectReference> ModuleAsync()
        => module ??= await jsRuntime.InvokeAsync<IJSObjectReference>("import", ModulePath);
}
