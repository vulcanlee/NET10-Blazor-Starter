using System.ComponentModel;
using Microsoft.Extensions.AI;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.SystemReports;
using static MyProject.Web.Ai.Agents.AdminAssistant.AdminAssistantToolText;

namespace MyProject.Web.Ai.Agents.AdminAssistant;

/// <summary>
/// 系統管理助理的巡檢工具（0.10.42 起，系統巡檢報告 M4）：用系統巡檢報告的規則引擎產生指定區間的報告。
///
/// ⚠️ 這個工具會存一份報告，但 <b>不經核准窗</b>（<see cref="AiToolDefinition.IsMutating"/> 為 false）——
/// 使用者 2026/11/20 決定的明文例外：只寫一份內部報告、不寄信、不改任何業務資料。只在巡檢回合載入（<see cref="AdminAssistantAgent.PatrolTools"/>）。
/// 燈號與數字由程式判定，回給模型的只有規則的結果（不含帳號、路徑、例外內文）。
/// </summary>
public sealed class ReportAssistantTools
{
    private readonly SystemReportAssistantService service;
    private readonly AiAgentReportOutputs outputs;

    public ReportAssistantTools(SystemReportAssistantService service, AiAgentReportOutputs outputs)
    {
        this.service = service;
        this.outputs = outputs;
    }

    private static readonly string RunSystemReportDescription = "用固定規則巡檢系統並存成一份系統巡檢報告（指定區間），回傳整體燈號、結論、每一項問題（燈號、標題、現況、期限、處理頁面）、"
            + "需要擴充的磁碟與下期預測。燈號：Red＝已發生問題、Orange＝即將發生（有期限）、Yellow＝注意、Green＝正常。"
            + $"使用者沒講日期時兩個日期都不要填（＝昨天到現在）；有講就原樣填入使用者說的日期（例如 11/1、2026-11-01），程式會補年份。一次最多 {SystemReportAssistantService.MaxDays} 天。"
            + "產生需要數十秒；報告不寄信。";

    public static AiToolDefinition RunSystemReportTool { get; } = new()
    {
        Name = "run_system_report",
        DisplayName = "產生系統巡檢報告",
        Description = RunSystemReportDescription,
        PermissionPage = MagicObjectHelper.角色_系統巡檢報告,
        PermissionAction = PermissionActions.View,
        Create = services => AIFunctionFactory.Create(
            services.GetRequiredService<ReportAssistantTools>().RunSystemReportAsync, "run_system_report", RunSystemReportDescription),
    };

    public async Task<string> RunSystemReportAsync(
        [Description("起日，原樣填入使用者說的日期（例如 11/1 或 2026-11-01）；使用者沒講日期就不要填")] string? startDate = null,
        [Description("迄日（含），原樣填入使用者說的日期；只有一天時可不填")] string? endDate = null,
        CancellationToken cancellationToken = default)
    {
        var range = service.ResolveRange(startDate, endDate);
        if (range.Period is not { } period)
        {
            return ToJson(new { error = range.Error });
        }

        var result = await service.GenerateAsync(period, cancellationToken);
        var content = result.Content;
        if (result.ReportId is { } reportId)
        {
            outputs.Add(reportId, SystemReportPdfBuilder.Title(content));
        }

        return ToJson(new
        {
            saved = result.ReportId is not null,
            note = result.ReportId is null ? "報告產生了但存檔失敗，畫面上沒有「查看報告」按鈕；以下是這次判定的結果。" : "報告已存檔，回覆下方會出現「查看報告」「下載 PDF」按鈕。",
            // 區間寫到分鐘：迄日是今天時，數字只到產生報告的這一刻。
            period = $"{period.Start:yyyy/MM/dd HH:mm}～{(period.End > content.CreatedAt ? content.CreatedAt : period.End):yyyy/MM/dd HH:mm}",
            level = SystemReportLevels.ShortName(content.Level),
            verdict = content.Verdict,
            findings = content.Findings.Select(x => new
            {
                level = SystemReportLevels.ShortName(x.Level),
                section = x.Section,
                title = x.Title,
                detail = x.Detail,
                deadline = x.Deadline?.ToString("yyyy/MM/dd"),
                howToFix = x.HowToFix,
                page = SystemReportPdfBuilder.PageName(x.ActionRoute),
            }),
            expansions = content.Expansions.Select(x => new { volume = x.Volume, fullDate = x.FullDate?.ToString("yyyy/MM/dd"), suggestedGb = Math.Round(x.SuggestedBytes / 1024d / 1024 / 1024, 1) }),
            predictions = content.Predictions,
            stats = content.Stats.Select(x => $"{x.Section}／{x.Label}：{x.Value}"),
        });
    }
}
