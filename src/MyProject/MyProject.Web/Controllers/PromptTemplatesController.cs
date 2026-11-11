using System.Globalization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Commons;
using MyProject.Web.Ai;
using MyProject.Web.Components.Commons;
using MyProject.Web.Filters;

namespace MyProject.Web.Controllers;

/// <summary>
/// AI 提示詞頁的 Web API（0.10.5 起，WASM 遷移 M6），只限管理員。走畫面同一個 <see cref="PromptTemplateService"/>；
/// 內建預設與固定附加的規則（<see cref="AiPromptGuardrails"/>）只在伺服器，由這裡帶給畫面顯示。
/// 稽核與 Server 版頁面相同：存成新版本一筆 <c>Prompt.Update</c>（<c>version=N</c>）、切換一筆 <c>Prompt.Activate</c>（<c>version=N|default</c>）。
/// </summary>
[Route("api/v1/prompt-templates")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class PromptTemplatesController : ControllerBase
{
    private const string TargetType = "PromptTemplate";
    private const string UnknownKeyMessage = "提示詞種類不正確。";

    private readonly PromptTemplateService service;
    private readonly IAuditLogService auditLogService;
    private readonly CurrentUserService currentUserService;
    private readonly ILogger<PromptTemplatesController> logger;

    public PromptTemplatesController(
        PromptTemplateService service,
        IAuditLogService auditLogService,
        CurrentUserService currentUserService,
        ILogger<PromptTemplatesController> logger)
    {
        this.service = service;
        this.auditLogService = auditLogService;
        this.currentUserService = currentUserService;
        this.logger = logger;
    }

    /// <summary>全部種類（依頁籤順序）：標題、版本紀錄（新到舊，含作用中）、內建預設與固定附加的規則。</summary>
    [HttpGet]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<List<PromptTemplateDto>>>> GetAll()
    {
        var templates = new List<PromptTemplateDto>();
        foreach (var key in PromptTemplateKeys.All)
        {
            var history = await service.GetHistoryAsync(key);
            templates.Add(new PromptTemplateDto
            {
                Key = key,
                Title = PromptTemplateTitles.Of(key),
                DefaultInstructions = AiPromptGuardrails.DefaultInstructions(key),
                LockedRules = AiPromptGuardrails.For(key),
                Versions = history.Select(x => new PromptTemplateVersionDto
                {
                    Version = x.Version,
                    Content = x.Content,
                    IsActive = x.IsActive,
                    Note = x.Note,
                    CreatedByAccount = x.CreatedByAccount,
                    // 服務已換成伺服器本地時間；不帶時差送出，WASM 才不會換成瀏覽器的時區。
                    CreatedAt = DateTime.SpecifyKind(x.CreatedAt, DateTimeKind.Unspecified),
                }).ToList(),
            });
        }

        return Ok(ApiResult<List<PromptTemplateDto>>.SuccessResult(templates, "取得 AI 提示詞成功"));
    }

    /// <summary>
    /// 存成新版本並設為作用中；開窗後別人改過（<see cref="PromptTemplateSaveRequestDto.ExpectedActiveVersion"/> 不是目前的作用中版本）回 409。
    /// 建立者是目前登入的帳號（不信任畫面送來的值）。
    /// </summary>
    [HttpPost("{key}/versions")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<PromptTemplateSaveResultDto>>> Save(string key, [FromBody] PromptTemplateSaveRequestDto request)
    {
        if (!PromptTemplateKeys.All.Contains(key))
        {
            return NotFound(ApiResult<PromptTemplateSaveResultDto>.NotFoundResult(UnknownKeyMessage));
        }

        var (result, version) = await service.SaveNewVersionAsync(
            key, request.Content ?? string.Empty, request.Note, request.ExpectedActiveVersion, currentUserService.CurrentUser.Account);
        if (result.Success)
        {
            await ViewAudit.WriteAsync(auditLogService, currentUserService, AuditActions.Prompt.Update, TargetType, key, $"version={version}");
        }
        else
        {
            logger.LogInformation("Prompt template version was not saved. TemplateKey={TemplateKey}, Kind={Kind}", key, result.Kind);
        }

        return this.FromVerify(result, () => new PromptTemplateSaveResultDto { Version = version }, $"已存成第 {version} 版");
    }

    /// <summary>切換作用中的版本；<see cref="PromptTemplateActivateRequestDto.Version"/> 為 null 代表改用內建預設。</summary>
    [HttpPost("{key}/activate")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult>> Activate(string key, [FromBody] PromptTemplateActivateRequestDto request)
    {
        if (!PromptTemplateKeys.All.Contains(key))
        {
            return NotFound(ApiResult.NotFoundResult(UnknownKeyMessage));
        }

        var result = await service.ActivateAsync(key, request.Version);
        if (result.Success)
        {
            await ViewAudit.WriteAsync(auditLogService, currentUserService, AuditActions.Prompt.Activate, TargetType, key,
                $"version={(request.Version is { } n ? n.ToString(CultureInfo.InvariantCulture) : "default")}");
        }

        return this.FromVerify(result, "切換成功");
    }
}
