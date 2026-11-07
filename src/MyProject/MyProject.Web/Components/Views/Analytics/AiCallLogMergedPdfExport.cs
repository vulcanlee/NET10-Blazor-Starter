using AntDesign;
using Microsoft.JSInterop;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Web.Ai;
using MyProject.Web.Components.Commons;
using MyProject.Web.Diagnostics;
using MyProject.Web.Export;

namespace MyProject.Web.Components.Views.Analytics;

/// <summary>合併匯出 PDF 需要的服務（清單頁與明細窗各自注入後交過來）。</summary>
internal sealed record AiCallLogMergedPdfServices(
    AiCallLogService CallLogs,
    IAuditLogService Audit,
    CurrentUserService CurrentUser,
    ISystemIdentity SystemIdentity,
    IJSRuntime Js,
    NotificationService Notifications,
    ILogger Logger);

/// <summary>
/// 多筆 AI 對話紀錄合併成一份 PDF（0.9.169 起）。三個入口共用：清單勾選、明細窗「整段對話」、目前查詢結果。
/// 稽核沿用 <see cref="AuditActions.AiCallLog.Export"/>，只記來源與筆數，絕不記內容。
/// </summary>
internal static class AiCallLogMergedPdfExport
{
    public const string SourceSelected = "selected";
    public const string SourceConversation = "conversation";
    public const string SourceQuery = "query";

    /// <summary>筆數超過上限時的提示（畫面先擋，產生器的例外只是保險）。</summary>
    public static string TooManyMessage(int count, string advice)
        => $"共 {count:N0} 筆，合併匯出一次最多 {AiCallLogPdfBuilder.MaxMergedRecords} 筆；{advice}";

    /// <summary>匯出並下載；回傳是否成功。0 筆或超過上限時只提示、不匯出。</summary>
    public static async Task<bool> ExportAsync(AiCallLogMergedPdfServices services, IReadOnlyCollection<int> ids, string source)
    {
        if (ids.Count == 0)
        {
            return false;
        }

        if (ids.Count > AiCallLogPdfBuilder.MaxMergedRecords)
        {
            ViewNotification.Caution(services.Notifications, TooManyMessage(ids.Count, "請縮小範圍。"));
            return false;
        }

        try
        {
            var items = await services.CallLogs.GetExportItemsAsync(ids);
            if (items.Count == 0)
            {
                ViewNotification.Caution(services.Notifications, "找不到要匯出的紀錄（可能已被清除），請重新整理。");
                return false;
            }

            var identity = services.SystemIdentity;
            var account = services.CurrentUser.CurrentUser.Account ?? string.Empty;
            var generatedAt = DateTime.Now;
            var request = new AiCallLogMergedPdfRequest
            {
                SystemName = identity.Name,
                SystemVersion = identity.Version,
                OperatorAccount = account,
                GeneratedAt = generatedAt,
                Items = items.Select(item => new AiCallLogPdfRequest
                {
                    Item = item.Item,
                    Messages = item.Messages,
                    ResponseText = item.ResponseText,
                    Usage = item.Usage,
                    ContentMissing = item.ContentMissing,
                }).ToList(),
            };

            // 排版是 CPU 密集的同步工作，放到背景執行緒，畫面才不會卡住（同單筆匯出）。
            var bytes = await Task.Run(() => AiCallLogPdfBuilder.BuildMerged(request));
            var fileName = $"MyProject.Web-ai-calls-{items.Count}-{generatedAt:yyyyMMdd-HHmmss}.pdf";
            await services.Js.DownloadAsync(fileName, bytes, "application/pdf");

            services.Logger.LogInformation("AI call logs merged PDF exported. Source={Source}, Rows={Rows}, Bytes={Bytes}", source, items.Count, bytes.Length);
            await WriteAuditAsync(services, source, items.Select(x => x.Item.Id).ToList());
            return true;
        }
        catch (JSDisconnectedException ex)
        {
            services.Logger.LogWarning(ex, "AI call logs merged PDF export aborted because the circuit disconnected.");
            return false;
        }
        catch (Exception ex)
        {
            services.Logger.LogError(ex, "AI call logs merged PDF export failed. Source={Source}, Rows={Rows}", source, ids.Count);
            ViewNotification.UnexpectedError(services.Notifications, $"PDF 匯出失敗：{ex.GetType().Name}。", ex);
            return false;
        }
    }

    /// <summary>摘要只記來源與筆數；匯出了哪幾筆（紀錄 Id）放進稽核的詳細內容檔，事後查得到，但絕不記內容。</summary>
    private static async Task WriteAuditAsync(AiCallLogMergedPdfServices services, string source, IReadOnlyList<int> ids)
    {
        try
        {
            var currentUser = services.CurrentUser.CurrentUser;
            await services.Audit.WriteAsync(
                AuditActions.AiCallLog.Export,
                success: true,
                actorUserId: currentUser.Id,
                actorAccount: currentUser.Account,
                targetType: "AiCallLog",
                targetId: "*",
                detail: $"format=pdf-merged; source={source}; rows={ids.Count}",
                content: $"ids={string.Join(",", ids)}");
        }
        catch (Exception ex)
        {
            services.Logger.LogError(ex, "Failed to write audit log for AI call log merged PDF export.");
        }
    }
}
