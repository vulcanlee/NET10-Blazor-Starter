using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;
using MyProject.Models.Systems;
using MyProject.Web.Auditing;
using MyProject.Web.Configuration.Parameters;
using MyProject.Web.Filters;

namespace MyProject.Web.Controllers;

/// <summary>
/// 系統參數頁的 Web API（0.10.6 起，WASM 遷移 M7），只限管理員。走畫面同一個 <see cref="SystemParameterManager"/>：
/// 存檔前以參數目錄的定義與所有設定驗證器在伺服器驗證，通過才寫資料庫，寫完立即重新套用（不需重啟）。
/// 稽核與 Server 版頁面相同：修改一筆 <c>SystemParameter.Update</c>、還原（或存成與設定檔相同的值、移除不認得的參數）一筆
/// <c>SystemParameter.Reset</c>，targetType <c>SystemParameter</c>、targetId 是設定鍵，detail <c>key=…; old=…(來源); new=…(來源)</c>。
/// 設定鍵含冒號，所以放在本文而不是網址。縮短保留天數的確認窗在畫面上（與 Server 版相同）。
/// </summary>
[Route("api/v1/system-parameters")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class SystemParametersController : ControllerBase
{
    private const string TargetType = "SystemParameter";

    private readonly SystemParameterManager manager;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly ILogger<SystemParametersController> logger;

    public SystemParametersController(
        SystemParameterManager manager,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        ILogger<SystemParametersController> logger)
    {
        this.manager = manager;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.logger = logger;
    }

    /// <summary>全部參數（目錄順序）、資料庫裡不認得的鍵、設定檔本身有誤的區段。</summary>
    [HttpGet]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<SystemParameterOverviewDto>>> Get()
    {
        var overview = await manager.GetOverviewAsync(HttpContext.RequestAborted);
        var dto = new SystemParameterOverviewDto
        {
            Items = overview.Items.Select(x => new SystemParameterItemDto
            {
                Definition = ToDto(x.Definition),
                EffectiveValue = x.EffectiveValue,
                BaseValue = x.BaseValue,
                Source = x.Source,
                Problem = x.Problem,
                Override = x.Override is { } row
                    ? new SystemParameterOverrideDto
                    {
                        ConcurrencyStamp = row.ConcurrencyStamp,
                        UpdatedBy = row.UpdatedBy,
                        // 資料庫是 UTC；換成伺服器本地時間、不帶時差送出（WASM 才不會換成瀏覽器的時區）。
                        UpdatedAt = DateTime.SpecifyKind(DateTime.SpecifyKind(row.UpdatedAtUtc, DateTimeKind.Utc).ToLocalTime(), DateTimeKind.Unspecified),
                    }
                    : null,
            }).ToList(),
            Orphans = overview.Orphans.Select(x => new SystemParameterOrphanDto { ParameterKey = x.ParameterKey, ConcurrencyStamp = x.ConcurrencyStamp }).ToList(),
            BaseInvalid = overview.BaseInvalid.Select(x => new SystemParameterSectionErrorDto { Section = x.Key, Errors = x.Value.ToList() }).ToList(),
        };
        return Ok(ApiResult<SystemParameterOverviewDto>.SuccessResult(dto, "取得系統參數成功"));
    }

    /// <summary>
    /// 修改一個參數。不在目錄的鍵 404；型別、範圍、長度、空白、設定驗證器不通過或值沒變 400；
    /// 開窗後別人改過或設定檔本身有誤 409。成功訊息就是畫面顯示的那一句。
    /// </summary>
    [HttpPost("save")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult>> Save([FromBody] SystemParameterSaveRequestDto request)
    {
        var result = await manager.SaveAsync(request.Key, request.Value, request.ExpectedStamp, currentUserService.CurrentUser.Account, HttpContext.RequestAborted);
        return await FinishAsync(request.Key, result);
    }

    /// <summary>還原為設定檔的值，或移除資料庫裡不認得的參數。覆寫已不存在 404、版本不同 409。</summary>
    [HttpPost("reset")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult>> Reset([FromBody] SystemParameterResetRequestDto request)
    {
        var result = await manager.ResetAsync(request.Key, request.ExpectedStamp, HttpContext.RequestAborted);
        return await FinishAsync(request.Key, result);
    }

    private async Task<ActionResult<ApiResult>> FinishAsync(string key, SystemParameterChangeResult result)
    {
        if (result.Success)
        {
            await ViewAudit.WriteAsync(
                auditLogService, currentUserService,
                result.IsReset ? AuditActions.SystemParameter.Reset : AuditActions.SystemParameter.Update,
                TargetType, TargetIdOf(key), result.AuditDetail);
        }
        else
        {
            // 不記使用者輸入的值，只記鍵與失敗種類。
            logger.LogInformation("System parameter change rejected. ParameterKey={ParameterKey}, Kind={Kind}", TargetIdOf(key), result.Kind);
        }

        return this.FromVerify(new VerifyRecordResult { Success = result.Success, Message = result.Message, Kind = result.Kind }, result.Message);
    }

    /// <summary>稽核的 targetId：目錄裡的寫法（與 Server 版的 <c>definition.Key</c> 相同）；不認得的鍵用資料庫裡的寫法（畫面送來的就是它）。</summary>
    private static string TargetIdOf(string key) => SystemParameterCatalog.Find(key)?.Key ?? key;

    private static SystemParameterDefinitionDto ToDto(SystemParameterDefinition definition) => new()
    {
        Key = definition.Key,
        Group = definition.Group,
        Label = definition.Label,
        Description = definition.Description,
        EffectNote = definition.EffectNote,
        Kind = definition.Kind,
        Unit = definition.Unit,
        ZeroMeaning = definition.ZeroMeaning,
        Min = definition.Min,
        Max = definition.Max,
        MaxLength = definition.MaxLength,
        AllowedValues = definition.AllowedValues?.ToList(),
        BlankMeaning = definition.BlankMeaning,
        Required = definition.Required,
        IsRetention = definition.IsRetention,
    };
}
