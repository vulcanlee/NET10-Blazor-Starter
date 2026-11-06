using System.ComponentModel.DataAnnotations;

namespace MyProject.AccessDatas.Models;

/// <summary>
/// AI 提示詞的一個版本（0.9.108 起）：管理員在「AI 提示詞」頁修改分析指示，每次儲存都新增一個版本，全部保留。
/// 同一個 <see cref="TemplateKey"/> 最多一個 <see cref="IsActive"/>；沒有任何作用中的版本時使用程式內建的預設。
/// 只存可修改的分析指示 —— 防注入與 Markdown 規則固定在程式裡，送出時自動接在最後，不存在這裡。
/// </summary>
public class PromptTemplate
{
    public int Id { get; set; }

    /// <summary><see cref="PromptTemplateKeys"/> 之一。</summary>
    [MaxLength(64)]
    public string TemplateKey { get; set; } = string.Empty;

    /// <summary>同一個範本內從 1 開始遞增；(TemplateKey, Version) 唯一。</summary>
    public int Version { get; set; }

    [MaxLength(8000)]
    public string Content { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    [MaxLength(200)]
    public string? Note { get; set; }

    [MaxLength(100)]
    public string? CreatedByAccount { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

public static class PromptTemplateKeys
{
    /// <summary>日誌檢視頁的 AI 分析。</summary>
    public const string LogAnalysis = "LogAnalysis";

    /// <summary>系統例外紀錄的 AI 例外分析（含追問）。</summary>
    public const string ExceptionAnalysis = "ExceptionAnalysis";

    /// <summary>「Agent 測試台」的範例 Agent（0.9.142 起）。</summary>
    public const string AgentPlayground = "AgentPlayground";

    /// <summary>「Agent 工作流程」專案狀態報告：蒐集（0.9.144 起，以下 7 種同）。</summary>
    public const string ProjectReportGatherer = "ProjectReportGatherer";

    /// <summary>專案狀態報告：撰寫。</summary>
    public const string ProjectReportWriter = "ProjectReportWriter";

    /// <summary>專案狀態報告：收尾（附加狀態摘要）。</summary>
    public const string ProjectReportFinalizer = "ProjectReportFinalizer";

    /// <summary>多觀點審查：需求完整性。</summary>
    public const string ReviewRequirements = "ReviewRequirements";

    /// <summary>多觀點審查：技術可行性與風險。</summary>
    public const string ReviewTechnical = "ReviewTechnical";

    /// <summary>多觀點審查：使用者體驗。</summary>
    public const string ReviewUsability = "ReviewUsability";

    /// <summary>多觀點審查：彙整。</summary>
    public const string ReviewAggregator = "ReviewAggregator";

    /// <summary>「系統管理助理」的共同說明（0.9.155 起，含巡檢報告格式；以下 5 種同）。</summary>
    public const string AdminAssistant = "AdminAssistant";

    /// <summary>系統管理助理：分派（判斷這則訊息屬於哪些領域）。</summary>
    public const string AdminAssistantRouter = "AdminAssistantRouter";

    /// <summary>系統管理助理：帳號與權限專家。</summary>
    public const string AdminAssistantAccounts = "AdminAssistantAccounts";

    /// <summary>系統管理助理：監控與診斷專家。</summary>
    public const string AdminAssistantMonitoring = "AdminAssistantMonitoring";

    /// <summary>系統管理助理：AI 管理專家。</summary>
    public const string AdminAssistantAi = "AdminAssistantAi";

    /// <summary>系統管理助理：系統設定與維運專家。</summary>
    public const string AdminAssistantOps = "AdminAssistantOps";

    public static readonly IReadOnlyList<string> All =
    [
        LogAnalysis, ExceptionAnalysis, AgentPlayground,
        ProjectReportGatherer, ProjectReportWriter, ProjectReportFinalizer,
        ReviewRequirements, ReviewTechnical, ReviewUsability, ReviewAggregator,
        AdminAssistant, AdminAssistantRouter, AdminAssistantAccounts, AdminAssistantMonitoring, AdminAssistantAi, AdminAssistantOps,
    ];
}
