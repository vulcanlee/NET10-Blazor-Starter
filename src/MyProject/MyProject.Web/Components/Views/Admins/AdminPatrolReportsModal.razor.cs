using MyProject.Business.Services.DataAccess;
using MyProject.Models.Systems;
using MyProject.Web.Ai;

namespace MyProject.Web.Components.Views.Admins;

/// <summary>
/// 「巡檢報告」視窗（0.9.161 起）：左邊是最新的巡檢報告清單，右邊是選中的那一份。
///
/// ⚠️ 只放在已做過管理員檢查的系統管理助理頁裡（報告全體管理員共用，服務本身不分使用者）。
/// 報告是 AI 產出，一律經 <see cref="AiMarkdownRenderer.ToSafeHtml"/>，超過 <see cref="MaxRenderedHtmlLength"/> 就不顯示（同系統管理助理頁）。
/// </summary>
public partial class AdminPatrolReportsModal
{
    /// <summary>AI 報告轉成 HTML 之後的顯示上限（同其他 AI 畫面）。</summary>
    private const int MaxRenderedHtmlLength = 512 * 1024;

    private readonly AdminPatrolReportService reportService;
    private readonly ILogger<AdminPatrolReportsModal> logger;

    private bool visible;
    private bool isLoading;
    private IReadOnlyList<AdminPatrolReportSummary> reports = [];
    private AdminPatrolReportDetail? detail;
    private string? detailHtml;

    public AdminPatrolReportsModal(AdminPatrolReportService reportService, ILogger<AdminPatrolReportsModal> logger)
    {
        this.reportService = reportService;
        this.logger = logger;
    }

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

        var found = true;
        try
        {
            reports = await reportService.ListAsync();
            if (reportId is { } id && await LoadAsync(id))
            {
                return true;
            }

            found = reportId is null;
            if (reports.Count > 0)
            {
                await LoadAsync(reports[0].ReportId);
            }

            return found;
        }
        catch (Exception ex)
        {
            // 從 OnAfterRenderAsync（深連結）呼叫時，例外會讓整個 circuit 中斷；讀不到就顯示空清單並記錯誤。
            logger.LogError(ex, "Failed to load admin patrol reports.");
            reports = [];
            detail = null;
            detailHtml = null;
            return false;
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
        var loaded = await reportService.GetAsync(reportId);
        if (loaded is null)
        {
            logger.LogInformation("Admin patrol report not found. ReportId={ReportId}", reportId);
            return false;
        }

        detail = loaded;
        SelectedReportId = reportId;
        var html = loaded.Markdown is null ? null : AiMarkdownRenderer.ToSafeHtml(loaded.Markdown);
        detailHtml = html is not null && html.Length <= MaxRenderedHtmlLength ? html : null;
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
