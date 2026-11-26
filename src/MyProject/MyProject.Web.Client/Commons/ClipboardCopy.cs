using AntDesign;
using Microsoft.JSInterop;

namespace MyProject.Web.Components.Commons;

/// <summary>
/// 複製文字到剪貼簿並顯示結果（0.10.45 起，整併自各頁重複的寫法）。
/// 走 <c>appClipboard.copyText</c>：https 用 navigator.clipboard，http 內網退回 execCommand，兩種部署都能用。
/// 成功顯示 <c>copiedMessage</c>、瀏覽器拒絕顯示 <see cref="RefusedMessage"/>、發生例外以 <see cref="ViewNotification.UnexpectedError"/> 顯示 <c>failedMessage</c>；
/// 日誌由呼叫端依結果自己寫（各頁的訊息不同）。
/// </summary>
public static class ClipboardCopy
{
    public const string RefusedMessage = "瀏覽器拒絕存取剪貼簿，請手動選取複製。";

    public static async Task<CopyResult> CopyTextAsync(
        this IJSRuntime js, string text, NotificationService notifications, string copiedMessage, string failedMessage)
    {
        try
        {
            if (await js.InvokeAsync<bool>("appClipboard.copyText", text))
            {
                ViewNotification.Info(notifications, copiedMessage);
                return CopyResult.Done;
            }

            ViewNotification.Warning(notifications, RefusedMessage);
            return CopyResult.Refused;
        }
        catch (Exception ex)
        {
            ViewNotification.UnexpectedError(notifications, failedMessage, ex);
            return new CopyResult(false, ex);
        }
    }
}

/// <summary><see cref="ClipboardCopy.CopyTextAsync"/> 的結果：複製成功（<see cref="Copied"/>）、瀏覽器拒絕、或發生例外（<see cref="Error"/>）。</summary>
public sealed record CopyResult(bool Copied, Exception? Error)
{
    public static readonly CopyResult Done = new(true, null);

    public static readonly CopyResult Refused = new(false, null);
}
