using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MyProject.Models.Systems;
using MyProject.Web.Ai;
using MyProject.Web.Client.Api;

namespace MyProject.Web.Client.Views.Admins;

/// <summary>
/// 「舊版巡檢報告」視窗（0.9.161 起）：左邊是最新的巡檢報告清單，右邊是選中的那一份。
/// 0.10.39 起從「系統巡檢報告」頁打開，資料走 <see cref="SystemReportApi"/> 的 legacy 端點（只限管理員；看報告不寫稽核）。
///
/// ⚠️ 只放在已做過管理員檢查的頁面裡（報告全體管理員共用）。
/// 報告是 AI 產出，一律經 <see cref="AiMarkdownRenderer.ToSafeHtml"/>，超過 <see cref="AiMarkdownRenderer.MaxRenderedHtmlLength"/> 就不顯示（同系統管理助理頁）。
/// </summary>
public partial class AdminPatrolReportsModal
{
    private bool visible;
    private bool isLoading;
    private int retentionDays;
    private IReadOnlyList<AdminPatrolReportSummary> reports = [];
    private AdminPatrolReportDetail? detail;
    private string? detailHtml;

    [Inject]
    public SystemReportApi Api { get; set; } = default!;

    [Inject]
    public ILogger<AdminPatrolReportsModal> Logger { get; set; } = default!;

    /// <summary>目前選中的報告。</summary>
    public Guid? SelectedReportId { get; private set; }

    /// <summary>
    /// 打開視窗並載入清單：指定 <paramref name="reportId"/> 時選那一份（深連結），否則選最新的一份。
    /// </summary>
    /// <returns>指定的報告找不到時回 false（視窗照樣打開、選最新的一份），呼叫端負責提示。</returns>
    public async Task<bool> OpenAsync(Guid? reportId = null)
    {
        visible = true;
        isLoading = true;
        detail = null;
        detailHtml = null;
        SelectedReportId = null;
        StateHasChanged();

        try
        {
            var list = await Api.LegacyListAsync();
            if (list.Success == false || list.Data is not { } data)
            {
                // 讀不到就顯示空清單並記錯誤（同 Server 版）。
                Logger.LogError("Failed to load admin patrol reports. Message={Message}", list.UserMessage());
                reports = [];
                return false;
            }

            retentionDays = data.RetentionDays;
            reports = data.Items.Select(AgentDtoMapper.ToModel).ToList();
            if (reportId is { } id && await LoadAsync(id))
            {
                return true;
            }

            var found = reportId is null;
            if (reports.Count > 0)
            {
                await LoadAsync(reports[0].ReportId);
            }

            return found;
        }
        finally
        {
            isLoading = false;
            StateHasChanged();
        }
    }

    private async Task SelectAsync(Guid reportId)
    {
        await LoadAsync(reportId);
        StateHasChanged();
    }

    private async Task<bool> LoadAsync(Guid reportId)
    {
        var result = await Api.LegacyGetAsync(reportId);
        if (result.Success == false || result.Data is not { } loaded)
        {
            Logger.LogInformation("Admin patrol report not found. ReportId={ReportId}, Status={Status}", reportId, result.StatusCode);
            return false;
        }

        detail = AgentDtoMapper.ToModel(loaded);
        SelectedReportId = reportId;
        detailHtml = detail.Markdown is null ? null : AiMarkdownRenderer.ToSafeHtmlWithinLimit(detail.Markdown);
        return true;
    }

    private static string StatusColor(string status) => status switch
    {
        AdminPatrolStatuses.Normal => "green",
        AdminPatrolStatuses.Attention => "orange",
        AdminPatrolStatuses.Abnormal => "red",
        _ => "default",
    };

    private static string TriggerText(AdminPatrolReportSummary item) => item.Trigger switch
    {
        JobRunTriggers.Manual => $"手動執行（{item.TriggeredByAccount}）",
        JobRunTriggers.CatchUp => "補跑",
        _ => "排程",
    };
}
