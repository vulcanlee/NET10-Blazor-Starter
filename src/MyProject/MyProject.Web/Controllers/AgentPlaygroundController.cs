using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Ai;
using MyProject.Dtos.Commons;
using MyProject.Share.Helpers;
using MyProject.Web.Ai.Agents;
using MyProject.Web.Configuration;
using MyProject.Web.Filters;

namespace MyProject.Web.Controllers;

/// <summary>
/// Agent 測試台（/agent-playground）的 Web API（0.10.7 起，WASM 遷移 M8）：頁面權限「Agent 測試台」（view），
/// 產生／清除測試專案另要專案項目的新增／刪除權限（同 Server 版的按鈕）。
/// 對話流程（每個請求接續一次、NDJSON 串流、併發、核准規則）見 <see cref="AgentConversationEndpoint"/>。
/// 稽核與 Server 版相同、都在原本的服務裡：工具執行／被拒（工具外殼）、拒絕核准（對話物件）、刪除對話（對話服務）、測試專案（專案服務）；
/// 改名與開啟對話不寫稽核。
/// </summary>
[Route("api/v1/agent-playground")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public sealed class AgentPlaygroundController : ControllerBase
{
    private readonly AgentConversationEndpoint endpoint;
    private readonly IAiAgentRunner runner;
    private readonly AiAgentConversationService conversationService;
    private readonly ProjectSampleDataService sampleDataService;
    private readonly CurrentUserService currentUserService;
    private readonly IOptionsMonitor<AiReasoningSettings> reasoningSettings;
    private readonly IOptionsMonitor<AiSettings> aiSettings;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<AgentPlaygroundController> logger;
    private readonly AgentConversationProfile profile;

    public AgentPlaygroundController(
        AgentConversationEndpoint endpoint,
        IAiAgentRunner runner,
        AiAgentConversationService conversationService,
        ProjectSampleDataService sampleDataService,
        CurrentUserService currentUserService,
        IOptionsMonitor<AiReasoningSettings> reasoningSettings,
        IOptionsMonitor<AiSettings> aiSettings,
        TimeProvider timeProvider,
        ILogger<AgentPlaygroundController> logger)
    {
        this.endpoint = endpoint;
        this.runner = runner;
        this.conversationService = conversationService;
        this.sampleDataService = sampleDataService;
        this.currentUserService = currentUserService;
        this.reasoningSettings = reasoningSettings;
        this.aiSettings = aiSettings;
        this.timeProvider = timeProvider;
        this.logger = logger;
        profile = new AgentConversationProfile
        {
            AgentName = AgentPlaygroundAgent.Definition.Name,
            StartAsync = (effort, ct) => runner.StartAsync(AgentPlaygroundAgent.Definition, effort, ct),
            ResumeAsync = (id, ct) => runner.ResumeAsync(AgentPlaygroundAgent.Definition, id, ct),
            SupportsReasoningEffort = true,
            UnexpectedMessage = "Agent 執行發生未預期的錯誤，請稍後再試。",
        };
    }

    /// <summary>開頁資訊：AI 是否可用、保存天數、推論等級選項與預設、測試專案的權限。</summary>
    [HttpGet("info")]
    [HasPermission(MagicObjectHelper.角色_Agent測試台, PermissionActions.View)]
    public ActionResult<ApiResult<AgentPageInfoDto>> Info()
    {
        var user = currentUserService.CurrentUser;
        var value = reasoningSettings.CurrentValue.GetOverride(AgentPlaygroundAgent.Definition.ModelKey)
            ?? aiSettings.CurrentValue.AgentReasoningEffort?.Trim().ToLowerInvariant();
        return Ok(ApiResult<AgentPageInfoDto>.SuccessResult(new AgentPageInfoDto
        {
            DisplayName = AgentPlaygroundAgent.Definition.DisplayName,
            UnavailableReason = runner.UnavailableReason,
            RetentionDays = conversationService.RetentionDays,
            ReasoningEffortValues = [.. AiSettings.AgentReasoningEffortValues],
            SystemDefaultReasoningLabel = string.IsNullOrEmpty(value) ? "系統預設（不指定，由模型決定）" : $"系統預設（目前：{value}）",
            CanCreateSamples = PermissionRules.CanAccessAction(user, MagicObjectHelper.角色_專案項目, PermissionActions.Create),
            CanClearSamples = PermissionRules.CanAccessAction(user, MagicObjectHelper.角色_專案項目, PermissionActions.Delete),
            SampleTitlePrefix = ProjectSampleDataService.TitlePrefix,
        }));
    }

    [HttpGet("conversations")]
    [HasPermission(MagicObjectHelper.角色_Agent測試台, PermissionActions.View)]
    public async Task<ActionResult<ApiResult<List<AgentConversationSummaryDto>>>> List()
        => Ok(ApiResult<List<AgentConversationSummaryDto>>.SuccessResult(await endpoint.ListAsync(profile)));

    [HttpGet("conversations/{id:guid}")]
    [HasPermission(MagicObjectHelper.角色_Agent測試台, PermissionActions.View)]
    public Task<ActionResult<ApiResult<AgentConversationDto>>> Get(Guid id) => endpoint.GetAsync(this, profile, id);

    [HttpPut("conversations/{id:guid}/title")]
    [HasPermission(MagicObjectHelper.角色_Agent測試台, PermissionActions.View)]
    public Task<ActionResult<ApiResult>> Rename(Guid id, [FromBody] AgentRenameRequestDto request) => endpoint.RenameAsync(this, profile, id, request.Title);

    [HttpDelete("conversations/{id:guid}")]
    [HasPermission(MagicObjectHelper.角色_Agent測試台, PermissionActions.View)]
    public Task<ActionResult<ApiResult>> Delete(Guid id) => endpoint.DeleteAsync(this, profile, id);

    /// <summary>送出訊息（新對話或接續），回 NDJSON 串流。</summary>
    [HttpPost("messages")]
    [HasPermission(MagicObjectHelper.角色_Agent測試台, PermissionActions.View)]
    public Task<IActionResult> Send([FromBody] AgentMessageRequestDto request) => endpoint.SendAsync(this, profile, request);

    /// <summary>回覆待核准的工具，回 NDJSON 串流。</summary>
    [HttpPost("conversations/{id:guid}/approvals")]
    [HasPermission(MagicObjectHelper.角色_Agent測試台, PermissionActions.View)]
    public Task<IActionResult> Respond(Guid id, [FromBody] AgentApprovalResponseRequestDto request) => endpoint.RespondAsync(this, profile, id, request);

    /// <summary>產生 10 筆測試專案（日期以伺服器的今天為基準）。已有測試專案時 409。</summary>
    [HttpPost("samples")]
    [HasPermission(MagicObjectHelper.角色_Agent測試台, PermissionActions.View)]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.Create)]
    public async Task<ActionResult<ApiResult<AgentSampleResultDto>>> GenerateSamples()
    {
        try
        {
            var result = await sampleDataService.GenerateAsync(timeProvider.GetLocalNow().DateTime);
            if (result.Success)
            {
                return Ok(ApiResult<AgentSampleResultDto>.SuccessResult(new AgentSampleResultDto { Count = result.Count, Titles = [.. result.Titles] }, result.Message));
            }

            // 已經有測試專案（一筆都沒建）是提醒（畫面用提醒樣式），建到一半失敗是錯誤。
            var body = result.Count == 0
                ? ApiResult<AgentSampleResultDto>.ConflictResult(result.Message)
                : ApiResult<AgentSampleResultDto>.ServerErrorResult(result.Message);
            body.TraceId = HttpContext.TraceIdentifier;
            return StatusCode(body.StatusCode, body);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent playground failed to generate sample projects.");
            return this.ApiServerError<AgentSampleResultDto>("產生測試專案失敗，請稍後再試。", ex);
        }
    }

    /// <summary>永久刪除所有測試專案（含已刪除的）。</summary>
    [HttpDelete("samples")]
    [HasPermission(MagicObjectHelper.角色_Agent測試台, PermissionActions.View)]
    [HasPermission(MagicObjectHelper.角色_專案項目, PermissionActions.Delete)]
    public async Task<ActionResult<ApiResult>> ClearSamples()
    {
        try
        {
            var result = await sampleDataService.ClearAsync();
            if (result.Success)
            {
                return Ok(ApiResult.SuccessResult(result.Message));
            }

            var body = ApiResult.ServerErrorResult(result.Message);
            body.TraceId = HttpContext.TraceIdentifier;
            return StatusCode(StatusCodes.Status500InternalServerError, body);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent playground failed to clear sample projects.");
            return this.ApiServerError("清除測試專案失敗，請稍後再試。", ex);
        }
    }
}
