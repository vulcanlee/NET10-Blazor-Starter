using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Ai;
using MyProject.Dtos.Commons;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Ai.Workflows;
using MyProject.Web.Client.Api;
using MyProject.Web.Filters;
using MyProject.Web.Streaming;

namespace MyProject.Web.Controllers;

/// <summary>
/// Agent 工作流程頁的 Web API（0.10.7 起，WASM 遷移 M8），需要「Agent 工作流程」頁面權限。
///
/// <para>
/// 一次執行要跨好幾個請求：<c>POST runs</c> 開始，串流（NDJSON）到停下來等使用者或結束；<c>POST runs/{id}/responses</c> 回覆後繼續串流；
/// 執行物件放在 <see cref="AiWorkflowRunRegistry"/>（自己的 scope、只有擁有者能操作、閒置 30 分鐘結束成「中斷」、只在單一行程有效）。
/// 重新整理頁面後以 <c>GET runs/active</c> 接回停下來等回覆的那一次。
/// </para>
/// <para>
/// 串流途中瀏覽器斷線（重新整理、關分頁、離開頁面）視同 Server 版的「離開頁面」：整個流程取消並存成「中斷」，不在背景繼續花 AI 費用。
/// 停下來等使用者時沒有開著的串流，重新整理不影響它。
/// </para>
/// <para>
/// 稽核與 Server 版相同、都不在這裡寫：工具的 Ai.ToolInvoked／ToolDenied／ToolRejected 由工具外殼以執行 scope 裡的目前使用者寫，
/// 刪除紀錄的 AiAgentConversation.Delete 由服務寫；看頁面、看紀錄、改名不寫。
/// </para>
/// </summary>
[Route("api/v1/agent-workflows")]
[ApiController]
[ApiValidationFilter]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
[HasPermission(MagicObjectHelper.角色_Agent工作流程)]
public class AgentWorkflowsController : ControllerBase
{
    /// <summary>串流閒置多久插一筆心跳。</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private const string RecordNotFoundMessage = "這筆紀錄的內容已不存在（可能已被清除）。可以刪除它。";
    private const string RunNotFoundMessage = "找不到這次執行（可能已經結束，或閒置太久而中斷）。";
    private const string ActiveRunExistsMessage = "你還有一次工作流程在執行或等你回覆（可能在其他分頁），請先完成或放棄它。";

    private readonly AiWorkflowRunRegistry registry;
    private readonly IAiWorkflowRunner runner;
    private readonly AiAgentConversationService conversationService;
    private readonly CurrentUserService currentUserService;
    private readonly ILogger<AgentWorkflowsController> logger;

    public AgentWorkflowsController(
        AiWorkflowRunRegistry registry,
        IAiWorkflowRunner runner,
        AiAgentConversationService conversationService,
        CurrentUserService currentUserService,
        ILogger<AgentWorkflowsController> logger)
    {
        this.registry = registry;
        this.runner = runner;
        this.conversationService = conversationService;
        this.currentUserService = currentUserService;
        this.logger = logger;
    }

    private int UserId => currentUserService.CurrentUser.Id;

    /// <summary>頁面資訊：可選的工作流程、AI 是否可用、紀錄保留天數。</summary>
    [HttpGet("info")]
    public ActionResult<ApiResult<AgentWorkflowPageInfoDto>> Info()
        => Ok(ApiResult<AgentWorkflowPageInfoDto>.SuccessResult(new AgentWorkflowPageInfoDto
        {
            UnavailableReason = runner.UnavailableReason,
            RetentionDays = conversationService.RetentionDays,
            Workflows = AiWorkflowCatalog.All.Select(AgentWorkflowMapper.ToDto).ToList(),
        }, "取得工作流程資訊成功"));

    /// <summary>「我的執行紀錄」：某一條工作流程，依最後執行時間由新到舊。</summary>
    [HttpGet("records")]
    public async Task<ActionResult<ApiResult<List<AgentWorkflowRecordDto>>>> Records([FromQuery] string? workflow)
    {
        if (FindWorkflow(workflow) is not { } definition)
        {
            return BadRequest(ApiResult<List<AgentWorkflowRecordDto>>.ValidationError("找不到這個工作流程。"));
        }

        var records = await conversationService.ListMineAsync(definition.Name);
        return Ok(ApiResult<List<AgentWorkflowRecordDto>>.SuccessResult(records.Select(AgentWorkflowMapper.ToDto).ToList(), "取得執行紀錄成功"));
    }

