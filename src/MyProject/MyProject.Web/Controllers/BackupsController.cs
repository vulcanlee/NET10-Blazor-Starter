using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;
using MyProject.Share.Helpers;
using MyProject.Web.Backup;
using MyProject.Web.Components.Commons;
using MyProject.Web.Configuration;
using MyProject.Web.Downloads;
using MyProject.Web.Filters;
using MyProject.Web.Scheduling;
using MyProject.Web.Scheduling.Jobs;

namespace MyProject.Web.Controllers;

/// <summary>
/// 系統備份頁的 Web API（0.10.6 起，WASM 遷移 M7），只限管理員：清單、立即備份、刪除、申請下載票證。
/// 「立即備份」與 Server 版相同放進排程框架的佇列（與排程共用作業鎖），不在請求裡執行，所以不必長逾時；畫面每 5 秒重新讀清單看進度。
/// 下載改用一次性票證（<see cref="DownloadsController"/> 兌換），取代只收 Cookie 的 <c>api/backups/{檔名}/download</c>。
/// 稽核與 Server 版頁面相同：立即備份一筆 <c>Job.Trigger</c>、每刪一份一筆 <c>Backup.Delete</c>、下載在兌換票證時一筆 <c>Backup.Download</c>。
/// ⚠️ 檔名一律經 <see cref="BackupStore.TryResolveFullPath"/>（檔名規則＋根目錄），擋路徑穿越。
/// </summary>
[Route("api/v1/backups")]
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class BackupsController : ControllerBase
{
    private const string NotFoundMessage = "找不到這份備份，可能已被刪除。";

    private readonly BackupStore store;
    private readonly ScheduledJobOverviewService overviewService;
    private readonly IOptionsMonitor<BackupSettings> backupOptions;
    private readonly DownloadTicketService downloadTickets;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly ILogger<BackupsController> logger;

    public BackupsController(
        BackupStore store,
        ScheduledJobOverviewService overviewService,
        IOptionsMonitor<BackupSettings> backupOptions,
        DownloadTicketService downloadTickets,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        ILogger<BackupsController> logger)
    {
        this.store = store;
        this.overviewService = overviewService;
        this.backupOptions = backupOptions;
        this.downloadTickets = downloadTickets;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.logger = logger;
    }

    /// <summary>備份清單（新到舊）、備份作業的狀態與保留份數。</summary>
    [HttpGet]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<BackupListDto>>> List()
    {
        var job = (await overviewService.GetOverviewAsync()).Items.FirstOrDefault(x => x.Name == SystemBackupJob.JobName);
        return Ok(ApiResult<BackupListDto>.SuccessResult(new BackupListDto
        {
            Items = store.List().Select(ToDto).ToList(),
            Job = MaintenanceJobMapper.ToDto(job, overviewService),
            KeepCount = backupOptions.CurrentValue.KeepCount,
        }, "取得備份清單成功"));
    }

    /// <summary>立即備份：放進「立即執行」佇列；已在佇列或執行中回 409。</summary>
    [HttpPost("run")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<object>>> Run()
    {
        var user = currentUserService.CurrentUser;
        if (!overviewService.TryTrigger(SystemBackupJob.JobName, user.Account, user.Id))
        {
            return Conflict(ApiResult<object>.ConflictResult("備份已經在執行中，請等它完成。"));
        }

        await ViewAudit.WriteAsync(auditLogService, currentUserService, AuditActions.Job.Trigger, "ScheduledJob", SystemBackupJob.JobName, $"job={SystemBackupJob.JobName}");
        logger.LogInformation("Backup triggered manually from the backup page.");
        return Ok(ApiResult<object>.SuccessResult(new { }, "已開始備份。"));
    }

    /// <summary>刪除一份備份（「刪除選取」由畫面逐筆呼叫，每份一筆稽核）。</summary>
    [HttpDelete("{fileName}")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<object>>> Delete(string fileName)
    {
        if (!store.Delete(fileName))
        {
            return NotFound(ApiResult<object>.NotFoundResult(NotFoundMessage));
        }

        await ViewAudit.WriteAsync(auditLogService, currentUserService, AuditActions.Backup.Delete, "Backup", fileName, $"file={fileName}");
        logger.LogInformation("Backup deleted from the backup page. FileName={FileName}", fileName);
        return Ok(ApiResult<object>.SuccessResult(new { }, $"已刪除「{fileName}」。"));
    }

    /// <summary>
    /// 申請下載票證：檔名合格、在備份目錄裡而且檔案存在才發，否則 404。回傳的網址 60 秒內有效、只能用一次，
    /// 瀏覽器以 <c>&lt;a download&gt;</c> 原生下載（不經 WASM 記憶體）。稽核在兌換時寫。
    /// </summary>
    [HttpPost("{fileName}/ticket")]
    [AdminOnly]
    public ActionResult<ApiResult<DownloadTicketDto>> Ticket(string fileName)
    {
        var path = store.TryResolveFullPath(fileName);
        if (path is null || !System.IO.File.Exists(path))
        {
            return NotFound(ApiResult<DownloadTicketDto>.NotFoundResult("找不到這份備份。"));
        }

        var user = currentUserService.CurrentUser;
        var ticket = downloadTickets.Issue(DownloadTicketService.BackupKind, fileName, user.Id, User.FindFirstValue(MagicObjectHelper.SecurityStampClaimType));
        logger.LogInformation("Backup download ticket issued. FileName={FileName}, UserId={UserId}", fileName, user.Id);
        return Ok(ApiResult<DownloadTicketDto>.SuccessResult(new DownloadTicketDto
        {
            Url = $"{Request.PathBase}/api/v1/downloads/{ticket}",
            ExpiresInSeconds = (int)DownloadTicketService.Lifetime.TotalSeconds,
        }, "已取得下載連結"));
    }

    private static BackupFileDto ToDto(BackupFileInfo item) => new()
    {
        FileName = item.FileName,
        SizeBytes = item.SizeBytes,
        CreatedAtLocal = MaintenanceJobMapper.Unzoned(item.CreatedAtLocal),
        Manifest = item.Manifest is { } manifest ? new BackupManifestDto
        {
            SystemVersion = manifest.SystemVersion,
            Trigger = manifest.Trigger,
            Folders = manifest.Folders.Select(x => new BackupManifestFolderDto { SettingKey = x.SettingKey, FileCount = x.FileCount }).ToList(),
            MissingFileCount = manifest.MissingFileCount,
        } : null,
    };
}
