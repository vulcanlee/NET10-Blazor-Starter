using Microsoft.JSInterop;

namespace MyProject.Web.Components.Commons;

/// <summary>
/// 交給瀏覽器下載一個檔案（0.10.2 起從 <c>TabularExport.DownloadAsync</c> 抽出）：
/// 走 <c>appFileDownload.downloadFromStream</c>（與 PDF 匯出同一條路）。
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "BL0016:JS interop call is not guarded with a try/catch block", Justification = "JS 互通的薄包裝：例外刻意交給呼叫端處理，在這裡吞掉反而讓呼叫端無從得知失敗（.NET 11 起的分析器）。")]
public static class FileDownload
{
    public static async Task DownloadFileAsync(this IJSRuntime js, string fileName, byte[] content, string contentType)
    {
        using var stream = new MemoryStream(content);
        using var streamReference = new DotNetStreamReference(stream);
        await js.InvokeVoidAsync("appFileDownload.downloadFromStream", fileName, streamReference, contentType);
    }
}
