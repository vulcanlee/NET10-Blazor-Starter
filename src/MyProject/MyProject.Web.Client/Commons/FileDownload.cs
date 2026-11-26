using AntDesign;
using Microsoft.JSInterop;
using MyProject.Web.Client.Api;

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

    /// <summary>
    /// 把 API 回來的檔案交給瀏覽器下載（0.10.45 起，整併自各頁重複的寫法）：
    /// 伺服器回失敗時顯示「<paramref name="failureLabel"/>：原因」（<paramref name="cautionOnBadRequest"/> 時 400 改顯示提醒，例如匯出超過上限筆數）；
    /// 交給瀏覽器時發生例外以 <see cref="ViewNotification.UnexpectedError"/> 顯示。日誌由呼叫端依結果自己寫（各頁的訊息不同）。
    /// </summary>
    public static async Task<DownloadResult> SaveDownloadAsync(
        this IJSRuntime js, ApiDownload outcome, NotificationService notifications, string failureLabel, bool cautionOnBadRequest = false)
    {
        if (!outcome.Success)
        {
            if (cautionOnBadRequest && outcome.StatusCode == 400)
            {
                ViewNotification.Caution(notifications, outcome.Message);
            }
            else
            {
                ViewNotification.Error(notifications, $"{failureLabel}：{outcome.Message}");
            }

            return DownloadResult.Rejected;
        }

        try
        {
            await js.DownloadFileAsync(outcome.FileName!, outcome.Content!, outcome.ContentType!);
            return new DownloadResult(true, outcome.Content!.Length, null);
        }
        catch (Exception ex)
        {
            ViewNotification.UnexpectedError(notifications, $"{failureLabel}：{ex.GetType().Name}。", ex);
            return new DownloadResult(false, 0, ex);
        }
    }
}

/// <summary><see cref="FileDownload.SaveDownloadAsync"/> 的結果：交給瀏覽器了（<see cref="Saved"/>）、伺服器拒絕、或交給瀏覽器時失敗（<see cref="Error"/>）。</summary>
public sealed record DownloadResult(bool Saved, int Bytes, Exception? Error)
{
    public static readonly DownloadResult Rejected = new(false, 0, null);
}