    /// <summary>一筆之前的執行紀錄（唯讀回顧；不寫稽核，同 Server 版）。</summary>
    [HttpGet("records/{id:guid}")]
    public async Task<ActionResult<ApiResult<AgentWorkflowRecordDetailDto>>> Record(Guid id)
    {
        var loaded = await LoadRecordAsync(id);
        if (loaded?.Content is not { } content)
        {
            return NotFound(ApiResult<AgentWorkflowRecordDetailDto>.NotFoundResult(RecordNotFoundMessage));
        }

        return Ok(ApiResult<AgentWorkflowRecordDetailDto>.SuccessResult(new AgentWorkflowRecordDetailDto
        {
            ConversationId = id,
            Transcript = content.Transcript.Select(AgentWorkflowDtoMapper.ToDto).ToList(),
        }, "取得執行紀錄成功"));
    }

    /// <summary>重新命名（只給本人看，不寫稽核，同 Server 版）。</summary>
    [HttpPut("records/{id:guid}/title")]
    public async Task<ActionResult<ApiResult>> Rename(Guid id, [FromBody] AgentWorkflowRenameRequestDto request)
    {
        if (await LoadRecordAsync(id) is null)
        {
            return NotFound(ApiResult.NotFoundResult("找不到這段對話。"));
        }

        return this.FromVerify(await conversationService.RenameAsync(id, request.Title), "已重新命名。");
    }

    /// <summary>刪除一筆紀錄（服務寫 AiAgentConversation.Delete 稽核）。這次執行還沒結束的話先放棄它（存成「中斷」），同 Server 版。</summary>
    [HttpDelete("records/{id:guid}")]
    public async Task<ActionResult<ApiResult>> Delete(Guid id)
    {
        if (await LoadRecordAsync(id) is null)
        {
            return NotFound(ApiResult.NotFoundResult("找不到這段對話。"));
        }

        if (registry.Find(id, UserId) is { } entry)
        {
            await registry.AbandonAsync(entry);
        }

        return this.FromVerify(await conversationService.DeleteAsync(id), "已刪除紀錄。");
    }

    /// <summary>
    /// 開始一次執行，回 NDJSON 串流（start、各 agent 的更新…，最後是 request 或 result）。
    /// 一個人同時只能有一次執行（可能在別的分頁）：已經有的話回 409。
    /// </summary>
    [HttpPost("runs")]
    public async Task<IActionResult> Start([FromBody] AgentWorkflowStartRequestDto request)
    {
        if (FindWorkflow(request.Workflow) is not { } definition)
        {
            return BadRequest(ApiResult.ValidationError("找不到這個工作流程。"));
        }

        var input = request.Input?.Trim() ?? string.Empty;
        if (input.Length == 0)
        {
            return BadRequest(ApiResult.ValidationError("請輸入內容。"));
        }

        if (runner.UnavailableReason is { } unavailable)
        {
            return BadRequest(ApiResult.ValidationError(unavailable));
        }

        var user = currentUserService.CurrentUser;
        if (registry.FindByOwner(user.Id) is not null)
        {
            return Conflict(ApiResult.ConflictResult(ActiveRunExistsMessage));
        }

        var scope = registry.CreateScope(user, HttpContext.Connection.RemoteIpAddress);
        AiWorkflowRunEntry entry;
        try
        {
            var run = await scope.ServiceProvider.GetRequiredService<IAiWorkflowRunner>().StartAsync(definition, HttpContext.RequestAborted);
            entry = new AiWorkflowRunEntry(user.Id, run, scope);
        }
        catch (Exception ex)
        {
            await scope.DisposeAsync();
            logger.LogError(ex, "Agent workflow failed to start. Workflow={Workflow}", definition.Name);
            return this.ApiServerError("無法開始執行，請稍後再試。", ex);
        }

        if (registry.TryAdd(entry) == false)
        {
            await entry.Run.DisposeAsync();
            await scope.DisposeAsync();
            return Conflict(ApiResult.ConflictResult(ActiveRunExistsMessage));
        }

        // 剛登錄、還沒有別人知道它，一定拿得到使用權。
        registry.TryEnter(entry);
        var updates = entry.Run.RunAsync(input, registry.BeginRound(entry));
        return Stream(entry, updates, sent: 0);
    }

