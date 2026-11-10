using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Web.Ai.Agents.AdminAssistant;

namespace MyProject.Tests;

/// <summary>定時巡檢（0.9.161 起）：有「注意／異常」時發給全體管理員的通知（站內＋Email，摘要＋連結）。</summary>
public sealed class AdminPatrolNotificationTests
{
    private static readonly Guid ReportId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly DateTime CreatedAt = new(2026, 11, 8, 7, 30, 5);

    private const string Markdown = """
        ## 總結
        有兩個排程失敗，需要處理。

        ## 健康狀態
        狀態：正常
        - 一切正常。

        ## 排程與備份
        狀態：異常
        - 「系統備份」昨天 03:00 失敗。
        - 「日誌清理」逾期未執行。
        - 第三點。
        - 第四點不應出現。

        ## 帳號與安全
        狀態：注意
        - 有 2 個帳號被鎖定，詳見 https://example.com/x。

        ## 建議處理
        1. 到排程作業頁查看。
        """;

    [Fact]
    public void Build_ListsOnlyAttentionSections_WithSummaryAndLink()
    {
        var assessment = AdminPatrolReportParser.Parse(Markdown, isTruncated: false);

        var request = AdminPatrolNotification.Build(ReportId, CreatedAt, assessment);

        Assert.Equal(NotificationCategories.AdminPatrol, request.Category);
        Assert.Equal("系統巡檢：異常（2026/11/08）", request.Title);
        Assert.True(request.AlsoEmail);
        Assert.True(request.Target.Admins);
        Assert.Equal($"AdminPatrol:{ReportId:N}", request.SourceKey);
        Assert.Equal("/admin-assistant?patrolReport=0f8fad5b-d9cb-469f-a165-70867728950e", request.Link);

        var body = request.Body!;
        Assert.StartsWith("有兩個排程失敗，需要處理。", body, StringComparison.Ordinal);
        Assert.Contains("【排程與備份】異常", body, StringComparison.Ordinal);
        Assert.Contains("- 「系統備份」昨天 03:00 失敗。", body, StringComparison.Ordinal);
        Assert.Contains("- 第三點。", body, StringComparison.Ordinal);
        Assert.DoesNotContain("第四點", body, StringComparison.Ordinal);
        Assert.Contains("【帳號與安全】注意", body, StringComparison.Ordinal);
        Assert.Contains("（連結已移除）", body, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", body, StringComparison.Ordinal);
        Assert.DoesNotContain("健康狀態", body, StringComparison.Ordinal);
        Assert.EndsWith(AdminPatrolNotification.Footer, body, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_LongReport_BodyStaysWithinLimit_AndKeepsFooter()
    {
        var sections = string.Concat(Enumerable.Range(1, 30).Select(i =>
            $"\n## 段落{i}\n狀態：異常\n- {new string('長', 300)}\n- {new string('字', 300)}\n"));
        var assessment = AdminPatrolReportParser.Parse("## 總結\n" + new string('總', 500) + sections, isTruncated: false);

        var request = AdminPatrolNotification.Build(ReportId, CreatedAt, assessment);

        Assert.True(request.Body!.Length <= AdminPatrolNotification.BodyMaxLength);
        Assert.EndsWith(AdminPatrolNotification.Footer, request.Body, StringComparison.Ordinal);
        Assert.True(request.Link!.Length <= 200);
        Assert.True(request.Title.Length <= 100);
    }

    [Fact]
    public void Build_Unreadable_ExplainsWhy()
    {
        var assessment = AdminPatrolReportParser.Parse("看起來都很好。", isTruncated: false);

        var request = AdminPatrolNotification.Build(ReportId, CreatedAt, assessment);

        Assert.Equal("系統巡檢：無法判讀（2026/11/08）", request.Title);
        Assert.Contains(assessment.Reason!, request.Body, StringComparison.Ordinal);
        Assert.EndsWith(AdminPatrolNotification.Footer, request.Body, StringComparison.Ordinal);
    }

    /// <summary>通知的連結要真的打得開：系統管理助理頁收這個查詢參數，並交給視圖打開那份報告。</summary>
    [Fact]
    public void AdminAssistantPage_AcceptsTheDeepLinkParameter()
    {
        var web = FindWebProjectDirectory();
        var page = File.ReadAllText(Path.Combine(web, "Components", "Pages", "Admins", "AdminAssistantPage.razor"));
        var view = File.ReadAllText(Path.Combine(web, "Components", "Views", "Admins", "AdminAssistantView.razor"));

        Assert.Contains("@page \"/admin-assistant\"", page, StringComparison.Ordinal);
        Assert.Contains("[SupplyParameterFromQuery(Name = \"patrolReport\")]", page, StringComparison.Ordinal);
        Assert.Contains("InitialPatrolReportId=", page, StringComparison.Ordinal);
        Assert.Contains("<AdminPatrolReportsModal @ref=", view, StringComparison.Ordinal);
    }

    [Fact]
    public void LinkOf_IsParsableDeepLink()
    {
        var link = AdminPatrolNotification.LinkOf(ReportId);

        Assert.StartsWith("/admin-assistant?patrolReport=", link, StringComparison.Ordinal);
        Assert.True(Guid.TryParse(link["/admin-assistant?patrolReport=".Length..], out var parsed));
        Assert.Equal(ReportId, parsed);
    }

    private static string FindWebProjectDirectory() => SourceTree.Web;
}
