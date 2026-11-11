using MyProject.AccessDatas.Models;

namespace MyProject.Web.Ai;

/// <summary>
/// AI 提示詞各種類的顯示名稱（頁籤標題）。0.10.5 起（WASM 遷移 M6）從 Server 版頁面搬來：
/// 提示詞頁的 API（<c>PromptTemplatesController</c>）與系統管理助理的提示詞工具共用這一份。
/// </summary>
public static class PromptTemplateTitles
{
    public static string Of(string key) => key switch
    {
        PromptTemplateKeys.LogAnalysis => "日誌分析",
        PromptTemplateKeys.ExceptionAnalysis => "例外分析",
        PromptTemplateKeys.AgentPlayground => "Agent 測試台",
        PromptTemplateKeys.ProjectReportGatherer => "狀態報告／蒐集",
        PromptTemplateKeys.ProjectReportWriter => "狀態報告／撰寫",
        PromptTemplateKeys.ProjectReportFinalizer => "狀態報告／收尾",
        PromptTemplateKeys.ReviewRequirements => "審查／需求完整性",
        PromptTemplateKeys.ReviewTechnical => "審查／技術可行性",
        PromptTemplateKeys.ReviewUsability => "審查／使用者體驗",
        PromptTemplateKeys.ReviewAggregator => "審查／彙整",
        PromptTemplateKeys.AdminAssistant => "管理助理／共同",
        PromptTemplateKeys.AdminAssistantRouter => "管理助理／分派",
        PromptTemplateKeys.AdminAssistantAccounts => "管理助理／帳號與權限",
        PromptTemplateKeys.AdminAssistantMonitoring => "管理助理／監控與診斷",
        PromptTemplateKeys.AdminAssistantAi => "管理助理／AI 管理",
        PromptTemplateKeys.AdminAssistantOps => "管理助理／系統設定與維運",
        _ => key,
    };
}
