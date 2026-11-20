using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.Business.Services.DataAccess;
using MyProject.Dtos.Admins;
using MyProject.Dtos.Ai;
using MyProject.Dtos.Commons;
using MyProject.Web.Ai.Agents;
using MyProject.Web.Ai.Agents.AdminAssistant;
using MyProject.Web.Client.Api;
using MyProject.Web.Filters;

namespace MyProject.Web.Controllers;

/// <summary>
/// 系統管理助理（/admin-assistant）的 Web API（0.10.7 起，WASM 遷移 M8），只限管理員（權限鍵刻意不進角色矩陣）。
/// 對話流程見 <see cref="AgentConversationEndpoint"/>：每則訊息先分派領域（<see cref="AdminAssistantTurnPlanner"/>），
/// 停在核准的對話接續時沿用提出核准的那一輪（存在對話的 TurnKey），不會變成「全部工具＋全部領域」。
/// 巡檢報告 0.10.39 起改由「系統巡檢報告」頁的 API（<see cref="SystemReportsController"/>）提供。
/// </summary>
[Route("api/v1/admin-assistant")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public sealed class AdminAssistantController : ControllerBase
{
    private readonly AgentConversationEndpoint endpoint;
    private readonly IAiAgentRunner runner;
    private readonly AiAgentConversationService conversationService;
    private readonly ILogger<AdminAssistantController> logger;
    private readonly AgentConversationProfile profile;

    public AdminAssistantController(
        AgentConversationEndpoint endpoint,
        IAiAgentRunner runner,
        AdminAssistantTurnPlanner planner,
        AiAgentConversationService conversationService,
        ILogger<AdminAssistantController> logger)
    {
        this.endpoint = endpoint;
        this.runner = runner;
        this.conversationService = conversationService;
        this.logger = logger;
        profile = new AgentConversationProfile
        {
            AgentName = AdminAssistantAgent.Name,
            StartAsync = (_, ct) => runner.StartAsync(planner, ct),
            ResumeAsync = (id, ct) => runner.ResumeAsync(planner, id, ct),
            UnexpectedMessage = "系統管理助理發生未預期的錯誤，請稍後再試。",
        };
    }

    /// <summary>開頁資訊：AI 是否可用、對話保存天數。</summary>
    [HttpGet("info")]
    [AdminOnly]
    public ActionResult<ApiResult<AgentPageInfoDto>> Info()
        => Ok(ApiResult<AgentPageInfoDto>.SuccessResult(new AgentPageInfoDto
        {
            DisplayName = AdminAssistantAgent.DisplayName,
            UnavailableReason = runner.UnavailableReason,
            RetentionDays = conversationService.RetentionDays,
        }));

    [HttpGet("conversations")]
    [AdminOnly]
    public async Task<ActionResult<ApiResult<List<AgentConversationSummaryDto>>>> List()
        => Ok(ApiResult<List<AgentConversationSummaryDto>>.SuccessResult(await endpoint.ListAsync(profile)));

    [HttpGet("conversations/{id:guid}")]
    [AdminOnly]
    public Task<ActionResult<ApiResult<AgentConversationDto>>> Get(Guid id) => endpoint.GetAsync(this, profile, id);

    [HttpPut("conversations/{id:guid}/title")]
    [AdminOnly]
    public Task<ActionResult<ApiResult>> Rename(Guid id, [FromBody] AgentRenameRequestDto request) => endpoint.RenameAsync(this, profile, id, request.Title);

    [HttpDelete("conversations/{id:guid}")]
    [AdminOnly]
    public Task<ActionResult<ApiResult>> Delete(Guid id) => endpoint.DeleteAsync(this, profile, id);

    /// <summary>送出訊息（新對話或接續），回 NDJSON 串流。</summary>
    [HttpPost("messages")]
    [AdminOnly]
    public Task<IActionResult> Send([FromBody] AgentMessageRequestDto request) => endpoint.SendAsync(this, profile, request);

    /// <summary>回覆待核准的工具（不可復原的動作要帶「確認」），回 NDJSON 串流。</summary>
    [HttpPost("conversations/{id:guid}/approvals")]
    [AdminOnly]
    public Task<IActionResult> Respond(Guid id, [FromBody] AgentApprovalResponseRequestDto request) => endpoint.RespondAsync(this, profile, id, request);
}
