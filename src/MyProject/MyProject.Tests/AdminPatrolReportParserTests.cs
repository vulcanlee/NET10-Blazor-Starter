using MyProject.Models.Systems;
using MyProject.Web.Ai.Agents.AdminAssistant;

namespace MyProject.Tests;

/// <summary>定時巡檢（0.9.161 起）：從巡檢報告每段第一行的「狀態：正常／注意／異常」判讀整體狀態。</summary>
public sealed class AdminPatrolReportParserTests
{
    private static string Report(
        string health = "正常", string errors = "正常", string jobs = "正常", string usage = "正常", string accounts = "正常")
        => $"""
        ## 總結
        系統整體穩定，沒有需要立即處理的事。

        ## 健康狀態
        狀態：{health}
        - 資料庫、AI、SMTP 都正常。

        ## 例外與錯誤
        狀態：{errors}
        - 最近 24 小時 3 筆例外。
        - 主要是逾時。

        ## 排程與備份
        狀態：{jobs}
        - 全部排程成功。

        ## 資源用量
        狀態：{usage}
        - 資料庫 12 MB。

        ## 帳號與安全
        狀態：{accounts}
        - 沒有鎖定的帳號。

        ## 建議處理
        1. 無。
        """;

    [Fact]
    public void AllNormal_IsNormal_AndNeedsNoAttention()
    {
        var result = AdminPatrolReportParser.Parse(Report(), isTruncated: false);

        Assert.Equal(AdminPatrolStatuses.Normal, result.Overall);
        Assert.False(result.NeedsAttention);
        Assert.Equal(string.Empty, result.AttentionSectionNames);
        Assert.Equal("系統整體穩定，沒有需要立即處理的事。", result.Summary);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void OneAttention_IsAttention_WithSectionName()
    {
        var result = AdminPatrolReportParser.Parse(Report(jobs: "注意"), isTruncated: false);

        Assert.Equal(AdminPatrolStatuses.Attention, result.Overall);
        Assert.True(result.NeedsAttention);
        Assert.Equal("排程與備份", result.AttentionSectionNames);
    }

    [Fact]
    public void AbnormalWins_OverAttention()
    {
        var result = AdminPatrolReportParser.Parse(Report(errors: "異常", accounts: "注意"), isTruncated: false);

        Assert.Equal(AdminPatrolStatuses.Abnormal, result.Overall);
        Assert.Equal("例外與錯誤、帳號與安全", result.AttentionSectionNames);
        var errors = Assert.Single(result.Sections, section => section.Heading == "例外與錯誤");
        Assert.Equal(["最近 24 小時 3 筆例外。", "主要是逾時。"], errors.KeyPoints);
    }

    [Theory]
    [InlineData("**狀態：** 異常")]
    [InlineData("**狀態：異常**")]
    [InlineData("狀態: 異常")]
    [InlineData("- 狀態：異常")]
    [InlineData("> 狀態：異常")]
    [InlineData("\n狀態：異常")]
    public void StatusLine_Variants_AreRecognized(string statusLine)
    {
        var markdown = $"""
            ##健康狀態
            {statusLine}
            - AI 探測失敗。
            """;

        var result = AdminPatrolReportParser.Parse(markdown, isTruncated: false);

        Assert.Equal(AdminPatrolStatuses.Abnormal, result.Overall);
        Assert.Equal("健康狀態", result.AttentionSectionNames);
    }

    /// <summary>審查發現：模型常在開頭加 H1 標題、段落內用 ### 分小節，這些不能被當成「該寫狀態卻沒寫」的段落。</summary>
    [Fact]
    public void TitleAndSubheadings_DoNotCountAsSections()
    {
        var markdown = "# 系統巡檢報告（2026/11/07）\n\n" + Report().ReplaceLineEndings("\n")
            .Replace("- 沒有鎖定的帳號。", "### 鎖定帳號\n- 沒有鎖定的帳號。\n### 2FA\n- 管理員都已啟用。", StringComparison.Ordinal);

        var result = AdminPatrolReportParser.Parse(markdown, isTruncated: false);

        Assert.Equal(AdminPatrolStatuses.Normal, result.Overall);
        Assert.Contains("鎖定帳號", Assert.Single(result.Sections, section => section.Heading == "帳號與安全").KeyPoints);
    }

    [Fact]
    public void UnknownSectionWithoutStatus_IsIgnored()
    {
        var markdown = Report() + "\n\n## 補充說明\n- 本次巡檢範圍是最近 24 小時。";

        Assert.Equal(AdminPatrolStatuses.Normal, AdminPatrolReportParser.Parse(markdown, isTruncated: false).Overall);
    }

    [Theory]
    [InlineData("狀態：不正常")]
    [InlineData("狀態：無異常")]
    [InlineData("狀態：未見異常")]
    public void StatusValue_MustStartWithTheStatusWord(string statusLine)
    {
        var markdown = Report().ReplaceLineEndings("\n").Replace("## 健康狀態\n狀態：正常", "## 健康狀態\n" + statusLine, StringComparison.Ordinal);

        var result = AdminPatrolReportParser.Parse(markdown, isTruncated: false);

        Assert.Equal(AdminPatrolStatuses.Attention, result.Overall);
        Assert.Null(Assert.Single(result.Sections, section => section.Heading == "健康狀態").Status);
    }

    [Fact]
    public void StatusValue_WithExplanation_IsRecognized()
    {
        var markdown = Report(accounts: "注意（2 個帳號被鎖定）");

        Assert.Equal(AdminPatrolStatuses.Attention, AdminPatrolReportParser.Parse(markdown, isTruncated: false).Overall);
    }

    [Fact]
    public void MissingKnownSection_IsAttention_WithReason()
    {
        var markdown = Report().ReplaceLineEndings("\n").Replace("## 帳號與安全\n狀態：正常\n- 沒有鎖定的帳號。\n", string.Empty, StringComparison.Ordinal);

        var result = AdminPatrolReportParser.Parse(markdown, isTruncated: false);

        Assert.Equal(AdminPatrolStatuses.Attention, result.Overall);
        Assert.Contains("帳號與安全", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusLine_ListingAllStatuses_IsIgnored()
    {
        var markdown = """
            ## 健康狀態
            狀態：正常／注意／異常
            狀態：正常
            """;

        var result = AdminPatrolReportParser.Parse(markdown, isTruncated: false);

        Assert.Equal(AdminPatrolStatuses.Normal, Assert.Single(result.Sections).Status);
    }

    [Fact]
    public void NoStatusLines_IsUnreadable_AndNeedsAttention()
    {
        var result = AdminPatrolReportParser.Parse("系統看起來都很好，沒有問題。", isTruncated: false);

        Assert.Equal(AdminPatrolStatuses.Unreadable, result.Overall);
        Assert.True(result.NeedsAttention);
        Assert.NotNull(result.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_IsUnreadable(string? markdown)
    {
        Assert.Equal(AdminPatrolStatuses.Unreadable, AdminPatrolReportParser.Parse(markdown, isTruncated: false).Overall);
    }

    [Fact]
    public void SectionWithoutStatus_CountsAsAttention()
    {
        var markdown = Report().ReplaceLineEndings("\n").Replace("狀態：正常\n- 資料庫 12 MB。", "- 資料庫 12 MB。", StringComparison.Ordinal);

        var result = AdminPatrolReportParser.Parse(markdown, isTruncated: false);

        Assert.Equal(AdminPatrolStatuses.Attention, result.Overall);
        Assert.Equal("資源用量", result.AttentionSectionNames);
    }

    [Fact]
    public void QueryFailed_SaidNormal_CountsAsAttention()
    {
        var markdown = Report().Replace("- 沒有鎖定的帳號。", "- 目前無法查詢：工具逾時。", StringComparison.Ordinal);

        var result = AdminPatrolReportParser.Parse(markdown, isTruncated: false);

        Assert.Equal(AdminPatrolStatuses.Attention, result.Overall);
        Assert.Equal("帳號與安全", result.AttentionSectionNames);
    }

    [Fact]
    public void Truncated_AllNormal_IsAttention_WithReason()
    {
        var result = AdminPatrolReportParser.Parse(Report(), isTruncated: true);

        Assert.Equal(AdminPatrolStatuses.Attention, result.Overall);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void SummaryAndSuggestions_StatusLines_AreNotCounted()
    {
        var markdown = Report()
            .Replace("系統整體穩定，沒有需要立即處理的事。", "狀態：異常", StringComparison.Ordinal)
            .Replace("1. 無。", "狀態：異常", StringComparison.Ordinal);

        var result = AdminPatrolReportParser.Parse(markdown, isTruncated: false);

        Assert.Equal(AdminPatrolStatuses.Normal, result.Overall);
    }

    [Fact]
    public void UnknownSectionHeading_IsStoredAsOther()
    {
        var markdown = Report() + "\n\n## 第三方服務\n狀態：異常\n- 金流 API 無回應。";

        var result = AdminPatrolReportParser.Parse(markdown, isTruncated: false);

        Assert.Equal(AdminPatrolStatuses.Abnormal, result.Overall);
        Assert.Equal(AdminPatrolSections.Other, result.AttentionSectionNames);
        Assert.Contains(result.Sections, section => section.Heading == "第三方服務" && section.Status == AdminPatrolStatuses.Abnormal);
    }

    [Theory]
    [InlineData("- **資料庫** 用量 `12 MB`", 120, "資料庫 用量 12 MB")]
    [InlineData("1. 到[排程作業](/scheduled-jobs)頁查看", 120, "到排程作業頁查看")]
    [InlineData("詳見 https://evil.example.com/x?y=1 說明", 120, "詳見 （連結已移除） 說明")]
    [InlineData("請到 evil.example.com/reset 重設", 120, "請到 （連結已移除） 重設")]
    [InlineData("或 www.phish.tw 也可以", 120, "或 （連結已移除） 也可以")]
    [InlineData("資料庫 12.4 MB，BackendDB.db 正常", 120, "資料庫 12.4 MB，BackendDB.db 正常")]
    [InlineData("一二三四五六", 4, "一二三…")]
    public void ToPlainLine_StripsMarkdownAndUrls(string line, int maxLength, string expected)
    {
        Assert.Equal(expected, AdminPatrolReportParser.ToPlainLine(line, maxLength));
    }
}
