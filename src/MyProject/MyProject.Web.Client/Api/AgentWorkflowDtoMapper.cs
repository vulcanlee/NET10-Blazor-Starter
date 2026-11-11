using MyProject.Dtos.Ai;
using MyProject.Models.Systems;

namespace MyProject.Web.Client.Api;

/// <summary>執行紀錄的一則 ↔ API DTO（0.10.7 起，WASM 遷移 M8；伺服器送出與 WASM 收下共用）。</summary>
public static class AgentWorkflowDtoMapper
{
    public static AgentWorkflowEntryDto ToDto(AiAgentTranscriptEntry entry) => new()
    {
        Kind = entry.Kind,
        Text = entry.Text,
        Agent = entry.Agent,
    };

    public static AiAgentTranscriptEntry ToModel(AgentWorkflowEntryDto dto) => new()
    {
        Kind = dto.Kind,
        Text = dto.Text,
        Agent = dto.Agent,
    };
}