    /// <summary>自己停下來等回覆的那一次執行（重新整理後接回來）；沒有（或正在處理中）時資料是 null。</summary>
    [HttpGet("runs/active")]
    public async Task<ActionResult<ApiResult<AgentWorkflowRunDto?>>> Active()
    {
        if (registry.FindByOwner(UserId) is not { } entry || registry.TryEnter(entry) == false)
        {
            return Ok(ApiResult<AgentWorkflowRunDto?>.SuccessResult(null, "目前沒有等你回覆的執行"));
        }

        try
        {
            var run = entry.Run;
            if (run.Result is not null || run.PendingRequests.Count == 0)
            {
                return Ok(ApiResult<AgentWorkflowRunDto?>.SuccessResult(null, "目前沒有等你回覆的執行"));
            }

            return Ok(ApiResult<AgentWorkflowRunDto?>.SuccessResult(new AgentWorkflowRunDto
            {
                RunId = run.ConversationId,
                Workflow = run.Definition.Name,
                ModelName = run.ModelName,
                Transcript = run.Transcript.Select(AgentWorkflowDtoMapper.ToDto).ToList(),
                PendingRequests = run.PendingRequests.Select(AgentWorkflowMapper.ToDto).ToList(),
            }, "取得執行成功"));
        }
        finally
        {
            await registry.ReleaseAsync(entry);
        }
    }

    /// <summary>
    /// 回覆一筆等待中的請求並繼續執行，回 NDJSON 串流。規則全在伺服器：提問的回答不可空白、退回要寫意見且還有退回次數（400）；
    /// 已經回覆過或不存在的請求、正在處理中的執行回 409；不是自己的執行回 404。
    /// </summary>
    [HttpPost("runs/{id:guid}/responses")]
    public async Task<IActionResult> Respond(Guid id, [FromBody] AgentWorkflowRespondRequestDto request)
    {
        if (registry.Find(id, UserId) is not { } entry)
        {
            return NotFound(ApiResult.NotFoundResult(RunNotFoundMessage));
        }

        if (registry.TryEnter(entry) == false)
        {
            return Conflict(ApiResult.ConflictResult("這次執行正在處理中，請稍候。"));
        }

        IAsyncEnumerable<AiWorkflowUpdate> updates;
        int sent;
        try
        {
            var run = entry.Run;
            var pending = run.PendingRequests.FirstOrDefault(item => item.RequestId == request.RequestId);
            if (pending is null)
            {
                await registry.ReleaseAsync(entry);
                return Conflict(ApiResult.ConflictResult("這筆請求已經回覆過或不存在。"));
            }

            if (AgentWorkflowMapper.ToResponse(pending.Request, request) is not { } response)
            {
                await registry.ReleaseAsync(entry);
                return BadRequest(ApiResult.ValidationError("請選擇通過、退回或結束。"));
            }

            // 這個請求剛載入的目前使用者（權限被改了，下一步就照新的算）。
            AiWorkflowRunRegistry.RefreshActor(entry, currentUserService.CurrentUser, HttpContext.Connection.RemoteIpAddress);
            sent = run.Transcript.Count;
            updates = run.RespondAsync(pending.RequestId, response, registry.BeginRound(entry));
        }
        catch (InvalidOperationException ex)
        {
            // 執行器的規則檢查（回答空白、退回沒寫意見、沒有退回次數）：什麼都沒改，請求還在等。
            await registry.ReleaseAsync(entry);
            return BadRequest(ApiResult.ValidationError(ex.Message));
        }

        return Stream(entry, updates, sent);
    }

