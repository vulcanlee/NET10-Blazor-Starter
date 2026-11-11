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
using MyProject.Share.Helpers;
using MyProject.Web.Auth;
using MyProject.Web.Client.Api;
using MyProject.Web.Components.Commons;
using MyProject.Web.Export;
using MyProject.Web.Filters;

namespace MyProject.Web.Controllers;

/// <summary>
/// 使用者的 Web API（0.10.4 起，WASM 遷移 M5）：只限管理員，走與畫面相同的 <see cref="MyUserService"/>／<see cref="ITwoFactorService"/>
/// （support 與自己的保護、密碼原則與歷史、角色與團隊同一個交易、並行衝突、軟刪除）。
/// 寫入的稽核由服務寫；查看（開修改窗）、搜尋、匯出由這裡寫，與 Server 版相同。
///
/// 停用、強制登出、重設兩步驟驗證、改密碼／角色／管理員身分會換對方的工作階段版本：對方下一個請求就被登出（API 401、刷新被拒）。
/// 改到自己時，0.10.8 起直接回這台裝置的新 Token（<see cref="SessionRenewalDto.Tokens"/>），這台裝置維持登入。
/// </summary>
[Route("api/v1/users")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[AdminOnly]
public class UsersController : ControllerBase
{
    private const string TargetType = "MyUser";

    private static readonly string[] SortableFields =
    [
        nameof(MyUserAdapterModel.Account),
        nameof(MyUserAdapterModel.Name),
        nameof(MyUserAdapterModel.Email),
        nameof(MyUserAdapterModel.RoleViewName),
        nameof(MyUserAdapterModel.StatusText),
        nameof(MyUserAdapterModel.IsAdminText),
        nameof(MyUserAdapterModel.CreateAt),
        nameof(MyUserAdapterModel.UpdateAt),
    ];

    private readonly MyUserService userService;
    private readonly RoleViewService roleViewService;
    private readonly TeamService teamService;
    private readonly ITwoFactorService twoFactorService;
    private readonly IPasswordPolicy passwordPolicy;
    private readonly SelfSessionRenewal sessionRenewal;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly ILogger<UsersController> logger;

    public UsersController(
        MyUserService userService,
        RoleViewService roleViewService,
        TeamService teamService,
        ITwoFactorService twoFactorService,
        IPasswordPolicy passwordPolicy,
        SelfSessionRenewal sessionRenewal,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        ILogger<UsersController> logger)
    {
        this.userService = userService;
        this.roleViewService = roleViewService;
        this.teamService = teamService;
        this.twoFactorService = twoFactorService;
        this.passwordPolicy = passwordPolicy;
        this.sessionRenewal = sessionRenewal;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.logger = logger;
    }

    private int MyId => currentUserService.CurrentUser.Id;

    /// <summary>清單（每列帶可用的操作）；<see cref="UserSearchRequestDto.RecordSearch"/> 為 true 時寫一筆搜尋稽核。</summary>
    [HttpPost("search")]
    public async Task<ActionResult<ApiResult<PagedResult<UserDto>>>> Search([FromBody] UserSearchRequestDto request)
    {
        var dataRequest = ToDataRequest(request, request.PageIndex, request.PageSize);
        var result = request.Deleted ? await userService.GetDeletedAsync(dataRequest) : await userService.GetAsync(dataRequest);
        if (request.RecordSearch)
        {
            await ViewAudit.WriteSearchAsync(auditLogService, currentUserService, AuditActions.User.Search, TargetType,
                AuditSearchCondition.Text("keyword", request.Keyword), AuditSearchCondition.Of("showDeleted", request.Deleted));
        }

        return Ok(ApiResult<PagedResult<UserDto>>.SuccessResult(new PagedResult<UserDto>
        {
            Items = result.Result.Select(ToDto).ToList(),
            TotalCount = result.Count,
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            TotalPages = (int)Math.Ceiling(result.Count / (double)request.PageSize),
        }, "搜尋使用者成功"));
    }

    /// <summary>單筆（含額外角色與團隊；WASM 頁開修改窗時呼叫）；寫一筆「查看」稽核。</summary>
    [HttpGet("{id:int}")]
    public async Task<ActionResult<ApiResult<UserDto>>> GetById(int id)
    {
        var item = await userService.GetAsync(id);
        if (item.Id == 0)
        {
            return NotFound(ApiResult<UserDto>.NotFoundResult($"找不到 ID 為 {id} 的使用者"));
        }

        var (additionalRoleIds, teamNames) = await userService.GetUserAssignmentsAsync(id);
        item.AdditionalRoleIds = additionalRoleIds;
        item.TeamNames = teamNames;
        await ViewAudit.WriteAsync(auditLogService, currentUserService, AuditActions.User.View, TargetType, id.ToString(), $"account={item.Account}");
        return Ok(ApiResult<UserDto>.SuccessResult(ToDto(item), "取得使用者成功"));
    }

    /// <summary>表單選項：角色（含預設團隊）、啟用中的團隊、新增時的預設角色、密碼規則說明。</summary>
    [HttpGet("options")]
    public async Task<ActionResult<ApiResult<UserFormOptionsDto>>> Options()
    {
        var roles = await userService.GetRoleViewsAsync();
        int? defaultRoleId = null;
        try
        {
            var defaultRole = await roleViewService.Get預設新建帳號角色Async();
            defaultRoleId = defaultRole.Id != 0 ? defaultRole.Id : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load default role for new user creation.");
        }

        return Ok(ApiResult<UserFormOptionsDto>.SuccessResult(new UserFormOptionsDto
        {
            Roles = roles.Select(x => new UserRoleOptionDto { Id = x.Id, Name = x.Name, DefaultTeams = [.. x.DefaultTeams] }).ToList(),
            Teams = await teamService.GetAllEnabledNamesAsync(),
            DefaultRoleId = defaultRoleId ?? roles.FirstOrDefault()?.Id,
            PasswordRule = passwordPolicy.Describe(),
        }, "取得使用者表單選項成功"));
    }

    /// <summary>「複製」的「產生」：一組符合密碼原則的隨機密碼（只回給這次請求，不存）。</summary>
    [HttpGet("password/generate")]
    public ActionResult<ApiResult<string>> GeneratePassword()
    {
        // 明碼密碼：不可被瀏覽器或中間的代理快取（審查發現）。
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        return Ok(ApiResult<string>.SuccessResult(passwordPolicy.Generate(), "已產生密碼"));
    }

    /// <summary>新增前的檢查（帳號是否已存在、Email 格式）：「複製」建立時與 Server 版同順序，先檢查帳號再問「Email 已有人使用」。</summary>
    [HttpPost("check")]
    public async Task<ActionResult<ApiResult>> Check([FromBody] UserDto dto)
    {
        var model = UserDtoMapper.ToModel(dto);
        model.Id = 0;
        return this.FromVerify(await userService.BeforeAddCheckAsync(model), "可以建立");
    }

    /// <summary>「複製」的範本：support 或已不存在的回 404。</summary>
    [HttpGet("{id:int}/copy-draft")]
    public async Task<ActionResult<ApiResult<UserCopyDraftDto>>> CopyDraft(int id)
    {
        if (await userService.BuildCopyDraftAsync(id) is not { } copy)
        {
            return NotFound(ApiResult<UserCopyDraftDto>.NotFoundResult("這位使用者不能當作複製範本，或已不存在，請重新整理後再試。"));
        }

        return Ok(ApiResult<UserCopyDraftDto>.SuccessResult(new UserCopyDraftDto
        {
            Draft = UserDtoMapper.ToDto(copy.Draft),
            SkippedTeamNames = copy.SkippedTeamNames,
        }, "取得複製範本成功"));
    }

    /// <summary>這個 Email 已被哪些帳號使用（複製建立前提醒「重設信會寄到同一個信箱」）。</summary>
    [HttpPost("accounts-using-email")]
    public async Task<ActionResult<ApiResult<List<string>>>> AccountsUsingEmail([FromBody] EmailLookupRequestDto request)
        => Ok(ApiResult<List<string>>.SuccessResult(
            string.IsNullOrWhiteSpace(request.Email) ? [] : await userService.GetAccountsUsingEmailAsync(request.Email), "查詢完成"));

    /// <summary>新增（一般新增與「複製」共用；<see cref="UserDto.CopiedFromAccount"/> 有值時寫進稽核）。</summary>
    [HttpPost]
    public async Task<ActionResult<ApiResult<UserDto>>> Create([FromBody] UserDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Password))
        {
            return BadRequest(ApiResult<UserDto>.ValidationError("新增使用者時必須輸入密碼。"));
        }

        var model = UserDtoMapper.ToModel(dto);
        model.Id = 0;
        model.CreateAt = DateTime.Now;
        model.UpdateAt = DateTime.Now;
        if (Invalid(model) is { } invalid)
        {
            return BadRequest(ApiResult<UserDto>.ValidationError(invalid));
        }

        var check = await userService.BeforeAddCheckAsync(model);
        if (!check.Success)
        {
            return this.FromVerify<UserDto>(check, () => null!, string.Empty);
        }

        var result = await userService.AddAsync(model, string.IsNullOrWhiteSpace(dto.CopiedFromAccount) ? null : dto.CopiedFromAccount);
        if (!result.Success)
        {
            return this.FromVerify<UserDto>(result, () => null!, string.Empty);
        }

        var saved = await userService.GetAsync(model.Id);
        return Ok(ApiResult<UserDto>.SuccessResult(ToDto(saved.Id == 0 ? model : saved), "新增使用者成功"));
    }

    /// <summary>修改；改到自己而且工作階段版本換掉時，回這台裝置的新 Token。</summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult<ApiResult<UserSaveResultDto>>> Update(int id, [FromBody] UserDto dto)
    {
        if (id != dto.Id)
        {
            return BadRequest(ApiResult<UserSaveResultDto>.ValidationError("路由 ID 與資料 ID 不一致"));
        }

        var model = UserDtoMapper.ToModel(dto);
        model.UpdateAt = DateTime.Now;
        if (Invalid(model) is { } invalid)
        {
            return BadRequest(ApiResult<UserSaveResultDto>.ValidationError(invalid));
        }

        var check = await userService.BeforeUpdateCheckAsync(model);
        if (!check.Success)
        {
            return this.FromVerify<UserSaveResultDto>(check, () => null!, string.Empty);
        }

        var result = await userService.UpdateAsync(model);
        if (!result.Success)
        {
            return this.FromVerify<UserSaveResultDto>(result, () => null!, string.Empty);
        }

        var saved = id == MyId
            ? await sessionRenewal.RenewAsync<UserSaveResultDto>(User, id, "SelfUpdated")
            : new UserSaveResultDto();
        return Ok(ApiResult<UserSaveResultDto>.SuccessResult(saved, "更新使用者成功"));
    }

    /// <summary>點狀態標籤啟用／停用（support 與自己不可停用）。停用會換對方的工作階段版本。</summary>
    [HttpPut("{id:int}/enabled")]
    public async Task<ActionResult<ApiResult>> SetEnabled(int id, [FromBody] SetEnabledRequestDto request)
        => this.FromVerify(await userService.SetStatusAsync(id, request.Enabled), request.Enabled ? "已啟用" : "已停用");

    [HttpPost("{id:int}/unlock")]
    public async Task<ActionResult<ApiResult>> Unlock(int id)
        => this.FromVerify(await userService.UnlockAsync(id), "已解除鎖定");

    /// <summary>強制登出（所有瀏覽器與 API）。不可對自己做（請用右上角的登出）：Server 版只在畫面上擋，API 也擋。</summary>
    [HttpPost("{id:int}/force-logout")]
    public async Task<ActionResult<ApiResult>> ForceLogout(int id)
    {
        if (id == MyId)
        {
            return BadRequest(ApiResult.ValidationError("不可強制登出自己的帳號，請使用右上角的登出。"));
        }

        return this.FromVerify(await userService.ForceLogoutAsync(id), "已強制登出");
    }

    /// <summary>重設兩步驟驗證（清除驗證器與備用碼，對方所有登入都會登出）。不可對自己做（自己到「兩步驟驗證」頁處理）。</summary>
    [HttpPost("{id:int}/two-factor/reset")]
    public async Task<ActionResult<ApiResult>> ResetTwoFactor(int id)
    {
        if (id == MyId)
        {
            return BadRequest(ApiResult.ValidationError("不可重設自己的兩步驟驗證，請到「兩步驟驗證」頁停用或重新設定。"));
        }

        return this.FromVerify(await twoFactorService.ResetAsync(id), "已重設兩步驟驗證");
    }

    /// <summary>軟刪除（support 與自己不可刪）；會換對方的工作階段版本並刪掉他的重設密碼連結。</summary>
    [HttpDelete("{id:int}")]
    public async Task<ActionResult<ApiResult>> Delete(int id)
        => this.FromVerify(await userService.DeleteAsync(id), "刪除使用者成功");

    [HttpPost("{id:int}/restore")]
    public async Task<ActionResult<ApiResult>> Restore(int id)
        => this.FromVerify(await userService.RestoreAsync(id), "還原成功");

    /// <summary>永久刪除：只能對已刪除的使用者執行，角色與團隊設定一併刪除。</summary>
    [HttpDelete("{id:int}/purge")]
    public async Task<ActionResult<ApiResult>> Purge(int id)
        => this.FromVerify(await userService.PurgeAsync(id), "已永久刪除");

    /// <summary>匯出 Excel：與清單同一個查詢，超過上限筆數回 400 與原因。</summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] UserSearchRequestDto request)
    {
        var outcome = await BusinessExports.BuildAsync(
            r => request.Deleted ? userService.GetDeletedAsync(r) : userService.GetAsync(r),
            ToDataRequest(request, 1, request.PageSize), "使用者", "users", BusinessExports.UserColumns);
        if (outcome.Error is { } error)
        {
            return BadRequest(ApiResult.ValidationError(error));
        }

        logger.LogInformation("User export downloaded. Rows={Rows}", outcome.Rows);
        await BusinessExports.WriteAuditAsync(auditLogService, currentUserService, AuditActions.User.Export, TargetType, outcome.Rows, request.Deleted);
        return File(outcome.Content!, TabularExport.XlsxContentType, outcome.FileName);
    }

    /// <summary>畫面上的必填（帳號、名稱、角色）由伺服器再驗一次：API 呼叫端不經過畫面的表單驗證（審查發現）。</summary>
    private static string? Invalid(MyUserAdapterModel model)
    {
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        var ok = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(model, new(model), results, validateAllProperties: true);
        if (string.IsNullOrWhiteSpace(model.Account))
        {
            results.Add(new("帳號 不可為空白"));
        }

        if (string.IsNullOrWhiteSpace(model.Name))
        {
            results.Add(new("名稱 不可為空白"));
        }

        return ok && results.Count == 0 ? null : string.Join(" ", results.Select(x => x.ErrorMessage).Distinct());
    }

    private static DataRequest ToDataRequest(UserSearchRequestDto request, int page, int pageSize)
    {
        var sortField = ControllerVerifyResultExtensions.ResolveSortField<MyUserAdapterModel>(request.SortBy, SortableFields);
        return new DataRequest
        {
            Search = request.Keyword ?? string.Empty,
            SortField = sortField ?? string.Empty,
            SortDescending = sortField is null ? null : request.SortDescending,
            CurrentPage = page,
            PageSize = pageSize,
            Take = pageSize,
        };
    }

    /// <summary>清單與單筆的 DTO：可用的操作、鎖定文字（伺服器本地時間）由這裡算，規則與 Server 版畫面相同。</summary>
    private UserDto ToDto(MyUserAdapterModel item)
    {
        var dto = UserDtoMapper.ToDto(item);
        dto.Password = null;
        dto.CanDelete = userService.CanDelete(item);
        dto.CanCopy = userService.CanCopy(item);
        dto.CanToggleStatus = userService.CanToggleStatus(item);
        dto.CanForceLogout = item.Id != MyId;
        dto.CanResetTwoFactor = item.TwoFactorEnabled && item.Id != MyId;
        if (item.LockoutEndUtc is { } end && end > DateTime.UtcNow)
        {
            var local = DateTime.SpecifyKind(end, DateTimeKind.Utc).ToLocalTime();
            dto.LockedText = local.Date == DateTime.Today ? $"鎖定至 {local:HH:mm}" : $"鎖定至 {local:MM-dd HH:mm}";
        }

        return dto;
    }
}
