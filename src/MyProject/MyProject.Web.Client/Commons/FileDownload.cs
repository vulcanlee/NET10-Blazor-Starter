using Microsoft.JSInterop;

namespace MyProject.Web.Components.Commons;

/// <summary>
/// 交給瀏覽器下載一個檔案（0.10.2 起從 <c>TabularExport.DownloadAsync</c> 抽出）：
/// 走 <c>appFileDownload.downloadFromStream</c>（與 PDF 匯出同一條路）。
/// </summary>
public static class FileDownload
{
    public static async Task DownloadFileAsync(this IJSRuntime js, string fileName, byte[] content, string contentType)
    {
        using var stream = new MemoryStream(content);
        using var streamReference = new DotNetStreamReference(stream);
        await js.InvokeVoidAsync("appFileDownload.downloadFromStream", fileName, streamReference, contentType);
    }
}
