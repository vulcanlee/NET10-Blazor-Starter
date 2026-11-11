using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc;
using MyProject.Business.Services.DataAccess;
using MyProject.Dtos.Ai;
using MyProject.Dtos.Commons;
using MyProject.Models.Systems;
using MyProject.Web.Ai;
using MyProject.Web.Ai.Agents;
using MyProject.Web.Ai.Agents.AdminAssistant;
using MyProject.Web.Client.Api;
using MyProject.Web.Streaming;

namespace MyProject.Web.Controllers;

/// <summary>
/// 一種 Agent 對話頁的設定（Agent 測試台、系統管理助理）：怎麼開新對話、怎麼接續、能不能選推論等級、未預期錯誤的訊息。
/// </summary>
public sealed class AgentConversationProfile
{
    /// <summary>存檔的 AgentName（「我的對話」只列這個 Agent 的）。</summary>
    public required string AgentName { get; init; }

    /// <summary>開新對話（推論等級：null＝系統預設；不支援推論等級的 Agent 忽略）。</summary>
    public required Func<string?, CancellationToken, Task<AiAgentChat>> StartAsync { get; init; }

    /// <summary>接續一段對話；不存在、不是本人的、讀不懂時回 null。</summary>
    public required Func<Guid, CancellationToken, Task<AiAgentChat?>> ResumeAsync { get; init; }

    /// <summary>可以選推論等級（只有 Agent 測試台）。</summary>
    public bool SupportsReasoningEffort { get; init; }

    /// <summary>執行中發生未預期例外時給畫面的訊息（同 Server 版畫面外層的防護）。</summary>
    public required string UnexpectedMessage { get; init; }
}

/// <summary>
/// Agent 對話 API 的共用流程（0.10.7 起，WASM 遷移 M8）：<see cref="AgentPlaygroundController"/> 與 <see cref="AdminAssistantController"/>
/// 的 action 只標授權、選 <see cref="AgentConversationProfile"/>，其餘都在這裡。
///
/// <para>
/// <b>每個請求接續一次對話</b>：伺服器不把對話物件留在記憶體，送訊息與回覆核准都是「讀存檔 → 接續 → 執行 → 存檔」，
/// 回 NDJSON 串流（<see cref="AgentStreamFrameDto"/>）。使用者中斷連線就取消這次執行，已收到的內容照樣存檔（對話物件保證）；
/// 稽核（工具外殼、拒絕、刪除）與記帳都在原本的服務裡，與 Server 版相同。
/// </para>
/// <para>
/// <b>Server 版只在畫面擋的規則搬到這裡</b>：訊息 2,000 字、等待核准時不能送新訊息（409）、核准的編號必須在目前的待核准裡
/// （已決定過的 409，所以同一個核准送兩次只執行一次）、沒有待核准時回覆核准 409（不會送出空訊息給模型）、
/// 核准不可復原的動作要輸入「確認」（400，<see cref="AdminAssistantApproval.CanApprove"/>）。
/// </para>
/// <para>
/// <b>併發</b>：每段對話一把鎖（<see cref="AiAgentConversationLocks"/>）。鎖得到就在回應開始前驗證、錯誤用狀態碼回；
/// 正被另一個請求（例如另一個分頁）使用時排隊 —— 先回 200 串流、等待期間送心跳，輪到之後再驗證，這時的錯誤放在 result 那一行。
/// 等待時間可能超過一般請求的 100 秒逾時，所以排隊一定在串流裡等。核准另外以資料庫的條件式更新搶占（跨行程也只執行一次）。
/// </para>
/// </summary>
public sealed class AgentConversationEndpoint
{
    /// <summary>心跳間隔（工具或推論模型可能很久沒有輸出；中間的代理伺服器會切斷閒置連線）。</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>打開對話時，等正在執行的請求收尾（取消、存檔）的上限。</summary>
    public static readonly TimeSpan OpenWaitLimit = TimeSpan.FromSeconds(3);

