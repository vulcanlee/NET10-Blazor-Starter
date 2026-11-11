using MyProject.Dtos.Ai;
using MyProject.Models.Systems;
using MyProject.Web.Ai;
using MyProject.Web.Ai.Workflows;

namespace MyProject.Web.Controllers;

/// <summary>工作流程的伺服器型別 → API DTO（0.10.7 起，WASM 遷移 M8）。紀錄的一則（Models 型別）由 Client 的 <c>AgentWorkflowDtoMapper</c> 轉。</summary>
public static class AgentWorkflowMapper
{
    /// <summary>要貼整份文件的流程（多觀點審查）輸入框比較高，同 Server 版。</summary>
    private const int DocumentInputRows = 8;
    private const int DefaultInputRows = 3;

    public static AgentWorkflowDefinitionDto ToDto(AiWorkflowDefinition definition) => new()
    {
        Name = definition.Name,
        DisplayName = definition.DisplayName,
        Description = definition.Description,
        InputPlaceholder = definition.InputPlaceholder,
        InputRows = definition == MultiPerspectiveReviewWorkflow.Definition ? DocumentInputRows : DefaultInputRows,
    };

    /// <summary>「我的執行紀錄」的一列；本地時間以不帶時差送出（帶了時差 WASM 會換成瀏覽器的時區）。</summary>
    public static AgentWorkflowRecordDto ToDto(AiAgentConversationSummary summary) => new()
    {
        ConversationId = summary.ConversationId,
        Title = summary.Title,
        UpdatedAt = DateTime.SpecifyKind(summary.UpdatedAt, DateTimeKind.Unspecified),
    };

    public static AgentWorkflowPendingRequestDto ToDto(AiWorkflowPendingRequest pending) => new()
    {
        RequestId = pending.RequestId,
        Kind = pending.Request.Kind.ToString(),
        Agent = pending.Request.Agent,
        Approvals = pending.Request.Approvals.Select(approval => new AgentWorkflowApprovalDto
        {
            RequestId = approval.RequestId,
            ToolName = approval.ToolName,
            DisplayName = approval.DisplayName,
            Arguments = approval.Arguments.Select(argument => new AgentWorkflowArgumentDto { Key = argument.Key, Value = argument.Value }).ToList(),
        }).ToList(),
        Question = pending.Request.Question,
        Draft = pending.Request.Draft,
        RejectionsLeft = pending.Request.RejectionsLeft,
    };

    public static AgentWorkflowResultDto ToDto(AiWorkflowResult result) => new()
    {
        Outcome = result.Outcome.ToString(),
        ErrorMessage = result.ErrorMessage,
        ModelCalls = result.ModelCalls,
        Usage = AiTokenUsage.ToDto(result.Usage),
        ElapsedMilliseconds = (long)result.Elapsed.TotalMilliseconds,
    };

    /// <summary>一筆更新 → 串流的一行（判斷順序同 Server 版畫面）。</summary>
    public static AgentWorkflowFrameDto ToFrame(AiWorkflowUpdate update)
    {
        var frame = new AgentWorkflowFrameDto { Agent = update.Agent };
        if (update.AgentStarted)
        {
            frame.Kind = AgentWorkflowFrameKinds.AgentStarted;
        }
        else if (update.TextDelta.Length > 0)
        {
            frame.Kind = AgentWorkflowFrameKinds.Delta;
            frame.Text = update.TextDelta;
        }
        else if (update.ToolActivity is { } activity)
        {
            frame.Kind = AgentWorkflowFrameKinds.Tool;
            frame.Tool = new AgentWorkflowToolActivityDto { ToolName = activity.ToolName, DisplayName = activity.DisplayName, Completed = activity.Completed };
        }
        else if (update.AgentCompleted)
        {
            frame.Kind = AgentWorkflowFrameKinds.AgentCompleted;
        }
        else if (update.Request is { } request)
        {
            frame.Kind = AgentWorkflowFrameKinds.Request;
            frame.Request = ToDto(request);
        }
        else if (update.Output is { } output)
        {
            frame.Kind = AgentWorkflowFrameKinds.Output;
            frame.Output = new AgentWorkflowOutputDto { Title = output.Title, Markdown = output.Markdown };
        }
        else if (update.Result is { } result)
        {
            frame.Kind = AgentWorkflowFrameKinds.Result;
            frame.Result = ToDto(result);
        }

        return frame;
    }

    /// <summary>
    /// 畫面送來的回覆 → 執行器的回覆。審閱關卡的決定不認得時回 null（400）；其餘規則（回答空白、退回沒寫意見、沒有退回次數）
    /// 由執行器檢查（<see cref="AiWorkflowRun.RespondAsync"/> 丟 <see cref="InvalidOperationException"/>），規則只有一份。
    /// </summary>
    public static AiWorkflowHumanResponse? ToResponse(AiWorkflowHumanRequest request, AgentWorkflowRespondRequestDto dto)
    {
        AiReviewDecision? parsed = dto.Decision switch
        {
            AgentWorkflowReviewDecisions.Approve => AiReviewDecision.Approve,
            AgentWorkflowReviewDecisions.Reject => AiReviewDecision.Reject,
            AgentWorkflowReviewDecisions.End => AiReviewDecision.End,
            _ => null,
        };
        if (request.Kind == AiWorkflowRequestKind.Review && parsed is null)
        {
            return null;
        }

        var decision = parsed ?? AiReviewDecision.Approve;

        return new AiWorkflowHumanResponse
        {
            ApprovedRequestIds = request.Kind == AiWorkflowRequestKind.ToolApproval ? dto.ApprovedRequestIds.ToList() : [],
            Answer = dto.Answer,
            Decision = decision,
            Comment = dto.Comment,
        };
    }
}
