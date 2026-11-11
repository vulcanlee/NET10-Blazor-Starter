using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
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
using MyProject.Web.Components.Commons;
using MyProject.Web.Downloads;
using MyProject.Web.Export;
using MyProject.Web.Filters;

namespace MyProject.Web.Controllers;

/// <summary>
/// 專案的 Web API。0.10.3 起（WASM 遷移 M4）改走與畫面相同的 <see cref="ProjectService"/>（刪除最後一個 Repository）：
/// 團隊範圍、名稱與欄位規則、並行衝突、軟刪除、附件與稽核只有一份（服務寫稽核，操作者是存取 Token 的使用者）。
/// 範圍外的資料一律當作不存在（404）。WASM 版專案頁只用這組端點。
///
/// 新增與修改的網址各一個，依 Content-Type 分兩種：JSON（不帶附件，與 0.10.3 之前相同）與 multipart（<c>data</c> 是同一份 JSON，
/// 另帶 <c>files</c> 與 <c>removedFileIds</c>）。multipart 與 Server 版畫面走同一條路：資料與附件在同一次存檔裡處理。
/// 附件下載先以 <c>POST files/{id}/ticket</c> 申請一次性票證，再由瀏覽器原生下載（<see cref="DownloadsController"/>）。
/// </summary>
[Route("api/[controller]")]
[Route("api/v1/[controller]")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class ProjectController : ControllerBase
{
    /// <summary>
    /// 一次存檔（含所有附件）的請求大小上限：IIS 的 <c>maxAllowedContentLength</c> 最多只能設到 4 GB（見 web.config），這裡與它一致。
    /// 單一附件的上限是 <see cref="ProjectService.MaxUploadFileSize"/>（1 GB），由服務檢查。
    /// </summary>
    public const long MaxRequestBytes = uint.MaxValue;

    private const string TargetType = "Project";
    private const string FormContentType = "multipart/form-data";

    /// <summary>
    /// JSON 版本收的 Content-Type：與 0.10.3 之前的 <c>[FromBody]</c> 相同（application/json、text/json、application/*+json）。
    /// 只寫 application/json 的話，text/json 的請求會從 200 變成錯誤（審查發現）。
    /// </summary>
    private const string JsonContentType = "application/json";

    private static readonly string[] SortableFields =
    [
        nameof(ProjectAdapterModel.Title),
        nameof(ProjectAdapterModel.StartDate),
        nameof(ProjectAdapterModel.EndDate),
        nameof(ProjectAdapterModel.Status),
        nameof(ProjectAdapterModel.Priority),
        nameof(ProjectAdapterModel.CompletionPercentage),
        nameof(ProjectAdapterModel.Owner),
        nameof(ProjectAdapterModel.CreatedAt),
        nameof(ProjectAdapterModel.UpdatedAt),
    ];

    private readonly ILogger<ProjectController> logger;
    private readonly ProjectService projectService;
    private readonly CategoryService categoryService;
    private readonly TeamService teamService;
    private readonly ITeamTreeCache teamTreeCache;
    private readonly IRecordAccessScopeProvider accessScope;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly DownloadTicketService downloadTickets;
    private readonly SystemSettings systemSettings;

    public ProjectController(
        ILogger<ProjectController> logger,
        ProjectService projectService,
        CategoryService categoryService,
        TeamService teamService,
        ITeamTreeCache teamTreeCache,
        IRecordAccessScopeProvider accessScope,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        DownloadTicketService downloadTickets,
        IOptions<SystemSettings> systemSettings)
    {
        this.logger = logger;
        this.projectService = projectService;
        this.categoryService = categoryService;
        this.teamService = teamService;
        this.teamTreeCache = teamTreeCache;
        this.accessScope = accessScope;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.downloadTickets = downloadTickets;
        this.systemSettings = systemSettings.Value;
    }

    /// <summary>單筆（含附件清單；WASM 頁開修改窗時呼叫，拿到最新的版本號）；寫一筆「查看」稽核。</summary>
    /// <param name="includeRelatedData">0.10.3 之前的參數，保留相容；附件一律回傳。</param>
    [HttpGet("{id}")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<ProjectDto>>> GetById(int id, [FromQuery] bool includeRelatedData = false)
    {
        var item = await projectService.GetAsync(id);
        if (item.Id == 0)
        {
            logger.LogInformation("Project get request could not find record. ProjectId={ProjectId}", id);
            return NotFound(ApiResult<ProjectDto>.NotFoundResult($"找不到 ID 為 {id} 的專案"));
        }

        await ViewAudit.WriteAsync(auditLogService, currentUserService, AuditActions.Project.View, TargetType, id.ToString(), $"title={item.Title}");
        return Ok(ApiResult<ProjectDto>.SuccessResult(ProjectDtoMapper.ToDto(item), "取得專案成功"));
    }

    /// <summary>清單（含「顯示已刪除」）；<see cref="ProjectSearchRequestDto.RecordSearch"/> 為 true 時寫一筆搜尋稽核。</summary>
    [HttpPost("search")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<PagedResult<ProjectDto>>>> Search([FromBody] ProjectSearchRequestDto request)
    {
        if (ForbidDeletedList(request.Deleted) is { } forbidden)
        {
            return forbidden;
        }

        var dataRequest = ToDataRequest(request);
        var filter = ToFilter(request);
        var result = request.Deleted
            ? await projectService.GetDeletedAsync(dataRequest, filter)
            : await projectService.GetAsync(dataRequest, filter);

        if (request.RecordSearch)
        {
            await ViewAudit.WriteSearchAsync(auditLogService, currentUserService, AuditActions.Project.Search, TargetType,
                AuditSearchCondition.Text("keyword", request.Keyword), AuditSearchCondition.Of("categories", request.Categories),
                AuditSearchCondition.Of("teams", request.Teams), AuditSearchCondition.Of("showDeleted", request.Deleted));
        }

        logger.LogDebug("Project search completed. TotalCount={TotalCount}, Deleted={Deleted}", result.Count, request.Deleted);
        return Ok(ApiResult<PagedResult<ProjectDto>>.SuccessResult(new PagedResult<ProjectDto>
        {
            Items = result.Result.Select(ProjectDtoMapper.ToDto).ToList(),
            TotalCount = result.Count,
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            TotalPages = (int)Math.Ceiling(result.Count / (double)request.PageSize),
        }, "搜尋專案成功"));
    }

    /// <summary>工具列過濾與表單的選項、新增時預設帶入的團隊、附件限制（與 Server 版相同，不要求分類頁、團隊頁的權限）。</summary>
    [HttpGet("options")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<ProjectFormOptionsDto>>> Options()
    {
        var scope = await accessScope.GetAsync();
        var tree = await teamTreeCache.GetAsync();

        // 預設團隊：範圍裡最上層的那些（0.9.105 起範圍含下屬部門，全帶會讓專案對所有下屬公開）。
        var defaultTeams = scope.Teams
            .Where(t => !tree.AncestorsAndSelf(t).Skip(1).Any(a => scope.Teams.Contains(a, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        return Ok(ApiResult<ProjectFormOptionsDto>.SuccessResult(new ProjectFormOptionsDto
        {
            Categories = await categoryService.GetAllEnabledNamesAsync(),
            Teams = await teamService.GetAllEnabledNamesAsync(),
            IsAdmin = scope.IsAdmin,
            MyTeams = scope.Teams.ToList(),
            DefaultTeams = defaultTeams,
            AllowedExtensions = [.. UploadFileTypePolicy.EffectiveExtensions(systemSettings.Upload.AllowedExtensions)],
            MaxUploadFileSize = ProjectService.MaxUploadFileSize,
        }, "取得專案表單選項成功"));
    }

    /// <summary>新增（不帶附件）。</summary>
    [HttpPost]
    [Consumes(JsonContentType, "text/json", "application/*+json")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.Create)]
    public Task<ActionResult<ApiResult<ProjectDto>>> Create([FromBody] ProjectCreateUpdateDto dto)
        => CreateCoreAsync(dto, []);

    /// <summary>
    /// 新增（帶附件，multipart：<c>data</c> 是專案的 JSON，<c>files</c> 是附件）。
    /// <c>Order = 1</c>：請求沒帶 Content-Type 時兩個 action 都符合，讓 JSON 版優先（回 415，與 0.10.3 之前相同），不會因為模稜兩可而 500。
    /// 不列入 Swagger：Swagger 同一個方法與網址只能有一個 action（否則整份文件產不出來）；格式見 docs 的 Web API 端點目錄。
    /// </summary>
    [HttpPost(Order = 1)]
    [ApiExplorerSettings(IgnoreApi = true)]
    [Consumes(FormContentType)]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.Create)]
    public async Task<ActionResult<ApiResult<ProjectDto>>> CreateWithFiles([FromForm] ProjectFormRequest form)
    {
        if (ParseFormData(form) is not { } dto)
        {
            return BadRequest(InvalidFormData<ProjectDto>(form));
        }

        return await CreateCoreAsync(dto, form.Files);
    }

    /// <summary>修改（不帶附件）。</summary>
    [HttpPut("{id}")]
    [Consumes(JsonContentType, "text/json", "application/*+json")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.Edit)]
    public Task<ActionResult<ApiResult>> Update(int id, [FromBody] ProjectCreateUpdateDto dto)
        => UpdateCoreAsync(id, dto, [], []);

    /// <summary>修改（帶附件，multipart：<c>data</c>、<c>files</c>、<c>removedFileIds</c>）。Order 與不列入 Swagger 的理由見 <see cref="CreateWithFiles"/>。</summary>
    [HttpPut("{id}", Order = 1)]
    [ApiExplorerSettings(IgnoreApi = true)]
    [Consumes(FormContentType)]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.Edit)]
    public async Task<ActionResult<ApiResult>> UpdateWithFiles(int id, [FromForm] ProjectFormRequest form)
    {
        if (ParseFormData(form) is not { } dto)
        {
            return BadRequest(InvalidFormData<object>(form));
        }

        return await UpdateCoreAsync(id, dto, form.Files, form.RemovedFileIds);
    }

    /// <summary>軟刪除：可在「顯示已刪除」中還原或永久刪除；附件實體檔保留到永久刪除。</summary>
    [HttpDelete("{id}")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.Delete)]
    public async Task<ActionResult<ApiResult>> Delete(int id)
        => this.FromVerify(await projectService.DeleteAsync(id), "刪除專案成功");

    [HttpPost("{id}/restore")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.Delete)]
    public async Task<ActionResult<ApiResult>> Restore(int id)
        => this.FromVerify(await projectService.RestoreAsync(id), "還原成功");

    /// <summary>永久刪除：只能對已刪除的專案執行，附件實體檔一併刪除。</summary>
    [HttpDelete("{id}/purge")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.Delete)]
    public async Task<ActionResult<ApiResult>> Purge(int id)
        => this.FromVerify(await projectService.PurgeAsync(id), "已永久刪除");

    /// <summary>匯出 Excel：與清單同一個查詢（搜尋、篩選、排序、顯示已刪除、團隊範圍），超過上限筆數回 400 與原因。</summary>
    [HttpPost("export")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.View)]
    public async Task<IActionResult> Export([FromBody] ProjectSearchRequestDto request)
    {
        if (ForbidDeletedList(request.Deleted) is { } forbidden)
        {
            return forbidden;
        }

        var filter = ToFilter(request);
        var outcome = await BusinessExports.BuildAsync(
            r => request.Deleted ? projectService.GetDeletedAsync(r, filter) : projectService.GetAsync(r, filter),
            ToDataRequest(request), "專案", "projects", BusinessExports.ProjectColumns);
        if (outcome.Error is { } error)
        {
            return BadRequest(ApiResult.ValidationError(error));
        }

        logger.LogInformation("Project export downloaded. Rows={Rows}", outcome.Rows);
        await BusinessExports.WriteAuditAsync(auditLogService, currentUserService, AuditActions.Project.Export, TargetType, outcome.Rows, request.Deleted);
        return File(outcome.Content!, TabularExport.XlsxContentType, outcome.FileName);
    }

    /// <summary>
    /// 申請附件的一次性下載票證：看得到這個附件（專案未刪除、在團隊範圍內、實體檔存在）才發，否則 404。
    /// 回傳的網址 60 秒內有效、只能用一次，瀏覽器直接導向它即可下載。
    /// </summary>
    [HttpPost("files/{fileId}/ticket")]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<DownloadTicketDto>>> FileTicket(int fileId)
    {
        var file = await projectService.GetFileDownloadAsync(fileId);
        if (file is null)
        {
            return NotFound(ApiResult<DownloadTicketDto>.NotFoundResult($"找不到 ID 為 {fileId} 的專案附件"));
        }

        // 這裡只確認看得到；檔案在兌換票證時才真正開啟。
        await file.Content.DisposeAsync();

        var user = currentUserService.CurrentUser;
        var ticket = downloadTickets.Issue(DownloadTicketService.ProjectFileKind, fileId, user.Id, User.FindFirstValue(MagicObjectHelper.SecurityStampClaimType));
        logger.LogInformation("Project file download ticket issued. ProjectFileId={ProjectFileId}, UserId={UserId}", fileId, user.Id);
        return Ok(ApiResult<DownloadTicketDto>.SuccessResult(new DownloadTicketDto
        {
            Url = $"{Request.PathBase}/api/v1/downloads/{ticket}",
            ExpiresInSeconds = (int)DownloadTicketService.Lifetime.TotalSeconds,
        }, "已取得下載連結"));
    }

    private async Task<ActionResult<ApiResult<ProjectDto>>> CreateCoreAsync(ProjectCreateUpdateDto dto, IReadOnlyList<IFormFile> files)
    {
        var model = ProjectDtoMapper.ToModel(dto);
        model.Id = 0;
        model.CreatedAt = DateTime.Now;
        model.UpdatedAt = DateTime.Now;

        using var uploads = new UploadInputs(files);
        var check = await projectService.BeforeAddCheckAsync(model, uploads.Inputs);
        if (!check.Success)
        {
            return this.FromVerify<ProjectDto>(check, () => null!, string.Empty);
        }

        var result = await projectService.AddAsync(model, uploads.Inputs);
        if (!result.Success)
        {
            return this.FromVerify<ProjectDto>(result, () => null!, string.Empty);
        }

        // 回傳存好的樣子（含附件清單與新的版本號）；這不是使用者開啟，不寫「查看」稽核。
        var saved = await projectService.GetAsync(model.Id);
        return Ok(ApiResult<ProjectDto>.SuccessResult(ProjectDtoMapper.ToDto(saved.Id == 0 ? model : saved), "新增專案成功"));
    }

    private async Task<ActionResult<ApiResult>> UpdateCoreAsync(int id, ProjectCreateUpdateDto dto, IReadOnlyList<IFormFile> files, IReadOnlyList<int> removedFileIds)
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

        // 先看範圍再比對名稱：範圍外的一律 404，不會因為名稱重複回 409 而透露它存在。
        if ((await projectService.GetAsync(id)).Id == 0)
        {
            return NotFound(ApiResult.NotFoundResult($"找不到 ID 為 {id} 的專案"));
        }

        var model = ProjectDtoMapper.ToModel(dto);
        model.UpdatedAt = DateTime.Now;

        using var uploads = new UploadInputs(files);
        var check = await projectService.BeforeUpdateCheckAsync(model, uploads.Inputs);
        if (!check.Success)
        {
            return this.FromVerify(check, string.Empty);
        }

        return this.FromVerify(await projectService.UpdateAsync(model, uploads.Inputs, removedFileIds), "更新專案成功");
    }

    /// <summary>multipart 的 <c>data</c> 轉成 DTO 並套用與 JSON 相同的欄位驗證；不合法回 null（錯誤寫進 <see cref="ProjectFormRequest.Errors"/>）。</summary>
    private static ProjectCreateUpdateDto? ParseFormData(ProjectFormRequest form)
    {
        ProjectCreateUpdateDto? dto;
        try
        {
            dto = string.IsNullOrWhiteSpace(form.Data) ? null : JsonSerializer.Deserialize<ProjectCreateUpdateDto>(form.Data, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            dto = null;
        }

        if (dto is null)
        {
            form.Errors["data"] = ["data 欄位不是有效的專案資料。"];
            return null;
        }

        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(dto, new ValidationContext(dto), results, validateAllProperties: true))
        {
            return dto;
        }

        foreach (var result in results)
        {
            var key = result.MemberNames.FirstOrDefault() ?? "data";
            form.Errors[key] = [.. form.Errors.GetValueOrDefault(key) ?? [], result.ErrorMessage ?? "欄位不合法"];
        }

        return null;
    }

    private ApiResult<T> InvalidFormData<T>(ProjectFormRequest form)
    {
        var body = ApiResult<T>.ValidationError(form.Errors);
        body.TraceId = HttpContext.TraceIdentifier;
        return body;
    }

    /// <summary>「顯示已刪除」與畫面相同，要有刪除權限（只有檢視權限的人看不到已刪除的資料與刪除者）。</summary>
    private ObjectResult? ForbidDeletedList(bool deleted)
        => deleted
            ? this.ForbidUnless(PermissionRules.CanAccessAction(currentUserService.CurrentUser, MagicObjectHelper.角色_專案項目, PermissionActions.Delete), "檢視已刪除的專案需要刪除權限。")
            : null;

    private static DataRequest ToDataRequest(ProjectSearchRequestDto request)
    {
        var sortField = ControllerVerifyResultExtensions.ResolveSortField<ProjectAdapterModel>(request.SortBy, SortableFields);
        return new DataRequest
        {
            Search = request.Keyword ?? string.Empty,
            SortField = sortField ?? string.Empty,
            SortDescending = sortField is null ? null : request.SortDescending,
            CurrentPage = request.PageIndex,
            PageSize = request.PageSize,
            Take = request.PageSize,
            CategoryFilters = request.Categories ?? [],
            TeamFilters = request.Teams ?? [],
        };
    }

    private static ProjectListFilter ToFilter(ProjectSearchRequestDto request) => new()
    {
        Owner = request.Owner,
        Status = request.Status,
        Priority = request.Priority,
        StartDateFrom = request.StartDateFrom,
        StartDateTo = request.StartDateTo,
        CompletionPercentageMin = request.CompletionPercentageMin,
        CompletionPercentageMax = request.CompletionPercentageMax,
    };

    /// <summary>上傳的檔案轉成服務的輸入；串流在請求結束前關閉。</summary>
    private sealed class UploadInputs : IDisposable
    {
        private readonly List<Stream> streams = [];

        public UploadInputs(IReadOnlyList<IFormFile> files)
        {
            Inputs = files.Select(file =>
            {
                var stream = file.OpenReadStream();
                streams.Add(stream);
                return new ProjectUploadFileInput
                {
                    FileName = file.FileName,
                    ContentType = file.ContentType,
                    FileSize = file.Length,
                    Content = stream,
                };
            }).ToList();
        }

        public List<ProjectUploadFileInput> Inputs { get; }

        public void Dispose()
        {
            foreach (var stream in streams)
            {
                stream.Dispose();
            }
        }
    }
}

/// <summary>帶附件的新增／修改（multipart/form-data）。</summary>
public sealed class ProjectFormRequest
{
    /// <summary>專案資料（與 JSON 版本相同的 <see cref="ProjectCreateUpdateDto"/>，序列化成字串）。</summary>
    public string? Data { get; set; }

    /// <summary>新增的附件（可多個，每個上限 1 GB）。</summary>
    public List<IFormFile> Files { get; set; } = [];

    /// <summary>修改時要移除的既有附件 Id。</summary>
    public List<int> RemovedFileIds { get; set; } = [];

    /// <summary><see cref="Data"/> 的驗證錯誤（不是請求欄位）。</summary>
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
    internal Dictionary<string, string[]> Errors { get; } = [];
}