    /// <summary>「停止」：取消正在處理的那一輪（流程存成「已停止」，串流最後一筆是 Canceled 的結果）。沒有在處理時什麼都不做。</summary>
    [HttpPost("runs/{id:guid}/stop")]
    public ActionResult<ApiResult> Stop(Guid id)
    {
        if (registry.Find(id, UserId) is not { } entry)
        {
            return NotFound(ApiResult.NotFoundResult(RunNotFoundMessage));
        }

        registry.RequestStop(entry);
        return Ok(ApiResult.SuccessResult("已停止。"));
    }

    /// <summary>放棄這次執行（使用者確認放棄或離開頁面）：存成「中斷」、之後只能回顧，同 Server 版。</summary>
    [HttpPost("runs/{id:guid}/abandon")]
    public async Task<ActionResult<ApiResult>> Abandon(Guid id, [FromQuery] bool onlyIfIdle = false)
    {
        if (registry.Find(id, UserId) is not { } entry)
        {
            return NotFound(ApiResult.NotFoundResult(RunNotFoundMessage));
        }

        // 離開頁面時（onlyIfIdle）：另一個分頁正在用這次執行（串流中）就不放棄（0.10.7 獨立審查）。
        if (onlyIfIdle && registry.IsBusy(entry))
        {
            return Ok(ApiResult.SuccessResult("另一個分頁正在使用這次執行，沒有放棄。"));
        }

        await registry.AbandonAsync(entry);
        return Ok(ApiResult.SuccessResult("已放棄這次執行。"));
    }

    private NdjsonResult<AgentWorkflowFrameDto> Stream(AiWorkflowRunEntry entry, IAsyncEnumerable<AiWorkflowUpdate> updates, int sent)
        => new(NdjsonHeartbeat.WithHeartbeat(
            Frames(entry, updates, sent, HttpContext.RequestAborted),
            HeartbeatInterval,
            () => new AgentWorkflowFrameDto { Kind = AgentWorkflowFrameKinds.Ping }));

    /// <summary>
    /// 把執行物件的更新轉成串流的每一行；每一行附上執行紀錄這之前新增的幾則。
    /// 瀏覽器中斷連線就中斷整個流程（存成「中斷」）；結束時一定放開使用權（流程結束的話移出登錄並釋放 scope）。
    /// </summary>
    private async IAsyncEnumerable<AgentWorkflowFrameDto> Frames(
        AiWorkflowRunEntry entry, IAsyncEnumerable<AiWorkflowUpdate> updates, int sent, [EnumeratorCancellation] CancellationToken requestAborted)
    {
        var run = entry.Run;
        List<AgentWorkflowEntryDto>? NewEntries()
        {
            var transcript = run.Transcript;
            if (transcript.Count <= sent)
            {
                return null;
            }

            var added = transcript.Skip(sent).Select(AgentWorkflowDtoMapper.ToDto).ToList();
            sent = transcript.Count;
            return added;
        }

        try
        {
            using (requestAborted.Register(() => registry.Interrupt(entry)))
            {
                yield return new AgentWorkflowFrameDto
                {
                    Kind = AgentWorkflowFrameKinds.Start,
                    RunId = run.ConversationId,
                    ModelName = run.ModelName,
                    Entries = NewEntries(),
                };

                await foreach (var update in updates)
                {
                    var frame = AgentWorkflowMapper.ToFrame(update);
                    frame.Entries = NewEntries();
                    yield return frame;
                }
            }
        }
        finally
        {
            await registry.ReleaseAsync(entry);
        }
    }

    private static AiWorkflowDefinition? FindWorkflow(string? name)
        => AiWorkflowCatalog.All.FirstOrDefault(item => item.Name == name);

    /// <summary>自己的、屬於某條工作流程的紀錄（Agent 測試台的對話不算）；找不到回 null。</summary>
    private async Task<AiAgentConversationLoaded?> LoadRecordAsync(Guid id)
    {
        var loaded = await conversationService.LoadMineAsync(id);
        return loaded is not null && FindWorkflow(loaded.Summary.AgentName) is not null ? loaded : null;
    }
}
