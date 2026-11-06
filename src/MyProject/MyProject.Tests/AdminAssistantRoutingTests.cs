using MyProject.Models.Systems;
using MyProject.Web.Ai.Agents.AdminAssistant;

namespace MyProject.Tests;

/// <summary>系統管理助理的分派解析（0.9.155 起）：模型回覆的領域代碼、畫面標籤與送給分派模型的輸入。</summary>
public sealed class AdminAssistantRoutingTests
{
    [Fact]
    public void Parse_CommaSeparatedCodes_ReturnsDomains()
        => Assert.Equal(
            new HashSet<AdminAssistantDomain> { AdminAssistantDomain.Monitoring, AdminAssistantDomain.Ops },
            AdminAssistantRouting.Parse("monitoring, ops"));

    [Fact]
    public void Parse_IsCaseInsensitive()
        => Assert.Equal(new HashSet<AdminAssistantDomain> { AdminAssistantDomain.Patrol }, AdminAssistantRouting.Parse("PATROL"));

    [Fact]
    public void Parse_Ai_ReturnsAiDomain()
        => Assert.Equal(new HashSet<AdminAssistantDomain> { AdminAssistantDomain.Ai }, AdminAssistantRouting.Parse("ai"));

    [Fact]
    public void Parse_OnlyWholeWords()
        => Assert.Empty(AdminAssistantRouting.Parse("aim maintenance"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("我認為這是監控問題")]
    public void Parse_NoCodes_ReturnsEmpty(string? text)
        => Assert.Empty(AdminAssistantRouting.Parse(text));

    [Fact]
    public void LabelOf_UsesEnumOrderAndChineseNames()
        => Assert.Equal("監控與診斷、系統設定與維運",
            AdminAssistantRouting.LabelOf(new HashSet<AdminAssistantDomain> { AdminAssistantDomain.Ops, AdminAssistantDomain.Monitoring }));

    [Fact]
    public void LabelOf_AllNames()
        => Assert.Equal("帳號與權限、監控與診斷、AI 管理、系統設定與維運、巡檢",
            AdminAssistantRouting.LabelOf(Enum.GetValues<AdminAssistantDomain>().ToHashSet()));

    [Fact]
    public void LabelOf_Empty_IsDefaultLabel()
        => Assert.Equal("系統管理助理", AdminAssistantRouting.LabelOf(new HashSet<AdminAssistantDomain>()));

    [Fact]
    public void BuildRouterInput_KeepsLastSixUserAndAssistantEntries_Truncated()
    {
        var transcript = new List<AiAgentTranscriptEntry>();
        for (var i = 1; i <= 10; i++)
        {
            transcript.Add(new AiAgentTranscriptEntry
            {
                Kind = i % 2 == 1 ? AiAgentTranscriptKinds.User : AiAgentTranscriptKinds.Assistant,
                Text = $"第{i}則" + new string('長', 400),
            });
            transcript.Add(new AiAgentTranscriptEntry { Kind = AiAgentTranscriptKinds.Tool, Text = $"工具{i}" });
        }

        transcript.Add(new AiAgentTranscriptEntry { Kind = AiAgentTranscriptKinds.Notice, Text = "你核准了：x" });

        var input = AdminAssistantRouting.BuildRouterInput("那就停用他", transcript);

        Assert.DoesNotContain("第4則", input, StringComparison.Ordinal);
        Assert.Contains("第5則", input, StringComparison.Ordinal);
        Assert.Contains("第10則", input, StringComparison.Ordinal);
        Assert.DoesNotContain("工具", input, StringComparison.Ordinal);
        Assert.DoesNotContain("你核准了", input, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('長', 301), input, StringComparison.Ordinal);
        Assert.EndsWith("使用者這次的訊息：那就停用他", input, StringComparison.Ordinal);
    }
}
