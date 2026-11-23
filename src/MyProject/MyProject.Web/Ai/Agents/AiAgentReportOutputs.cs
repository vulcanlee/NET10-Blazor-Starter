namespace MyProject.Web.Ai.Agents;

/// <summary>這一輪工具產生的系統巡檢報告（報告編號與標題）。</summary>
public sealed record AiAgentReportOutput(Guid ReportId, string Title);

/// <summary>
/// 工具與對話之間的交接（0.10.42 起，scoped：同一個請求共用一個）：工具產生報告後放進來，
/// <see cref="AiAgentChat"/> 在這一輪結束時取出，在回覆下方加一則 <c>Report</c> 逐字稿（畫面顯示「查看報告」「下載 PDF」）。
/// 工具回給模型的文字不必帶任何畫面用的資料。
/// </summary>
public sealed class AiAgentReportOutputs
{
    private readonly List<AiAgentReportOutput> pending = [];
    private readonly Lock gate = new();

    public void Add(Guid reportId, string title)
    {
        lock (gate)
        {
            pending.Add(new AiAgentReportOutput(reportId, title));
        }
    }

    /// <summary>取出目前累積的，並清空。</summary>
    public IReadOnlyList<AiAgentReportOutput> Drain()
    {
        lock (gate)
        {
            var items = pending.ToList();
            pending.Clear();
            return items;
        }
    }
}
