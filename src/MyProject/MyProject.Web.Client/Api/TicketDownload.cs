using MyProject.Dtos.Commons;
using MyProject.Models.Systems;
using MyProject.Web.Client.Commons;

namespace MyProject.Web.Client.Api;

/// <summary>
/// 一次性票證下載（0.10.45 起，整併自系統備份與專案附件兩份相同的寫法）：先向 <c>ticketUrl</c> 申請票證，
/// 再讓瀏覽器以 <c>&lt;a download&gt;</c> 原生下載（大檔不經過 WASM 記憶體；票證只能用一次，所以不支援續傳）。
/// </summary>
public static class TicketDownload
{
    public static async Task<VerifyRecordResult> DownloadByTicketAsync(this ApiClient api, BrowserFiles files, string ticketUrl)
    {
        var ticket = await api.PostAsync<DownloadTicketDto>(ticketUrl);
        if (!ticket.Success || ticket.Data is null)
        {
            return ticket.ToVerify();
        }

        // 0.10.10 起：跨來源下載失敗時（票證失效、權限已失去）回伺服器的訊息，頁面留在原地。
        var error = await files.DownloadAsync(ticket.Data.Url);
        return error is null ? new VerifyRecordResult { Success = true } : new VerifyRecordResult { Success = false, Message = error };
    }
}
