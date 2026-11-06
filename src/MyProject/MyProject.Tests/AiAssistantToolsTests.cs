using MyProject.AccessDatas.Models;
using MyProject.Business.Services.DataAccess;
using MyProject.Models.Systems;
using MyProject.Web.Ai.Agents.AdminAssistant;

namespace MyProject.Tests;

/// <summary>系統管理助理「AI 管理」的唯讀工具（0.9.157 起）：提示詞名稱解析、狀態、Token 分組與日期、對話紀錄截斷。</summary>
public sealed class AiAssistantToolsTests
{
    private static readonly DateTime Today = new(2026, 11, 6);

    [Theory]
    [InlineData("AdminAssistantAi", PromptTemplateKeys.AdminAssistantAi)]
    [InlineData("adminassistantai", PromptTemplateKeys.AdminAssistantAi)]
    [InlineData("管理助理／AI 管理", PromptTemplateKeys.AdminAssistantAi)]
    [InlineData(" 日誌分析 ", PromptTemplateKeys.LogAnalysis)]
    [InlineData("不存在", null)]
    [InlineData(null, null)]
    public void ResolvePromptKey_AcceptsKeyOrChineseTitle(string? input, string? expected)
        => Assert.Equal(expected, AiAssistantTools.ResolvePromptKey(input));

    [Fact]
    public void PromptStatus_BuiltInDefaultOrActiveVersion()
    {
        Assert.Equal("內建預設", AiAssistantTools.PromptStatus([]));
        Assert.Equal("內建預設", AiAssistantTools.PromptStatus([Version(2, active: false)]));
        Assert.Equal("版本 3", AiAssistantTools.PromptStatus([Version(3, active: true), Version(2, active: false)]));
    }

    [Theory]
    [InlineData("operation", TokenGrouping.Operation, false)]
    [InlineData("ACCOUNT", TokenGrouping.Account, false)]
    [InlineData("model", TokenGrouping.Model, false)]
    [InlineData("day", TokenGrouping.Day, false)]
    [InlineData(null, TokenGrouping.Operation, false)]
    [InlineData("weekly", TokenGrouping.Operation, true)]
    public void ParseGrouping_DefaultsToOperation(string? input, TokenGrouping expected, bool hasNote)
    {
        var (grouping, note) = AiAssistantTools.ParseGrouping(input);

        Assert.Equal(expected, grouping);
        Assert.Equal(hasNote, note is not null);
    }

    [Fact]
    public void ResolveDateRange_DefaultsToRecentSevenDays()
    {
        var (start, end, note) = AiAssistantTools.ResolveDateRange(null, null, Today);

        Assert.Equal(TokenUsageRanges.Recent(Today, 7), (start, end));
        Assert.Null(note);
    }

    /// <summary>get_token_usage 的 days（0.9.157 起）：沒給 start 時統計最近 N 天（含今天），夾在 1～366 並說明。</summary>
    [Theory]
    [InlineData(2, 2, false)]
    [InlineData(null, 7, false)]
    [InlineData(0, 1, true)]
    [InlineData(999, 366, true)]
    public void TokenDays_DefaultsAndClamps(int? days, int expected, bool hasNote)
    {
        var notes = new List<string>();

        Assert.Equal(expected, AiAssistantTools.TokenDays(days, notes));
        Assert.Equal(hasNote, notes.Count > 0);
    }

    [Fact]
    public void ResolveDateRange_InvalidOrReversed_FallsBackWithNote()
    {
        var (start, end, note) = AiAssistantTools.ResolveDateRange("上週", "2026-11-01", Today);

        Assert.Equal(new DateTime(2026, 11, 1), end);
        Assert.Equal(end.AddDays(-6), start);
        Assert.Contains("上週", note, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncateMessages_PerMessageAndTotalCaps()
    {
        var messages = Enumerable.Range(1, 6).Select(i => new AiCallLogMessage("user", $"第{i}則" + new string('長', 3000))).ToList();

        var (result, truncated) = AiAssistantTools.TruncateMessages(messages, perMessage: 2000, total: 8000);

        Assert.True(truncated);
        Assert.All(result, message => Assert.True(message.Content.Length <= 2001));
        Assert.True(result.Sum(message => message.Content.Length) <= 8000 + result.Count);
        Assert.StartsWith("第1則", result[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncateMessages_SmallConversation_Unchanged()
    {
        List<AiCallLogMessage> messages = [new("system", "指示"), new("user", "問題")];

        var (result, truncated) = AiAssistantTools.TruncateMessages(messages, perMessage: 2000, total: 8000);

        Assert.False(truncated);
        Assert.Equal(messages, result);
    }

    private static PromptTemplateVersion Version(int version, bool active)
        => new(version, $"內容 {version}", active, null, "support", Today);
}
