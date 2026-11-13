using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;
using MyProject.Web.Client.Api;
using MyProject.Web.Auditing;
using MyProject.Web.Filters;

namespace MyProject.Web.Controllers;

/// <summary>
/// 角色的 Web API（0.10.4 起，WASM 遷移 M5）：只限管理員，走與畫面相同的 <see cref="RoleViewService"/>
/// （權限矩陣轉權限鍵並同步 RBAC 表、預設角色不可刪也不可改名、仍是某人的主要角色不可刪、並行衝突、軟刪除）。
/// 新增、修改、刪除、還原、永久刪除的稽核由服務寫；查看（開修改窗）與搜尋由這裡寫，與 Server 版相同。
/// 角色的變更不換任何人的工作階段版本：有效權限在每個請求重新讀取，下一個請求就生效。
/// </summary>
[Route("api/v1/roles")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[AdminOnly]
public class RolesController : ControllerBase
{
    private const string TargetType = "RoleView";

    private static readonly string[] SortableFields =
    [
        nameof(RoleViewAdapterModel.Name),
        nameof(RoleViewAdapterModel.CreateAt),
        nameof(RoleViewAdapterModel.UpdateAt),
    ];

    private readonly RoleViewService roleViewService;
    private readonly RolePermissionService rolePermissionService;
    private readonly TeamService teamService;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly ILogger<RolesController> logger;

    public RolesController(
        RoleViewService roleViewService,
        RolePermissionService rolePermissionService,
        TeamService teamService,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        ILogger<RolesController> logger)
    {
        this.roleViewService = roleViewService;
        this.rolePermissionService = rolePermissionService;
        this.teamService = teamService;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.logger = logger;
    }

    /// <summary>清單（含權限矩陣，「複製」直接用它）；<see cref="RoleSearchRequestDto.RecordSearch"/> 為 true 時寫一筆搜尋稽核。</summary>
    [HttpPost("search")]
    public async Task<ActionResult<ApiResult<PagedResult<RoleDto>>>> Search([FromBody] RoleSearchRequestDto request)
    {
        var sortField = ControllerVerifyResultExtensions.ResolveSortField<RoleViewAdapterModel>(request.SortBy, SortableFields);
        var dataRequest = new DataRequest
        {
            Search = request.Keyword ?? string.Empty,
            SortField = sortField ?? string.Empty,
            SortDescending = sortField is null ? null : request.SortDescending,
            CurrentPage = request.PageIndex,
            PageSize = request.PageSize,
            Take = request.PageSize,
        };
        var result = request.Deleted ? await roleViewService.GetDeletedAsync(dataRequest) : await roleViewService.GetAsync(dataRequest);

        if (request.RecordSearch)
        {
            await ViewAudit.WriteSearchAsync(auditLogService, currentUserService, AuditActions.Role.Search, TargetType,
                AuditSearchCondition.Text("keyword", request.Keyword), AuditSearchCondition.Of("showDeleted", request.Deleted));
        }

        return Ok(ApiResult<PagedResult<RoleDto>>.SuccessResult(new PagedResult<RoleDto>
        {
            Items = result.Result.Select(RoleDtoMapper.ToDto).ToList(),
            TotalCount = result.Count,
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            TotalPages = (int)Math.Ceiling(result.Count / (double)request.PageSize),
        }, "搜尋角色成功"));
    }

    /// <summary>單筆（WASM 頁開修改窗時呼叫，拿到最新的矩陣與版本號）；寫一筆「查看」稽核。</summary>
    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiResult<RoleDto>>> GetById(int id)
    {
        var item = await roleViewService.GetAsync(id);
        if (item.Id == 0)
        {
            return NotFound(ApiResult<RoleDto>.NotFoundResult($"找不到 ID 為 {id} 的角色"));
        }

        await ViewAudit.WriteAsync(auditLogService, currentUserService, AuditActions.Role.View, TargetType, id.ToString(), $"name={item.Name}");
        return Ok(ApiResult<RoleDto>.SuccessResult(RoleDtoMapper.ToDto(item), "取得角色成功"));
    }

    /// <summary>表單選項：啟用中的團隊（預設團隊下拉）與空白的權限矩陣（新增用）。</summary>
    [HttpGet("options")]
    public async Task<ActionResult<ApiResult<RoleFormOptionsDto>>> Options()
        => Ok(ApiResult<RoleFormOptionsDto>.SuccessResult(new RoleFormOptionsDto
        {
            Teams = await teamService.GetAllEnabledNamesAsync(),
            EmptyPermission = RoleDtoMapper.ToDto(rolePermissionService.InitializePermissionSetting()),
        }, "取得角色表單選項成功"));

    [HttpPost]
    public async Task<ActionResult<ApiResult<RoleDto>>> Create([FromBody] RoleDto dto)
    {
        var model = RoleDtoMapper.ToModel(dto);
        model.Id = 0;
        model.CreateAt = DateTime.Now;
        model.UpdateAt = DateTime.Now;
        model.RolePermission = OnlyTheCatalog(dto.Permission);
        if (Invalid(model) is { } invalid)
        {
            return BadRequest(ApiResult<RoleDto>.ValidationError(invalid));
        }

        var check = await roleViewService.BeforeAddCheckAsync(model);
        if (!check.Success)
        {
            return this.FromVerify<RoleDto>(check, () => null!, string.Empty);
        }

        var result = await roleViewService.AddAsync(model);
        if (!result.Success)
        {
            return this.FromVerify<RoleDto>(result, () => null!, string.Empty);
        }

        var saved = await roleViewService.GetAsync(model.Id);
        return Ok(ApiResult<RoleDto>.SuccessResult(RoleDtoMapper.ToDto(saved.Id == 0 ? model : saved), "新增角色成功"));
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<ApiResult>> Update(int id, [FromBody] RoleDto dto)
    {
        if (id != dto.Id)
        {
            return BadRequest(ApiResult.ValidationError("路由 ID 與資料 ID 不一致"));
        }

        var model = RoleDtoMapper.ToModel(dto);
        model.UpdateAt = DateTime.Now;
        model.RolePermission = OnlyTheCatalog(dto.Permission);
        if (Invalid(model) is { } invalid)
        {
            return BadRequest(ApiResult.ValidationError(invalid));
        }

        var check = await roleViewService.BeforeUpdateCheckAsync(model);
        if (!check.Success)
        {
            return this.FromVerify(check, string.Empty);
        }

        return this.FromVerify(await roleViewService.UpdateAsync(model), "更新角色成功");
    }

    /// <summary>
    /// 權限矩陣只採納權限目錄（<see cref="RolePermissionService.InitializePermissionSetting"/>）裡有的群組、頁面與動作（審查發現）：
    /// Server 版的矩陣來自伺服器，API 呼叫端卻可以多塞節點，例如管理員專屬的「稽核紀錄」，讓它被授予（破壞 AdminOnlyPermissionTests 的不變量）。
    /// </summary>
    private Models.Admins.RolePermission OnlyTheCatalog(RolePermissionDto? submitted)
    {
        var catalog = rolePermissionService.InitializePermissionSetting();
        var groups = (submitted?.Groups ?? []).GroupBy(x => x.Name).ToDictionary(x => x.Key, x => x.First());
        foreach (var group in catalog.Groups)
        {
            if (!groups.TryGetValue(group.Name, out var sent))
            {
                continue;
            }

            group.Enable = sent.Enable;
            var nodes = (sent.Permissions ?? []).GroupBy(x => x.Name).ToDictionary(x => x.Key, x => x.First());
            foreach (var node in group.Permissions)
            {
                if (!nodes.TryGetValue(node.Name, out var sentNode))
                {
                    continue;
                }

                node.Enable = sentNode.Enable;
                foreach (var action in RolePermissionService.SupportedActions)
                {
                    node.Actions[action] = sentNode.Actions?.GetValueOrDefault(action) ?? false;
                }
            }
        }

        return catalog;
    }

    /// <summary>畫面上的必填（名稱）由伺服器再驗一次：API 呼叫端不經過畫面的表單驗證。</summary>
    private static string? Invalid(RoleViewAdapterModel model)
    {
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        return System.ComponentModel.DataAnnotations.Validator.TryValidateObject(model, new(model), results, validateAllProperties: true)
            ? null
            : string.Join(" ", results.Select(x => x.ErrorMessage));
    }

    /// <summary>軟刪除：預設角色、仍是某人主要角色的不可刪（400，訊息列出前 5 個帳號）。</summary>
    [HttpDelete("{id:int}")]
    public async Task<ActionResult<ApiResult>> Delete(int id)
        => this.FromVerify(await roleViewService.DeleteAsync(id), "刪除角色成功");

    [HttpPost("{id:int}/restore")]
    public async Task<ActionResult<ApiResult>> Restore(int id)
        => this.FromVerify(await roleViewService.RestoreAsync(id), "還原成功");

    /// <summary>永久刪除：只能對已刪除的角色執行；權限設定與「額外角色」關聯一併刪除。</summary>
    [HttpDelete("{id:int}/purge")]
    public async Task<ActionResult<ApiResult>> Purge(int id)
    {
        logger.LogInformation("Role purge requested through the API. RoleViewId={RoleViewId}", id);
        return this.FromVerify(await roleViewService.PurgeAsync(id), "已永久刪除");
    }
}
