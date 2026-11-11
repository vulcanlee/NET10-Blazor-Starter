using MyProject.Models.Systems;
using MyProject.Web.Ai.Agents;
using MyProject.Web.Client.Api;

namespace MyProject.Tests.Client;

/// <summary>
/// Agent 對話與巡檢報告的 DTO 互轉（0.10.7 起，WASM 遷移 M8；伺服器的 controller 與 WASM 頁面共用 <see cref="AgentDtoMapper"/>）：
/// 欄位不漏、本地時間以不帶時差送出。
/// </summary>
public sealed class AgentDtoMapperTests
{
    [Fact]
    public void ApprovalRequest_RoundTrip_ShouldKeepEveryField()
    {
        var request = new AiToolApprovalRequest(
            "req-1", "delete_exceptions", "刪除例外紀錄",
            [new("ids", "共 2 筆：\n1\n2"), new("reason", "清理")],
            Note: "無法復原。", Label: "監控與診斷", IsIrreversible: true, Preview: "目前 2 筆");

        var back = AgentDtoMapper.ToModel(AgentDtoMapper.ToDto(request));

        Assert.Equal(request with { Arguments = back.Arguments }, back);
        Assert.Equal(request.Arguments, back.Arguments);
    }

    [Fact]
    public void TranscriptEntry_RoundTrip_ShouldKeepKindTextAndLabel()
    {
        var entry = new AiAgentTranscriptEntry { Kind = AiAgentTranscriptKinds.Assistant, Text = "**回覆**", Agent = "帳號與權限" };

        var back = AgentDtoMapper.ToModel(AgentDtoMapper.ToDto(entry));

        Assert.Equal((entry.Kind, entry.Text, entry.Agent), (back.Kind, back.Text, back.Agent));
    }

    /// <summary>伺服器的 DateTime.Now 是 Kind=Local，帶著時差送出的話 WASM 會換成瀏覽器的時區。</summary>
    [Fact]
    public void LocalTimes_ShouldBeSentAsUnspecified()
    {
        var local = new DateTime(2026, 11, 8, 7, 30, 0, DateTimeKind.Local);

        var conversation = AgentDtoMapper.ToDto(new AiAgentConversationSummary(Guid.NewGuid(), "AgentPlayground", "標題", local, local, true));
        var report = AgentDtoMapper.ToDto(new AdminPatrolReportSummary(Guid.NewGuid(), local, AdminPatrolStatuses.Normal, "", JobRunTriggers.Schedule, null, "gpt", false));

        Assert.Equal(DateTimeKind.Unspecified, conversation.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Unspecified, conversation.UpdatedAt.Kind);
        Assert.Equal(DateTimeKind.Unspecified, report.CreatedAt.Kind);
        Assert.Equal(new DateTime(2026, 11, 8, 7, 30, 0), report.CreatedAt);
        Assert.True(conversation.HasPendingApproval);
    }
}
