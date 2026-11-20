using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MyProject.Dtos.Admins;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Ai;
using MyProject.Web.Client.Api;

namespace MyProject.Web.Client.Views.Admins;

/// <summary>
/// 系統巡檢報告的內容視窗（0.10.39 起）。資料走 <see cref="SystemReportApi"/>（只限管理員；看報告不寫稽核）。
/// AI 說明是模型產出，一律經 <see cref="AiMarkdownRenderer.ToSafeHtml"/>，超過 <see cref="MaxRenderedHtmlLength"/> 就不顯示；
/// 其餘內容都是程式產生的純文字（Blazor 會自動編碼）。
/// </summary>
public partial class SystemReportDetailModal
{
    private const int MaxRenderedHtmlLength = 512 * 1024;

    private sealed record SummaryGroup(string Heading, IReadOnlyList<SystemReportFindingDto> Items);

    private bool visible;
    private bool isLoading;
    private string title = "系統巡檢報告";
    private string loadError = string.Empty;
    private SystemReportDetailDto? detail;
    private string? narrativeHtml;

    [Inject]
    public SystemReportApi Api { get; set; } = default!;

    [Inject]
    public ILogger<SystemReportDetailModal> Logger { get; set; } = default!;

    public async Task OpenAsync(Guid reportId)
    {
        visible = true;
        isLoading = true;
        detail = null;
        narrativeHtml = null;
        title = "系統巡檢報告";
        StateHasChanged();
        try
        {
            var result = await Api.GetAsync(reportId);
            if (!result.Success || result.Data is not { } loaded)
            {
                Logger.LogInformation("System report not found. ReportId={ReportId}, Status={Status}", reportId, result.StatusCode);
                loadError = result.UserMessage();
                return;
            }

            detail = loaded;
            var summary = loaded.Summary;
            var last = summary.PeriodEnd.AddDays(-1);
            var period = last.Date == summary.PeriodStart.Date ? $"{summary.PeriodStart:yyyy-MM-dd}" : $"{summary.PeriodStart:yyyy-MM-dd}～{last:yyyy-MM-dd}";
            title = $"系統巡檢{SystemReportFrequencies.DisplayName(summary.Frequency)}　{period}";
            var html = loaded.Content?.Narrative is { } markdown ? AiMarkdownRenderer.ToSafeHtml(markdown) : null;
            narrativeHtml = html is not null && html.Length <= MaxRenderedHtmlLength ? html : null;
            Logger.LogDebug("System report opened. ReportId={ReportId}", reportId);
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }

    /// <summary>摘要只列紅燈、橙燈與黃燈（黃燈最多 10 項）。</summary>
    private static IEnumerable<SummaryGroup> SummaryGroups(SystemReportContentDto content)
    {
        var red = content.Findings.Where(x => x.Level == SystemReportLevels.Red).ToList();
        var orange = content.Findings.Where(x => x.Level == SystemReportLevels.Orange).ToList();
        var yellow = content.Findings.Where(x => x.Level == SystemReportLevels.Yellow).Take(10).ToList();
        if (red.Count > 0)
        {
            yield return new SummaryGroup("已發生問題（要馬上處理）", red);
        }

        if (orange.Count > 0)
        {
            yield return new SummaryGroup("即將發生（有期限）", orange);
        }

        if (yellow.Count > 0)
        {
            yield return new SummaryGroup("注意即可", yellow);
        }
    }

    private static string FolderSize(SystemReportFolderDto folder) => folder.Status switch
    {
        CapacitySnapshotKinds.StatusMissing => "沒有設定或不存在",
        CapacitySnapshotKinds.StatusPartial => $"至少 {SizeFormatHelper.FormatBytes(folder.SizeBytes ?? 0)}（量測逾時）",
        _ => SizeFormatHelper.FormatBytes(folder.SizeBytes ?? 0),
    };
}
