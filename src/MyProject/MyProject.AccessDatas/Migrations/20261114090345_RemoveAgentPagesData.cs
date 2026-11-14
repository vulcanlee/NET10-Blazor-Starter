using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyProject.AccessDatas.Migrations;

/// <summary>
/// 0.10.22 起移除「Agent 測試台」與「Agent 工作流程」：資料表結構不變（對話表與系統管理助理共用），
/// 只把既有資料庫裡專為這兩頁存在的資料清掉——權限、角色對應與角色的權限清單、提示詞版本、系統參數。
/// 這兩頁的對話與執行紀錄（<c>AiAgentConversation</c>）不在這裡刪：交給「Agent 對話清理」排程依保留天數連同內容檔一起刪。
/// </summary>
public partial class _20261114090345_RemoveAgentPagesData : Migration
{
    /// <summary>三個權限鍵（MagicObjectHelper 0.10.21 的角色_AI助理、角色_Agent測試台、角色_Agent工作流程）。</summary>
    private const string PermissionKeys = "'AI 助理', 'Agent 測試台', 'Agent 工作流程'";

    /// <summary>八個提示詞種類（PromptTemplateKeys 0.10.21）。</summary>
    private const string PromptKeys =
        "'AgentPlayground', 'ProjectReportGatherer', 'ProjectReportWriter', 'ProjectReportFinalizer', " +
        "'ReviewRequirements', 'ReviewTechnical', 'ReviewUsability', 'ReviewAggregator'";

    /// <summary>六個系統參數（AiModelSettings 與 AiReasoningSettings 各三個）。</summary>
    private const string ParameterKeys =
        "'AiModelSettings:AgentPlayground', 'AiModelSettings:ProjectStatusReport', 'AiModelSettings:MultiPerspectiveReview', " +
        "'AiReasoningSettings:AgentPlayground', 'AiReasoningSettings:ProjectStatusReport', 'AiReasoningSettings:MultiPerspectiveReview'";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql($"DELETE FROM RolePermissionMap WHERE PermissionId IN (SELECT Id FROM Permission WHERE Key IN ({PermissionKeys}));");
        migrationBuilder.Sql($"DELETE FROM Permission WHERE Key IN ({PermissionKeys});");

        // 角色的權限清單（JSON 字串陣列）拿掉這三個鍵，其餘照原本的順序（ORDER BY key＝陣列索引）；不是合法 JSON 的不動（交給 RBAC 正規化）。
        migrationBuilder.Sql($"""
            UPDATE RoleView
            SET TabViewJson = (SELECT json_group_array(value ORDER BY key) FROM json_each(RoleView.TabViewJson) WHERE value NOT IN ({PermissionKeys}))
            WHERE json_valid(TabViewJson) AND EXISTS (SELECT 1 FROM json_each(RoleView.TabViewJson) WHERE value IN ({PermissionKeys}));
            """);

        migrationBuilder.Sql($"DELETE FROM PromptTemplate WHERE TemplateKey IN ({PromptKeys});");
        migrationBuilder.Sql($"DELETE FROM SystemParameter WHERE ParameterKey IN ({ParameterKeys});");
    }

    /// <summary>不還原：刪掉的是已不存在的功能的資料，程式也不再讀它們（需要時從 tag「AI助理-0.10.21」與備份取回）。</summary>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
