using MyProject.AccessDatas.Models;
using MyProject.Models.Systems;
using MyProject.Web.Configuration;

namespace MyProject.Web.Ai.Agents;

/// <summary>
/// 「Agent 測試台」的範例 Agent（0.9.142 起）：會搜尋專案、會在使用者核准後修改專案描述。
/// 用來驗收 Agent 骨架（工具、權限、核准、記帳），也是日後新增 Agent 的範本。
/// </summary>
public static class AgentPlaygroundAgent
{
    public static AiAgentDefinition Definition { get; } = new()
    {
        Name = "AgentPlayground",
        DisplayName = "專案小幫手（範例）",
        Operation = TokenUsageOperations.AgentPlayground,
        PromptTemplateKey = PromptTemplateKeys.AgentPlayground,
        ModelKey = nameof(AiModelSettings.AgentPlayground),
        Tools = [ProjectAgentTools.SearchTool, ProjectAgentTools.UpdateDescriptionTool],
    };

    /// <summary>內建的系統提示詞（管理員可在「AI 提示詞」頁另存版本取代）。</summary>
    public const string DefaultInstructions = """
        你是這套系統的「專案小幫手」，協助使用者查詢與整理專案資料。

        - 需要專案資料時，一律用 search_projects 查詢，不要憑空編造專案名稱、Id 或內容。
        - 使用者要求修改專案描述時，先用 search_projects 找到正確的專案並向使用者確認是哪一筆，
          再呼叫 update_project_description。系統會在執行前請使用者核准；被拒絕就照實告訴使用者沒有修改。
        - 工具回覆「沒有權限」時，照實告訴使用者需要向管理員申請權限，不要嘗試其他方式繞過。
        - 回答簡潔，列出專案時附上 Id 與名稱。
        """;
}
