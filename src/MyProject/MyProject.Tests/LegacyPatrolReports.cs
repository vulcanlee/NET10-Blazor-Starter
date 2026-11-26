using Microsoft.EntityFrameworkCore;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;

namespace MyProject.Tests;

/// <summary>
/// 一份舊版巡檢報告（0.9.161～0.10.38 的排程作業產生）。0.10.39 起不再產生新報告，0.10.43 起正式程式也拿掉了寫入路徑，
/// 測試要佈置舊報告時用 <see cref="LegacyPatrolReports.SeedAsync"/>，照當時的寫法放好內容檔與資料列。
/// </summary>
public sealed record LegacyPatrolReport(
    Guid ReportId,
    DateTime CreatedAt,
    string Status = AdminPatrolStatuses.Normal,
    string AttentionSections = "",
    string Trigger = JobRunTriggers.Manual,
    string? TriggeredByAccount = "support",
    int JobRunId = 42,
    string? ModelName = "gpt-test",
    bool IsTruncated = false,
    string Markdown = "## 總結\n正常。");

/// <summary>佈置舊版巡檢報告：內容檔 <c>{巡檢根目錄}/{yyyyMM}/{ReportId:N}.md</c>＋資料列（同原本的排程作業）。</summary>
public static class LegacyPatrolReports
{
    /// <returns>內容檔的相對路徑。</returns>
    public static async Task<string> SeedAsync(IDbContextFactory<BackendDBContext> contextFactory, string aiAgentConversationPath, LegacyPatrolReport report)
    {
        var relativePath = $"{report.CreatedAt:yyyyMM}/{report.ReportId:N}.md";
        var fullPath = Path.Combine(aiAgentConversationPath, AdminPatrolReportFileStore.FolderName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, report.Markdown);

        await using var context = await contextFactory.CreateDbContextAsync();
        context.AdminPatrolReport.Add(new AdminPatrolReport
        {
            ReportId = report.ReportId,
            CreatedAt = report.CreatedAt,
            Status = report.Status,
            AttentionSections = report.AttentionSections,
            Trigger = report.Trigger,
            TriggeredByAccount = report.TriggeredByAccount,
            JobRunId = report.JobRunId,
            ModelName = report.ModelName,
            IsTruncated = report.IsTruncated,
            ContentFile = relativePath,
        });
        await context.SaveChangesAsync();
        return relativePath;
    }
}