    public const string NotResumableMessage = "這段對話無法接續：內容可能已被清除，或格式已不相容。可以刪除它後開新對話。";
    public const string AwaitingApprovalMessage = "還有待核准的工具，請先核准或拒絕。";
    public const string NothingPendingMessage = "沒有待核准的操作，可能已經在其他分頁處理過了。";
    public const string AlreadyDecidedMessage = "這些操作已經處理過了，請重新開啟這段對話。";
    public const string ConfirmRequiredMessage = "請輸入「確認」以核准無法復原的動作。";

    private readonly IAiAgentRunner runner;
    private readonly AiAgentConversationService conversations;
    private readonly AiAgentConversationLocks locks;
    private readonly ILogger<AgentConversationEndpoint> logger;

    public AgentConversationEndpoint(
        IAiAgentRunner runner,
        AiAgentConversationService conversations,
        AiAgentConversationLocks locks,
        ILogger<AgentConversationEndpoint> logger)
    {
        this.runner = runner;
        this.conversations = conversations;
        this.locks = locks;
        this.logger = logger;
    }

    /// <summary>「我的對話」清單（依最後對話時間 desc）。時間以不帶時差送出。</summary>
    public async Task<List<AgentConversationSummaryDto>> ListAsync(AgentConversationProfile profile)
        => (await conversations.ListMineAsync(profile.AgentName)).Select(AgentDtoMapper.ToDto).ToList();

    /// <summary>
    /// 打開一段對話：完整的畫面紀錄與待核准（接續一次，不呼叫模型）。
    /// 正在執行時最多等 <see cref="OpenWaitLimit"/>：重新整理會中斷執行中的請求，伺服器取消、存檔只要一下子，等它存完才讀得到最後的內容；
    /// 另一個分頁還在跑很久的執行時不等，讀上次存下的內容。
    /// </summary>
    public async Task<ActionResult<ApiResult<AgentConversationDto>>> GetAsync(ControllerBase controller, AgentConversationProfile profile, Guid conversationId)
    {
        if (runner.UnavailableReason is { } unavailable)
        {
            return Conflict<AgentConversationDto>(controller, unavailable);
        }

        var ct = controller.HttpContext.RequestAborted;
        IDisposable? lease = null;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            wait.CancelAfter(OpenWaitLimit);
            try
            {
                lease = await locks.AcquireAsync(conversationId, wait.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested == false)
            {
                logger.LogInformation("AI agent conversation is busy; reading the last saved state. Agent={Agent}, ConversationId={ConversationId}", profile.AgentName, conversationId);
            }
        }

        using var held = lease;
        var chat = await profile.ResumeAsync(conversationId, ct);
        if (chat is null)
        {
            return NotFound<AgentConversationDto>(controller, NotResumableMessage);
        }

        return controller.Ok(ApiResult<AgentConversationDto>.SuccessResult(new AgentConversationDto
        {
            ConversationId = chat.ConversationId,
            ModelName = chat.ModelName,
            ReasoningEffort = chat.ReasoningEffort,
            Transcript = chat.Transcript.Select(AgentDtoMapper.ToDto).ToList(),
            PendingApprovals = chat.PendingApprovals.Select(AgentDtoMapper.ToDto).ToList(),
        }));
    }

    /// <summary>改標題（只能改自己的、這個 Agent 的）。不寫稽核（同 Server 版）。</summary>
    public async Task<ActionResult<ApiResult>> RenameAsync(ControllerBase controller, AgentConversationProfile profile, Guid conversationId, string? title)
    {
        if (!await IsMineAsync(profile, conversationId))
        {
            return controller.FromVerify(Business.Factories.VerifyRecordResultFactory.NotFound("找不到這段對話。"), string.Empty);
        }

        return controller.FromVerify(await conversations.RenameAsync(conversationId, title ?? string.Empty), "已重新命名。");
    }

