using System.ComponentModel;
using Microsoft.Extensions.AI;
using MyProject.Business.Services.DataAccess;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;

namespace MyProject.Web.Ai.Agents;

/// <summary>
/// 範例工具：專案的搜尋與修改描述（0.9.142 起，Agent 測試台使用；也是日後寫工具的範本）。
///
/// <para>
/// 一律呼叫既有的 <see cref="ProjectService"/>：團隊範圍限制、稽核（<c>Project.Update</c>）與並行版本號照常生效，
/// 工具本身不重寫任何商業規則。回給模型的內容刻意精簡（只給需要的欄位、描述截斷），省 token 也少外洩。
/// </para>
/// </summary>
public sealed class ProjectAgentTools
{
    /// <summary>搜尋最多回幾筆。</summary>
    public const int MaxResults = 10;

    /// <summary>回給模型的描述最多幾個字。</summary>
    public const int MaxDescriptionLength = 200;

    private readonly ProjectService projectService;

    public ProjectAgentTools(ProjectService projectService)
    {
        this.projectService = projectService;
    }

    public static AiToolDefinition SearchTool { get; } = new()
    {
        Name = "search_projects",
        DisplayName = "搜尋專案",
        Description = $"依關鍵字搜尋專案（比對名稱、描述、狀態、優先等級、負責人），最多回傳 {MaxResults} 筆。只會看到使用者有權限看的專案。",
        PermissionPage = MagicObjectHelper.角色_專案項目,
        PermissionAction = PermissionActions.View,
        Create = services => AIFunctionFactory.Create(
            services.GetRequiredService<ProjectAgentTools>().SearchProjectsAsync,
            "search_projects",
            "依關鍵字搜尋專案。"),
    };

    public static AiToolDefinition UpdateDescriptionTool { get; } = new()
    {
        Name = "update_project_description",
        DisplayName = "修改專案描述",
        Description = "把指定專案的描述改成新的內容（整段取代）。會修改資料，系統會先請使用者核准。專案 Id 請先用 search_projects 查到。",
        PermissionPage = MagicObjectHelper.角色_專案項目,
        PermissionAction = PermissionActions.Edit,
        IsMutating = true,
        Create = services => AIFunctionFactory.Create(
            services.GetRequiredService<ProjectAgentTools>().UpdateProjectDescriptionAsync,
            "update_project_description",
            "修改專案描述。"),
    };

    public async Task<IReadOnlyList<ProjectSummary>> SearchProjectsAsync(
        [Description("搜尋關鍵字；空字串代表列出最新的專案")] string keyword)
    {
        var result = await projectService.GetAsync(new DataRequest
        {
            Search = keyword,
            CurrentPage = 1,
            PageSize = MaxResults,
            Take = MaxResults,
        });

        return result.Result
            .Select(project => new ProjectSummary(
                project.Id,
                project.Title,
                project.Status,
                Truncate(project.Description)))
            .ToList();
    }

    public async Task<string> UpdateProjectDescriptionAsync(
        [Description("專案 Id")] int projectId,
        [Description("新的描述（整段取代原本的描述）")] string description)
    {
        // GetAsync 已套用團隊範圍：看不到的專案回空物件（Id 為 0），不洩漏它是否存在。
        var project = await projectService.GetAsync(projectId);
        if (project.Id == 0)
        {
            return $"找不到 Id 為 {projectId} 的專案，或使用者沒有權限存取它，沒有修改。";
        }

        project.Description = description;
        var result = await projectService.UpdateAsync(project);
        return result.Success
            ? $"已把專案「{project.Title}」（Id {projectId}）的描述更新。"
            : $"修改失敗：{result.Message}";
    }

    private static string Truncate(string? text)
        => string.IsNullOrEmpty(text) || text.Length <= MaxDescriptionLength
            ? text ?? string.Empty
            : string.Concat(text.AsSpan(0, MaxDescriptionLength), "…");
}

/// <summary>搜尋結果回給模型的精簡欄位。</summary>
public sealed record ProjectSummary(int Id, string Title, string Status, string Description);
