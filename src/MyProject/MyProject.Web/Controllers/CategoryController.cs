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
/// 分類的 Web API。0.10.2 起（WASM 遷移 M3）改走與畫面相同的 <see cref="CategoryService"/>：
/// 團隊範圍、名稱規則、並行衝突、軟刪除與稽核只有一份（服務寫稽核，操作者是存取 Token 的使用者）。
/// 範圍外的資料一律當作不存在（404）。WASM 版分類頁只用這組端點。
/// </summary>
[Route("api/[controller]")]
[Route("api/v1/[controller]")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class CategoryController : ControllerBase
{
    private const string TargetType = "Category";

    private static readonly string[] SortableFields =
    [
        nameof(CategoryAdapterModel.Name),
        nameof(CategoryAdapterModel.IsEnabled),
        nameof(CategoryAdapterModel.CreatedAt),
        nameof(CategoryAdapterModel.UpdatedAt),
    ];

    private readonly ILogger<CategoryController> logger;
    private readonly CategoryService categoryService;
    private readonly TeamService teamService;
    private readonly IRecordAccessScopeProvider accessScope;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;

    public CategoryController(
        ILogger<CategoryController> logger,
        CategoryService categoryService,
        TeamService teamService,
        IRecordAccessScopeProvider accessScope,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService)
    {
        this.logger = logger;
        this.categoryService = categoryService;
        this.teamService = teamService;
        this.accessScope = accessScope;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
    }

    /// <summary>單筆（WASM 頁開修改窗時呼叫，拿到最新的版本號）；寫一筆「查看」稽核。</summary>
    [HttpGet("{id}")]
    [HasPermission(MagicObjectHelper.角色_分類清單, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<CategoryDto>>> GetById(int id)
    {
        var item = await categoryService.GetAsync(id);
        if (item.Id == 0)
        {
            logger.LogInformation("Category get request could not find record. CategoryId={CategoryId}", id);
            return NotFound(ApiResult<CategoryDto>.NotFoundResult($"找不到 ID 為 {id} 的分類"));
        }

        await ViewAudit.WriteAsync(auditLogService, currentUserService, AuditActions.Category.View, TargetType, id.ToString(), $"name={item.Name}");
        return Ok(ApiResult<CategoryDto>.SuccessResult(CategoryDtoMapper.ToDto(item), "取得分類成功"));
    }

    /// <summary>清單（含「顯示已刪除」）；<see cref="CategorySearchRequestDto.RecordSearch"/> 為 true 時寫一筆搜尋稽核。</summary>
    [HttpPost("search")]
    [HasPermission(MagicObjectHelper.角色_分類清單, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<PagedResult<CategoryDto>>>> Search([FromBody] CategorySearchRequestDto request)
    {
        if (ForbidDeletedList(request.Deleted) is { } forbidden)
        {
            return forbidden;
        }

        var dataRequest = ToDataRequest(request);
        var result = request.Deleted
            ? await categoryService.GetDeletedAsync(dataRequest)
            : await categoryService.GetAsync(dataRequest, request.IsEnabled);

        if (request.RecordSearch)
        {
            await ViewAudit.WriteSearchAsync(auditLogService, currentUserService, AuditActions.Category.Search, TargetType,
                AuditSearchCondition.Text("keyword", request.Keyword), AuditSearchCondition.Of("showDeleted", request.Deleted));
        }

        logger.LogDebug("Category search completed. TotalCount={TotalCount}, Deleted={Deleted}", result.Count, request.Deleted);
        return Ok(ApiResult<PagedResult<CategoryDto>>.SuccessResult(new PagedResult<CategoryDto>
        {
            Items = result.Result.Select(CategoryDtoMapper.ToDto).ToList(),
            TotalCount = result.Count,
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            TotalPages = (int)Math.Ceiling(result.Count / (double)request.PageSize),
        }, "搜尋分類成功"));
    }

    /// <summary>表單的選項：啟用中的團隊名稱與自己的團隊範圍（與 Server 版相同，不要求團隊頁權限）。</summary>
    [HttpGet("options")]
    [HasPermission(MagicObjectHelper.角色_分類清單, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<CategoryFormOptionsDto>>> Options()
    {
        var scope = await accessScope.GetAsync();
        return Ok(ApiResult<CategoryFormOptionsDto>.SuccessResult(new CategoryFormOptionsDto
        {
            Teams = await teamService.GetAllEnabledNamesAsync(),
            IsAdmin = scope.IsAdmin,
            MyTeams = scope.Teams.ToList(),
        }, "取得分類表單選項成功"));
    }

    /// <summary>存檔前的名稱檢查（WASM 頁在「團隊提醒」之前呼叫，與 Server 版的順序相同）；<c>Id</c> 有值時是修改。</summary>
    [HttpPost("check")]
    [HasPermission(MagicObjectHelper.角色_分類清單, PermissionActions.View)]
    public async Task<ActionResult<ApiResult>> Check([FromBody] CategoryCreateUpdateDto dto)
    {
        var model = CategoryDtoMapper.ToModel(dto);

        // 檢查等同要存檔：新增要有新增權限、修改要有修改權限（只能看的人不能拿它探名稱）；修改時範圍外的一律當作不存在。
        var action = model.Id > 0 ? PermissionActions.Edit : PermissionActions.Create;
        if (this.ForbidUnless(PermissionRules.CanAccessAction(currentUserService.CurrentUser, MagicObjectHelper.角色_分類清單, action), "你沒有權限儲存分類。") is { } forbidden)
        {
            return forbidden;
        }

        if (model.Id > 0 && (await categoryService.GetAsync(model.Id)).Id == 0)
        {
            return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {model.Id} 的分類"));
        }

        var result = model.Id > 0 ? await categoryService.BeforeUpdateCheckAsync(model) : await categoryService.BeforeAddCheckAsync(model);
        return this.FromVerify(result, "可以儲存");
    }

    [HttpPost]
    [HasPermission(MagicObjectHelper.角色_分類清單, PermissionActions.Create)]
    public async Task<ActionResult<ApiResult<CategoryDto>>> Create([FromBody] CategoryCreateUpdateDto dto)
    {
        var model = CategoryDtoMapper.ToModel(dto);
        model.Id = 0;

        // 名稱重複先擋（去空白、不分大小寫）；唯一索引只比對原字串，大小寫不同擋不下。
        var check = await categoryService.BeforeAddCheckAsync(model);
        if (!check.Success)
        {
            return this.FromVerify<CategoryDto>(check, () => null!, string.Empty);
        }

        var result = await categoryService.AddAsync(model);
        return this.FromVerify(result, () => CategoryDtoMapper.ToDto(model), "新增分類成功");
    }

    [HttpPut("{id}")]
    [HasPermission(MagicObjectHelper.角色_分類清單, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> Update(int id, [FromBody] CategoryCreateUpdateDto dto)
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

        var model = CategoryDtoMapper.ToModel(dto);

        // 先看範圍再比對名稱：範圍外的一律 404，不會因為名稱重複回 409 而透露它存在。
        if ((await categoryService.GetAsync(id)).Id == 0)
        {
            return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的分類"));
        }

        var check = await categoryService.BeforeUpdateCheckAsync(model);
        if (!check.Success)
        {
            return this.FromVerify(check, string.Empty);
        }

        return this.FromVerify(await categoryService.UpdateAsync(model), "更新分類成功");
    }

    /// <summary>點狀態標籤切換啟用／停用（只改狀態，換版本號）。</summary>
    [HttpPut("{id:int}/enabled")]
    [HasPermission(MagicObjectHelper.角色_分類清單, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> SetEnabled(int id, [FromBody] SetEnabledRequestDto request)
        => this.FromVerify(await categoryService.SetEnabledAsync(id, request.Enabled), request.Enabled ? "已啟用" : "已停用");

    /// <summary>軟刪除：可在「顯示已刪除」中還原或永久刪除。</summary>
    [HttpDelete("{id}")]
    [HasPermission(MagicObjectHelper.角色_分類清單, PermissionActions.Delete)]
    public async Task<ActionResult<ApiResult>> Delete(int id)
        => this.FromVerify(await categoryService.DeleteAsync(id), "刪除分類成功");

    [HttpPost("{id:int}/restore")]
    [HasPermission(MagicObjectHelper.角色_分類清單, PermissionActions.Delete)]
    public async Task<ActionResult<ApiResult>> Restore(int id)
        => this.FromVerify(await categoryService.RestoreAsync(id), "還原成功");

    /// <summary>永久刪除：只能對已刪除的分類執行。</summary>
    [HttpDelete("{id:int}/purge")]
    [HasPermission(MagicObjectHelper.角色_分類清單, PermissionActions.Delete)]
    public async Task<ActionResult<ApiResult>> Purge(int id)
        => this.FromVerify(await categoryService.PurgeAsync(id), "已永久刪除");

    /// <summary>
    /// 匯出 Excel：與清單同一個查詢（搜尋、篩選、排序、顯示已刪除、團隊範圍），超過上限筆數回 400 與原因。
    /// 看得到清單就能匯出（使用者決定，沒有另外的匯出權限）。
    /// </summary>
    [HttpPost("export")]
    [HasPermission(MagicObjectHelper.角色_分類清單, PermissionActions.View)]
    public async Task<IActionResult> Export([FromBody] CategorySearchRequestDto request)
    {
        if (ForbidDeletedList(request.Deleted) is { } forbidden)
        {
            return forbidden;
        }

        var outcome = await BusinessExports.BuildAsync(
            r => request.Deleted ? categoryService.GetDeletedAsync(r) : categoryService.GetAsync(r, request.IsEnabled),
            ToDataRequest(request), "分類", "categories", BusinessExports.CategoryColumns);
        if (outcome.Error is { } error)
        {
            return BadRequest(ApiResult.ValidationError(error));
        }

        logger.LogInformation("Category export downloaded. Rows={Rows}", outcome.Rows);
        await BusinessExports.WriteAuditAsync(auditLogService, currentUserService, AuditActions.Category.Export, TargetType, outcome.Rows, request.Deleted);
        return File(outcome.Content!, TabularExport.XlsxContentType, outcome.FileName);
    }

    /// <summary>「顯示已刪除」與畫面相同，要有刪除權限（只有檢視權限的人看不到已刪除的資料與刪除者）。</summary>
    private ObjectResult? ForbidDeletedList(bool deleted)
        => deleted
            ? this.ForbidUnless(PermissionRules.CanAccessAction(currentUserService.CurrentUser, MagicObjectHelper.角色_分類清單, PermissionActions.Delete), "檢視已刪除的分類需要刪除權限。")
            : null;

    private static DataRequest ToDataRequest(CategorySearchRequestDto request)
    {
        var sortField = ControllerVerifyResultExtensions.ResolveSortField<CategoryAdapterModel>(request.SortBy, SortableFields);
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