    /// <summary>刪除自己的一段對話（寫 <c>AiAgentConversation.Delete</c> 稽核）。執行中就等它做完再刪，否則執行結束的存檔會把它建回來。</summary>
    public async Task<ActionResult<ApiResult>> DeleteAsync(ControllerBase controller, AgentConversationProfile profile, Guid conversationId)
    {
        using var lease = await locks.AcquireAsync(conversationId, controller.HttpContext.RequestAborted);
        if (!await IsMineAsync(profile, conversationId))
        {
            return controller.FromVerify(Business.Factories.VerifyRecordResultFactory.NotFound("找不到這段對話。"), string.Empty);
        }

        return controller.FromVerify(await conversations.DeleteAsync(conversationId), "已刪除對話。");
    }

    /// <summary>送出一則訊息（開新對話或接續），回 NDJSON 串流。</summary>
    public async Task<IActionResult> SendAsync(ControllerBase controller, AgentConversationProfile profile, AgentMessageRequestDto request)
    {
        var message = request.Message?.Trim() ?? string.Empty;
        if (message.Length == 0)
        {
            return Invalid(controller, "請輸入訊息。");
        }

        if ((request.Message ?? string.Empty).Length > AgentConversationLimits.MaxMessageLength)
        {
            return Invalid(controller, $"訊息最多 {AgentConversationLimits.MaxMessageLength} 個字。");
        }

        var effort = profile.SupportsReasoningEffort ? request.ReasoningEffort : null;
        if (!AiAgentChat.IsValidReasoningEffort(effort))
        {
            return Invalid(controller, $"推論等級只接受 {string.Join("、", Configuration.AiSettings.AgentReasoningEffortValues)} 或系統預設。");
        }

        if (runner.UnavailableReason is { } unavailable)
        {
            return Conflict<object>(controller, unavailable);
        }

        var ct = controller.HttpContext.RequestAborted;
        if (request.ConversationId is not { } conversationId)
        {
            // 新對話：ID 由伺服器產生（start 那一行帶給畫面）。照樣上鎖，執行中重新整理後從清單點開再送出的請求才會排隊。
            AiAgentChat chat;
            try
            {
                chat = await profile.StartAsync(AiAgentChat.Normalize(effort), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to start an AI agent conversation. Agent={Agent}", profile.AgentName);
                return controller.ApiServerError("無法開始對話，請稍後再試。", ex);
            }

            var lease = locks.TryAcquire(chat.ConversationId)!;
            controller.HttpContext.Response.RegisterForDispose(lease);
            return Stream(profile, chat.ConversationId, lease, new Prepared(chat, token => chat.SendAsync(message, token)), prepare: null, ct);
        }

        return await StreamWithLockAsync(controller, profile, conversationId, token => PrepareSendAsync(profile, conversationId, message, effort, token));
    }

    /// <summary>回覆待核准的工具：<see cref="AgentApprovalResponseRequestDto.ApprovedRequestIds"/> 核准，其餘拒絕；回 NDJSON 串流。</summary>
    public async Task<IActionResult> RespondAsync(ControllerBase controller, AgentConversationProfile profile, Guid conversationId, AgentApprovalResponseRequestDto request)
    {
        var approved = (request.ApprovedRequestIds ?? []).Distinct(StringComparer.Ordinal).ToList();
        var rejected = (request.RejectedRequestIds ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (approved.Count + rejected.Count == 0)
        {
            return Invalid(controller, "請指定要核准或拒絕的操作。");
        }

        if (approved.Intersect(rejected, StringComparer.Ordinal).Any())
        {
            return Invalid(controller, "同一個操作不能同時核准與拒絕。");
        }

        if (runner.UnavailableReason is { } unavailable)
        {
            return Conflict<object>(controller, unavailable);
        }

        return await StreamWithLockAsync(controller, profile, conversationId,
            token => PrepareRespondAsync(profile, conversationId, approved, rejected, request.ConfirmText, token));
    }

    /// <summary>
    /// 鎖得到：先驗證（錯誤以狀態碼回），再開始串流。鎖不到（另一個請求正在用這段對話）：直接回串流，在串流裡排隊、輪到後驗證。
    /// </summary>
    private async Task<IActionResult> StreamWithLockAsync(
        ControllerBase controller, AgentConversationProfile profile, Guid conversationId, Func<CancellationToken, Task<Prepared>> prepare)
    {
        var lease = locks.TryAcquire(conversationId);
        if (lease is null)
        {
            logger.LogInformation("AI agent conversation is busy; the request is queued. Agent={Agent}, ConversationId={ConversationId}", profile.AgentName, conversationId);
            return Stream(profile, conversationId, lease: null, prepared: null, prepare, controller.HttpContext.RequestAborted);
        }

        Prepared prepared;
        try
        {
            prepared = await prepare(controller.HttpContext.RequestAborted);
        }
        catch
        {
            lease.Dispose();
            throw;
        }

        if (prepared.Error is { } error)
        {
            lease.Dispose();
            var body = ApiResult<object>.FailureResult(error.Message, error.Status);
            body.TraceId = controller.HttpContext.TraceIdentifier;
            return controller.StatusCode(error.Status, body);
        }

        // 保險：串流正常結束時背景工作就會釋放；萬一回應根本沒開始列舉，請求結束時也會釋放（重複釋放無妨）。
        controller.HttpContext.Response.RegisterForDispose(lease);
        return Stream(profile, conversationId, lease, prepared, prepare: null, controller.HttpContext.RequestAborted);
    }

    private async Task<Prepared> PrepareSendAsync(AgentConversationProfile profile, Guid conversationId, string message, string? effort, CancellationToken ct)
    {
        var chat = await profile.ResumeAsync(conversationId, ct);
        if (chat is null)
        {
            return Prepared.Fail(StatusCodes.Status404NotFound, "NotFound", NotResumableMessage);
        }

        if (chat.IsAwaitingApproval)
        {
            return Prepared.Fail(StatusCodes.Status409Conflict, "Conflict", AwaitingApprovalMessage);
        }

        // 推論等級（Agent 測試台）：畫面送的與存的不同時，從這則訊息起生效（Server 版改下拉就立即重建 agent，效果相同）。
        if (profile.SupportsReasoningEffort && AiAgentChat.Normalize(effort) != chat.ReasoningEffort)
        {
            try
            {
                chat.SetReasoningEffort(effort);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to rebuild the agent for a new reasoning effort. Agent={Agent}, ConversationId={ConversationId}", profile.AgentName, conversationId);
                return Prepared.Fail(StatusCodes.Status500InternalServerError, "Unexpected", "無法改推論等級，請稍後再試；這段對話維持原本的設定。");
            }
        }

        return new Prepared(chat, token => chat.SendAsync(message, token));
    }

    private async Task<Prepared> PrepareRespondAsync(
        AgentConversationProfile profile, Guid conversationId, List<string> approved, List<string> rejected, string? confirmText, CancellationToken ct)
    {
        var chat = await profile.ResumeAsync(conversationId, ct);
        if (chat is null)
        {
            return Prepared.Fail(StatusCodes.Status404NotFound, "NotFound", NotResumableMessage);
        }

        var pending = chat.PendingApprovals;
        if (pending.Count == 0)
        {
            return Prepared.Fail(StatusCodes.Status409Conflict, "Conflict", NothingPendingMessage);
        }

        var pendingIds = pending.Select(item => item.RequestId).ToHashSet(StringComparer.Ordinal);
        if (!approved.Concat(rejected).All(pendingIds.Contains))
        {
            return Prepared.Fail(StatusCodes.Status409Conflict, "Conflict", AlreadyDecidedMessage);
        }

        var approvedRequests = pending.Where(item => approved.Contains(item.RequestId, StringComparer.Ordinal)).ToList();
        if (approvedRequests.Count > 0 && !AdminAssistantApproval.CanApprove(approvedRequests, confirmText))
        {
            return Prepared.Fail(StatusCodes.Status400BadRequest, "Invalid", ConfirmRequiredMessage);
        }

        // 搶占：條件式更新只有一個請求改得到（跨行程也一樣），已經被別人處理過就不再執行。
        if (!await conversations.TryClaimPendingApprovalAsync(conversationId))
        {
            return Prepared.Fail(StatusCodes.Status409Conflict, "Conflict", AlreadyDecidedMessage);
        }

        return new Prepared(chat, token => chat.RespondAsync(approved, token));
    }

    private async Task<bool> IsMineAsync(AgentConversationProfile profile, Guid conversationId)
        => (await conversations.LoadMineAsync(conversationId))?.Summary.AgentName == profile.AgentName;

    /// <summary>
    /// 回串流。執行放在背景工作裡、寫進佇列：對話物件每加一則紀錄就立即送出（例如分派完成後使用者那一則），
    /// 不必等到下一筆更新。背景工作在 NdjsonResult 開始讀取時才啟動；序列讀到結束時執行一定已經結束（存檔、稽核、記帳都做完），鎖也已釋放。
    /// </summary>
    private IActionResult Stream(
        AgentConversationProfile profile,
        Guid conversationId,
        IDisposable? lease,
        Prepared? prepared,
        Func<CancellationToken, Task<Prepared>>? prepare,
        CancellationToken requestAborted)
    {
        var channel = Channel.CreateUnbounded<AgentStreamFrameDto>(new UnboundedChannelOptions { SingleReader = true });
        Task Produce() => Task.Run(() => ProduceAsync(channel.Writer, profile, conversationId, lease, prepared, prepare, requestAborted), CancellationToken.None);
        return new NdjsonResult<AgentStreamFrameDto>(NdjsonHeartbeat.WithHeartbeat(
            FramesAsync(channel.Reader, Produce),
            HeartbeatInterval,
            () => new AgentStreamFrameDto { Kind = AgentStreamFrameKinds.Ping }));
    }

    private static async IAsyncEnumerable<AgentStreamFrameDto> FramesAsync(ChannelReader<AgentStreamFrameDto> reader, Func<Task> produce)
    {
        var producer = produce();
        await foreach (var frame in reader.ReadAllAsync())
        {
            yield return frame;
        }

        await producer;
    }

    private async Task ProduceAsync(
        ChannelWriter<AgentStreamFrameDto> writer,
        AgentConversationProfile profile,
        Guid conversationId,
        IDisposable? lease,
        Prepared? prepared,
        Func<CancellationToken, Task<Prepared>>? prepare,
        CancellationToken ct)
    {
        AiAgentChat? chat = null;
        try
        {
            if (prepared is null)
            {
                // 排隊：先送一行心跳，讓瀏覽器（與中間的代理伺服器）立刻收到回應開頭，不會等到輪到才算請求成功。
                writer.TryWrite(new AgentStreamFrameDto { Kind = AgentStreamFrameKinds.Ping });
                try
                {
                    lease = await locks.AcquireAsync(conversationId, ct);
                }
                catch (OperationCanceledException)
                {
                    // 排隊中使用者就離開了：什麼都還沒做。
                    return;
                }

                prepared = await prepare!(ct);
                if (prepared.Error is { } error)
                {
                    writer.TryWrite(new AgentStreamFrameDto { Kind = AgentStreamFrameKinds.Start, ConversationId = conversationId });
                    writer.TryWrite(ResultFrame(new AiResultDto { Success = false, Reason = error.Reason, ErrorMessage = error.Message }));
                    return;
                }
            }

            chat = prepared.Chat!;
            chat.EntryAdded = entry => writer.TryWrite(new AgentStreamFrameDto { Kind = AgentStreamFrameKinds.Entry, Entry = AgentDtoMapper.ToDto(entry) });
            writer.TryWrite(new AgentStreamFrameDto
            {
                Kind = AgentStreamFrameKinds.Start,
                ConversationId = chat.ConversationId,
                ModelName = chat.ModelName,
                ReasoningEffort = chat.ReasoningEffort,
            });

            // 「由誰回答」：每個請求至少送一次（0.10.7 獨立審查）—— 核准後的那一輪、接續後分派到同一領域的那一輪，
            // 標籤沒變也要讓畫面知道，按「停止產生」時畫面自己補的那則才標得出來（與伺服器存的一致）。
            string? label = null;
            await foreach (var update in prepared.Run!(ct))
            {
                if (chat.CurrentLabel is { } current && current != label)
                {
                    label = current;
                    writer.TryWrite(new AgentStreamFrameDto { Kind = AgentStreamFrameKinds.Label, Label = current });
                }

                if (ToFrame(update) is { } frame)
                {
                    writer.TryWrite(frame);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            writer.TryWrite(ResultFrame(AiStreamFrames.ToDto(AiAnalysisResult.Failure(AiAnalysisFailureReason.Canceled, "已取消本次執行。"))));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AI agent conversation request failed unexpectedly. Agent={Agent}, ConversationId={ConversationId}", profile.AgentName, conversationId);
            writer.TryWrite(ResultFrame(AiStreamFrames.ToDto(
                AiAnalysisResult.Failure(AiAnalysisFailureReason.Unexpected, profile.UnexpectedMessage, detail: ex.ToString()))));
        }
        finally
        {
            if (chat is not null)
            {
                chat.EntryAdded = null;
            }

            lease?.Dispose();
            writer.TryComplete();
        }
    }

    private static AgentStreamFrameDto? ToFrame(AiAgentUpdate update)
    {
        if (update.ToolActivity is { } activity)
        {
            return new AgentStreamFrameDto
            {
                Kind = AgentStreamFrameKinds.Tool,
                Tool = new AgentToolActivityDto { ToolName = activity.ToolName, DisplayName = activity.DisplayName, Completed = activity.Completed },
            };
        }

        if (update.Approvals is { } approvals)
        {
            return new AgentStreamFrameDto { Kind = AgentStreamFrameKinds.Approvals, Approvals = approvals.Select(AgentDtoMapper.ToDto).ToList() };
        }

        if (update.Result is { } result)
        {
            return ResultFrame(AiStreamFrames.ToDto(result));
        }

        return update.TextDelta.Length > 0 ? new AgentStreamFrameDto { Kind = AgentStreamFrameKinds.Delta, Text = update.TextDelta } : null;
    }

    private static AgentStreamFrameDto ResultFrame(AiResultDto result) => new() { Kind = AgentStreamFrameKinds.Result, Result = result };

    private static ObjectResult Invalid(ControllerBase controller, string message)
    {
        var body = ApiResult<object>.ValidationError(message);
        body.TraceId = controller.HttpContext.TraceIdentifier;
        return controller.BadRequest(body);
    }

    private static ObjectResult Conflict<T>(ControllerBase controller, string message)
    {
        var body = ApiResult<T>.ConflictResult(message);
        body.TraceId = controller.HttpContext.TraceIdentifier;
        return controller.Conflict(body);
    }

    private static ObjectResult NotFound<T>(ControllerBase controller, string message)
    {
        var body = ApiResult<T>.NotFoundResult(message);
        body.TraceId = controller.HttpContext.TraceIdentifier;
        return controller.NotFound(body);
    }

    /// <summary>驗證通過（<see cref="Chat"/>＋要跑的執行）或失敗（<see cref="Error"/>）。</summary>
    private sealed record Prepared(AiAgentChat? Chat, Func<CancellationToken, IAsyncEnumerable<AiAgentUpdate>>? Run)
    {
        public PrepareError? Error { get; init; }

        public static Prepared Fail(int status, string reason, string message) => new(null, null) { Error = new PrepareError(status, reason, message) };
    }

    private sealed record PrepareError(int Status, string Reason, string Message);
}
