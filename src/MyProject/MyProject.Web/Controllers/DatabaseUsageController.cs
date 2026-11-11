using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;
using MyProject.Web.Diagnostics;
using MyProject.Web.Filters;

namespace MyProject.Web.Controllers;

/// <summary>
/// 資料庫用量頁的 Web API（0.10.6 起，WASM 遷移 M7），只限管理員；與 Server 版相同不寫稽核。
/// 走畫面同一個 <see cref="IDatabaseUsageService"/>；不送資料庫的完整路徑（只送檔名，同系統管理助理的工具）。
/// </summary>
[Route("api/v1/database-usage")]
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class DatabaseUsageController : ControllerBase
{
    private readonly IDatabaseUsageService databaseUsageService;
    private readonly ILogger<DatabaseUsageController> logger;

    public DatabaseUsageController(IDatabaseUsageService databaseUsageService, ILogger<DatabaseUsageController> logger)
    {
        this.databaseUsageService = databaseUsageService;
        this.logger = logger;
    }

    [HttpGet]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<DatabaseUsageDto>>> Get()
    {
        var report = await databaseUsageService.GetReportAsync(HttpContext.RequestAborted);
        logger.LogInformation("Database usage report loaded. Tables={Tables}, Warnings={Warnings}, ElapsedMs={ElapsedMs}",
            report.Tables.Count, report.Warnings.Count, report.ElapsedMilliseconds);
        return Ok(ApiResult<DatabaseUsageDto>.SuccessResult(new DatabaseUsageDto
        {
            DatabaseFileName = report.DatabaseFileName,
            MainDbBytes = report.MainDbBytes,
            WalBytes = report.WalBytes,
            ShmBytes = report.ShmBytes,
            TotalOnDiskBytes = report.TotalOnDiskBytes,
            WalFileExists = report.WalFileExists,
            ShmFileExists = report.ShmFileExists,
            PageCount = report.PageCount,
            PageSize = report.PageSize,
            AllocatedBytes = report.AllocatedBytes,
            FreelistCount = report.FreelistCount,
            ReclaimableBytes = report.ReclaimableBytes,
            Tables = report.Tables.Select(x => new DatabaseTableUsageDto
            {
                TableName = x.TableName,
                RowCount = x.RowCount,
                EstimatedBytes = x.EstimatedBytes,
                IndexCount = x.IndexCount,
                Note = x.Note,
            }).ToList(),
            Warnings = report.Warnings,
            Message = report.Message,
            ErrorMessage = report.ErrorMessage,
        }, "取得資料庫用量成功"));
    }
}
