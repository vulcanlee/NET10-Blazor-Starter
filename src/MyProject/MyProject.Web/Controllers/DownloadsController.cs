using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Commons;
using MyProject.Share.Helpers;
using MyProject.Web.Backup;
using MyProject.Web.Downloads;

namespace MyProject.Web.Controllers;

/// <summary>
/// 以一次性票證下載大檔（0.10.3 起，WASM 遷移 M4；取代只收 Cookie 的 <c>api/project-files/{id}/download</c>；
/// 0.10.6 起也下載系統備份，取代只收 Cookie 的 <c>api/backups/{檔名}/download</c>）。
///
/// 端點本身不需要登入（瀏覽器原生下載帶不了 Token），安全性來自票證：不可猜、60 秒內有效、只能用一次。
/// 兌換時以票證上的使用者**重新**檢查一次：帳號存在、啟用、工作階段版本與申請時相同（改密碼、強制登出、停用後立即失效）、
/// 不在強制改密碼或設定兩步驟驗證期間、仍有檢視權限（備份：仍是管理員），再交給資料的服務檢查團隊範圍與檔案。
/// 任何一關不過都回同一個 404，不透露原因（原因各記一筆日誌）。
/// </summary>
[Route("api/v1/downloads")]
[ApiController]
[AllowAnonymous]
public class DownloadsController : ControllerBase
{
    private readonly ILogger<DownloadsController> logger;
    private readonly DownloadTicketService tickets;
    private readonly ICurrentUserLoader currentUserLoader;
    private readonly CurrentUserService currentUserService;
    private readonly ProjectService projectService;
    private readonly IAuditLogService auditLogService;
    private readonly BackupStore backupStore;

    public DownloadsController(
        ILogger<DownloadsController> logger,
        DownloadTicketService tickets,
        ICurrentUserLoader currentUserLoader,
        CurrentUserService currentUserService,
        ProjectService projectService,
        IAuditLogService auditLogService,
        BackupStore backupStore)
    {
        this.logger = logger;
        this.tickets = tickets;
        this.currentUserLoader = currentUserLoader;
        this.currentUserService = currentUserService;
        this.projectService = projectService;
        this.auditLogService = auditLogService;
        this.backupStore = backupStore;
    }

    [HttpGet("{ticket}")]
    public async Task<IActionResult> Download(string ticket)
    {
        var redeemed = tickets.TryRedeem(ticket);
        if (redeemed is null)
        {
            logger.LogInformation("Download refused because the ticket is unknown, used or expired.");
            return Gone();
        }

        var loaded = await currentUserLoader.LoadAsync(redeemed.UserId, redeemed.SecurityStamp);
        if (loaded.Status != CurrentUserLoadStatus.Ok || loaded.CurrentUser is null || loaded.MustChangePassword || loaded.RequiresTwoFactorSetup)
        {
            logger.LogInformation(
                "Download refused because the requesting session is no longer valid. UserId={UserId}, Status={Status}",
                redeemed.UserId, loaded.Status);
            return Gone();
        }

        // 讓後面的服務（團隊範圍、稽核）以申請者的身分執行，規則與一般 API 請求相同。
        CurrentUserLoader.ApplyTo(currentUserService, loaded.CurrentUser);

        return redeemed.Kind switch
        {
            DownloadTicketService.ProjectFileKind when int.TryParse(redeemed.Target, out var projectFileId) => await ProjectFileAsync(projectFileId),
            DownloadTicketService.BackupKind => await BackupAsync(redeemed.Target),
            _ => Gone(),
        };
    }

    private async Task<IActionResult> ProjectFileAsync(int projectFileId)
    {
        var user = currentUserService.CurrentUser;
        if (!PermissionRules.CanAccessAction(user, MagicObjectHelper.角色_專案項目, PermissionActions.View))
        {
            logger.LogInformation("Project file download refused because the user lost the view permission. ProjectFileId={ProjectFileId}, UserId={UserId}", projectFileId, user.Id);
            return Gone();
        }

        // 查無紀錄、專案已刪除、團隊範圍外、實體檔不存在，服務都回 null（原因已由服務各記一筆）。
        var file = await projectService.GetFileDownloadAsync(projectFileId);
        if (file is null)
        {
            return Gone();
        }

        // 附件可能敏感：成功下載留長期軌跡（日誌只保留 30 天）；稽核寫入失敗不擋下載（AuditLogService 不拋出）。
        await auditLogService.WriteAsync(
            AuditActions.Project.FileDownload,
            success: true,
            actorUserId: user.Id,
            actorAccount: user.Account,
            targetType: "ProjectFile",
            targetId: projectFileId.ToString(),
            detail: $"file={file.DownloadFileName}");
        logger.LogInformation("Project file downloaded. ProjectFileId={ProjectFileId}, UserId={UserId}", projectFileId, user.Id);

        // 成功回原生檔案串流（Content-Disposition: attachment），錯誤才回 ApiResult。票證只能用一次，所以不提供續傳。
        return File(file.Content, file.ContentType, file.DownloadFileName);
    }

    /// <summary>
    /// 系統備份（0.10.6 起）：備份含全部資料與金鑰環，兌換時再從資料庫確認仍是管理員；檔名再經 <see cref="BackupStore.TryResolveFullPath"/>
    /// （只認備份根目錄下、檔名合格的 zip，擋路徑穿越）。稽核在這裡寫（與 Server 版相同：真正開始下載時一筆）。
    /// </summary>
    private async Task<IActionResult> BackupAsync(string fileName)
    {
        var user = currentUserService.CurrentUser;
        if (!user.IsAdmin)
        {
            logger.LogInformation("Backup download refused because the user is no longer an administrator. UserId={UserId}", user.Id);
            return Gone();
        }

        var path = backupStore.TryResolveFullPath(fileName);
        if (path is null || !System.IO.File.Exists(path))
        {
            logger.LogInformation("Backup download refused because the file is gone. FileName={FileName}, UserId={UserId}", fileName, user.Id);
            return Gone();
        }

        await auditLogService.WriteAsync(
            AuditActions.Backup.Download,
            success: true,
            actorUserId: user.Id,
            actorAccount: user.Account,
            targetType: "Backup",
            targetId: fileName,
            detail: $"file={fileName}");
        logger.LogInformation("Backup download started. FileName={FileName}, UserId={UserId}", fileName, user.Id);

        // 票證只能用一次，所以不提供續傳（與附件相同）。
        return PhysicalFile(path, "application/zip", fileName);
    }

    private NotFoundObjectResult Gone()
    {
        var body = ApiResult.NotFoundResult("下載連結無效或已過期，請回到頁面重新下載。");
        body.TraceId = HttpContext.TraceIdentifier;
        return NotFound(body);
    }
}
