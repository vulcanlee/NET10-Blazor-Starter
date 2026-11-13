using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Models;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Auth;
using MyProject.Web.Client.Api;
using MyProject.Web.Auditing;
using MyProject.Web.Export;
using MyProject.Web.Filters;

namespace MyProject.Web.Controllers;

/// <summary>
/// 團隊（部門樹）的 Web API。0.10.2 起（WASM 遷移 M3）改走與畫面相同的 <see cref="TeamService"/>：
/// 上層規則、改名同步引用、有下屬不可刪、軟刪除與稽核只有一份（服務寫稽核，操作者是存取 Token 的使用者）。
/// WASM 版團隊頁只用這組端點；部門樹在畫面上用 <c>GET all</c> 組成。
/// </summary>
[Route("api/[controller]")]
[Route("api/v1/[controller]")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class TeamController : ControllerBase
{
    private const string TargetType = "Team";
    private const string DeletedParentName = "（已刪除的團隊）";

    private static readonly string[] SortableFields =
    [
        nameof(TeamAdapterModel.Name),
        nameof(TeamAdapterModel.Code),
        nameof(TeamAdapterModel.IsEnabled),
        nameof(TeamAdapterModel.CreatedAt),
        nameof(TeamAdapterModel.UpdatedAt),
    ];

    private readonly ILogger<TeamController> logger;
    private readonly TeamService teamService;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;

    public TeamController(
        ILogger<TeamController> logger,
        TeamService teamService,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService)
    {
        this.logger = logger;
        this.teamService = teamService;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
    }

    /// <summary>單筆（WASM 頁開修改窗時呼叫，拿到最新的版本號）；寫一筆「查看」稽核。</summary>
    [HttpGet("{id}")]
    [HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<TeamDto>>> GetById(int id)
    {
        var item = await teamService.GetAsync(id);
        if (item.Id == 0)
        {
            logger.LogInformation("Team get request could not find record. TeamId={TeamId}", id);
            return NotFound(ApiResult<TeamDto>.NotFoundResult($"找不到 ID 為 {id} 的團隊"));
        }

        await ViewAudit.WriteAsync(auditLogService, currentUserService, AuditActions.Team.View, TargetType, id.ToString(), $"name={item.Name}");
        return Ok(ApiResult<TeamDto>.SuccessResult(TeamDtoMapper.ToDto(item), "取得團隊成功"));
    }

    /// <summary>所有未刪除的團隊（依名稱），畫面上組成部門樹。</summary>
    [HttpGet("all")]
    [HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<List<TeamDto>>>> All()
        => Ok(ApiResult<List<TeamDto>>.SuccessResult((await teamService.GetAllAsync()).Select(TeamDtoMapper.ToDto).ToList(), "取得團隊成功"));

    /// <summary>可以當作上層的團隊：排除自己與自己的所有下屬（新增時 id 傳 0）。存檔時伺服器會再檢查一次。</summary>
    [HttpGet("{id:int}/parent-candidates")]
    [HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<List<TeamNodeDto>>>> ParentCandidates(int id)
        => Ok(ApiResult<List<TeamNodeDto>>.SuccessResult(
            (await teamService.GetParentCandidatesAsync(id)).Select(x => new TeamNodeDto { Id = x.Id, Name = x.Name, ParentId = x.ParentId }).ToList(),
            "取得可選的上層團隊成功"));

    /// <summary>清單（平面；含「顯示已刪除」）；<see cref="TeamSearchRequestDto.RecordSearch"/> 為 true 時寫一筆搜尋稽核。</summary>
    [HttpPost("search")]
    [HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<PagedResult<TeamDto>>>> Search([FromBody] TeamSearchRequestDto request)
    {
        if (ForbidDeletedList(request.Deleted) is { } forbidden)
        {
            return forbidden;
        }

        var dataRequest = ToDataRequest(request);
        var result = request.Deleted
            ? await teamService.GetDeletedAsync(dataRequest)
            : await teamService.GetAsync(dataRequest, request.IsEnabled);

        if (request.RecordSearch)
        {
            await ViewAudit.WriteSearchAsync(auditLogService, currentUserService, AuditActions.Team.Search, TargetType,
                AuditSearchCondition.Text("keyword", request.Keyword), AuditSearchCondition.Of("showDeleted", request.Deleted));
        }

        logger.LogDebug("Team search completed. TotalCount={TotalCount}, Deleted={Deleted}", result.Count, request.Deleted);
        return Ok(ApiResult<PagedResult<TeamDto>>.SuccessResult(new PagedResult<TeamDto>
        {
            Items = result.Result.Select(TeamDtoMapper.ToDto).ToList(),
            TotalCount = result.Count,
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            TotalPages = (int)Math.Ceiling(result.Count / (double)request.PageSize),
        }, "搜尋團隊成功"));
    }

    /// <summary>存檔前的名稱、代號檢查（WASM 頁在存檔確認之後呼叫，與 Server 版的順序相同）；<c>Id</c> 有值時是修改。</summary>
    [HttpPost("check")]
    [HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.View)]
    public async Task<ActionResult<ApiResult>> Check([FromBody] TeamCreateUpdateDto dto)
    {
        var model = TeamDtoMapper.ToModel(dto);

        // 檢查等同要存檔：新增要有新增權限、修改要有修改權限（只能看的人不能拿它探名稱）；修改時範圍外的一律當作不存在。
        var action = model.Id > 0 ? PermissionActions.Edit : PermissionActions.Create;
        if (this.ForbidUnless(PermissionRules.CanAccessAction(currentUserService.CurrentUser, MagicObjectHelper.角色_團隊清單, action), "你沒有權限儲存團隊。") is { } forbidden)
        {
            return forbidden;
        }

        if (model.Id > 0 && (await teamService.GetAsync(model.Id)).Id == 0)
        {
            return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {model.Id} 的團隊"));
        }

        var result = model.Id > 0 ? await teamService.BeforeUpdateCheckAsync(model) : await teamService.BeforeAddCheckAsync(model);
        return this.FromVerify(result, "可以儲存");
    }

    [HttpPost]
    [HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.Create)]
    public async Task<ActionResult<ApiResult<TeamDto>>> Create([FromBody] TeamCreateUpdateDto dto)
    {
        var model = TeamDtoMapper.ToModel(dto);
        model.Id = 0;

        // 名稱、代號重複先擋（去空白、不分大小寫）；唯一索引只比對原字串，大小寫不同擋不下。
        var check = await teamService.BeforeAddCheckAsync(model);
        if (!check.Success)
        {
            return this.FromVerify<TeamDto>(check, () => null!, string.Empty);
        }

        var result = await teamService.AddAsync(model);
        return this.FromVerify(result, () => TeamDtoMapper.ToDto(model), "新增團隊成功");
    }

    [HttpPut("{id}")]
    [HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> Update(int id, [FromBody] TeamCreateUpdateDto dto)
    {
        if (id != dto.Id)
        {
            return BadRequest(ApiResult.ValidationError("路由 ID 與資料 ID 不一致"));
        }

        // 樂觀並行（0.9.93 起）：PUT 必須帶上 GET 取得的版本號，否則無法判斷是否覆蓋了別人的修改。
        if (string.IsNullOrWhiteSpace(dto.ConcurrencyStamp))
        {
            return BadRequest(ApiResult.ValidationError("ConcurrencyStamp 為必填：請帶上 GET 取得的版本號（用來避免覆蓋別人的修改）。"));
        }

        var model = TeamDtoMapper.ToModel(dto);

        // 先看範圍再比對名稱：範圍外的一律 404，不會因為名稱重複回 409 而透露它存在。
        if ((await teamService.GetAsync(id)).Id == 0)
        {
            return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的團隊"));
        }

        var check = await teamService.BeforeUpdateCheckAsync(model);
        if (!check.Success)
        {
            return this.FromVerify(check, string.Empty);
        }

        return this.FromVerify(await teamService.UpdateAsync(model), "更新團隊成功");
    }

    /// <summary>點狀態標籤切換啟用／停用（只改狀態，換版本號）。</summary>
    [HttpPut("{id:int}/enabled")]
    [HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> SetEnabled(int id, [FromBody] SetEnabledRequestDto request)
        => this.FromVerify(await teamService.SetEnabledAsync(id, request.Enabled), request.Enabled ? "已啟用" : "已停用");

    /// <summary>軟刪除；還有下屬團隊時不可刪（409）。</summary>
    [HttpDelete("{id}")]
    [HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.Delete)]
    public async Task<ActionResult<ApiResult>> Delete(int id)
        => this.FromVerify(await teamService.DeleteAsync(id), "刪除團隊成功");

    [HttpPost("{id:int}/restore")]
    [HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.Delete)]
    public async Task<ActionResult<ApiResult>> Restore(int id)
        => this.FromVerify(await teamService.RestoreAsync(id), "還原成功");

    /// <summary>永久刪除：只能對已刪除的團隊執行；還有其他團隊以它為上層時不可刪（409）。</summary>
    [HttpDelete("{id:int}/purge")]
    [HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.Delete)]
    public async Task<ActionResult<ApiResult>> Purge(int id)
        => this.FromVerify(await teamService.PurgeAsync(id), "已永久刪除");

    /// <summary>
    /// 匯出 Excel：沒有搜尋、不是「顯示已刪除」、也沒有啟用狀態條件時是部門樹模式（全部團隊），其他時候與清單同一個查詢；超過上限筆數回 400 與原因。
    /// 「上層團隊」欄顯示名稱，上層已刪除時顯示「（已刪除的團隊）」。
    /// </summary>
    [HttpPost("export")]
    [HasPermission(MagicObjectHelper.角色_團隊清單, PermissionActions.View)]
    public async Task<IActionResult> Export([FromBody] TeamSearchRequestDto request)
    {
        if (ForbidDeletedList(request.Deleted) is { } forbidden)
        {
            return forbidden;
        }

        var all = await teamService.GetAllAsync();
        var names = all.ToDictionary(x => x.Id, x => x.Name);
        var columns = BusinessExports.TeamColumns(parentId => parentId is { } id ? names.GetValueOrDefault(id, DeletedParentName) : null);
        var treeMode = !request.Deleted && string.IsNullOrWhiteSpace(request.Keyword) && request.IsEnabled is null;

        var outcome = treeMode
            ? BusinessExports.FromRows(all, "團隊", "teams", columns)
            : await BusinessExports.BuildAsync(
                r => request.Deleted ? teamService.GetDeletedAsync(r) : teamService.GetAsync(r, request.IsEnabled),
                ToDataRequest(request), "團隊", "teams", columns);
        if (outcome.Error is { } error)
        {
            return BadRequest(ApiResult.ValidationError(error));
        }

        logger.LogInformation("Team export downloaded. Rows={Rows}", outcome.Rows);
        await BusinessExports.WriteAuditAsync(auditLogService, currentUserService, AuditActions.Team.Export, TargetType, outcome.Rows, request.Deleted);
        return File(outcome.Content!, TabularExport.XlsxContentType, outcome.FileName);
    }

    /// <summary>「顯示已刪除」與畫面相同，要有刪除權限（只有檢視權限的人看不到已刪除的資料與刪除者）。</summary>
    private ObjectResult? ForbidDeletedList(bool deleted)
        => deleted
            ? this.ForbidUnless(PermissionRules.CanAccessAction(currentUserService.CurrentUser, MagicObjectHelper.角色_團隊清單, PermissionActions.Delete), "檢視已刪除的團隊需要刪除權限。")
            : null;

    private static DataRequest ToDataRequest(TeamSearchRequestDto request)
    {
        var sortField = ControllerVerifyResultExtensions.ResolveSortField<TeamAdapterModel>(request.SortBy, SortableFields);
        return new DataRequest
        {
            Search = request.Keyword ?? string.Empty,
            SortField = sortField ?? string.Empty,
            SortDescending = sortField is null ? null : request.SortDescending,
            CurrentPage = request.PageIndex,
            PageSize = request.PageSize,
            Take = request.PageSize,
        };
    }
}
