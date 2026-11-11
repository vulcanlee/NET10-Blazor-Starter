using MyProject.Dtos.Admins;
using MyProject.Dtos.Ai;
using MyProject.Models.Systems;
using MyProject.Web.Ai.Agents;

namespace MyProject.Web.Client.Api;

/// <summary>
/// Agent 對話與巡檢報告的 DTO 與畫面模型互轉（0.10.7 起，WASM 遷移 M8；WASM 頁面與伺服器的
/// <c>AgentPlaygroundController</c>、<c>AdminAssistantController</c> 共用同一份）。
/// 本地時間（對話清單、巡檢報告）以不帶時差送出：帶了 +08:00，WASM 會換成瀏覽器的時區。
/// </summary>
public static class AgentDtoMapper
{
    public static AgentConversationSummaryDto ToDto(AiAgentConversationSummary item) => new()
    {
        ConversationId = item.ConversationId,
        Title = item.Title,
        CreatedAt = DateTime.SpecifyKind(item.CreatedAt, DateTimeKind.Unspecified),
        UpdatedAt = DateTime.SpecifyKind(item.UpdatedAt, DateTimeKind.Unspecified),
        HasPendingApproval = item.HasPendingApproval,
    };

    /// <summary>畫面用的一列（AgentName 由呼叫端帶，清單本來就只有這個 Agent 的）。</summary>
    public static AiAgentConversationSummary ToModel(AgentConversationSummaryDto dto, string agentName)
        => new(dto.ConversationId, agentName, dto.Title, dto.CreatedAt, dto.UpdatedAt, dto.HasPendingApproval);

    public static AgentTranscriptEntryDto ToDto(AiAgentTranscriptEntry entry) => new()
    {
        Kind = entry.Kind,
        Text = entry.Text,
        Agent = entry.Agent,
    };

    public static AiAgentTranscriptEntry ToModel(AgentTranscriptEntryDto dto) => new()
    {
        Kind = dto.Kind,
        Text = dto.Text,
        Agent = dto.Agent,
    };

    public static AgentApprovalRequestDto ToDto(AiToolApprovalRequest request) => new()
    {
        RequestId = request.RequestId,
        ToolName = request.ToolName,
        DisplayName = request.DisplayName,
        Arguments = request.Arguments.Select(argument => new AgentApprovalArgumentDto { Name = argument.Key, Value = argument.Value }).ToList(),
        Note = request.Note,
        Label = request.Label,
        IsIrreversible = request.IsIrreversible,
        Preview = request.Preview,
    };

    public static AiToolApprovalRequest ToModel(AgentApprovalRequestDto dto) => new(
        dto.RequestId,
        dto.ToolName,
        dto.DisplayName,
        dto.Arguments.Select(argument => new KeyValuePair<string, string>(argument.Name, argument.Value)).ToList(),
        dto.Note,
        dto.Label,
        dto.IsIrreversible,
        dto.Preview);

    public static AdminPatrolReportSummaryDto ToDto(AdminPatrolReportSummary item) => new()
    {
        ReportId = item.ReportId,
        CreatedAt = DateTime.SpecifyKind(item.CreatedAt, DateTimeKind.Unspecified),
        Status = item.Status,
        AttentionSections = item.AttentionSections,
        Trigger = item.Trigger,
        TriggeredByAccount = item.TriggeredByAccount,
        ModelName = item.ModelName,
        IsTruncated = item.IsTruncated,
    };

    public static AdminPatrolReportSummary ToModel(AdminPatrolReportSummaryDto dto) => new(
        dto.ReportId, dto.CreatedAt, dto.Status, dto.AttentionSections, dto.Trigger, dto.TriggeredByAccount, dto.ModelName, dto.IsTruncated);

    public static AdminPatrolReportDetailDto ToDto(AdminPatrolReportDetail detail) => new()
    {
        Summary = ToDto(detail.Summary),
        Markdown = detail.Markdown,
    };

    public static AdminPatrolReportDetail ToModel(AdminPatrolReportDetailDto dto) => new(ToModel(dto.Summary), dto.Markdown);
}
