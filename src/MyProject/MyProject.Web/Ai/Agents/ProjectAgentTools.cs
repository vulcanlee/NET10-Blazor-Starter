using System.ComponentModel;
using Microsoft.Extensions.AI;
using MyProject.Business.Services.DataAccess;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;

namespace MyProject.Web.Ai.Agents;

/// <summary>
/// 範例工具：專案的搜尋與修改描述（0.9.142 起，Agent 測試台使用；也是日後寫工具的範本）；
/// 0.9.144 起加上讀取專案明細與附加狀態摘要（Agent 工作流程「專案狀態報告」使用）。
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

    /// <summary>明細回給模型的描述最多幾個字（報告需要較完整的內容）。</summary>
    public const int MaxDetailDescriptionLength = 1000;

    /// <summary>狀態摘要最多幾個字（超過截斷）。</summary>
    public const int MaxSummaryLength = 200;

    /// <summary>附加在描述後面的狀態摘要開頭，例如「【狀態摘要 2026/11/04】」。</summary>
    public const string SummaryPrefix = "【狀態摘要 ";

    private readonly ProjectService projectService;
    private readonly TimeProvider timeProvider;

    public ProjectAgentTools(ProjectService projectService, TimeProvider? timeProvider = null)
    {
        this.projectService = projectService;
        this.timeProvider = timeProvider ?? TimeProvider.System;
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

    public static AiToolDefinition DetailTool { get; } = new()
    {
        Name = "get_project_detail",
        DisplayName = "讀取專案明細",
        Description = "讀取一個專案的完整資料：日期、狀態、優先等級、完成度、負責人、分類、團隊、描述與附件檔名。專案 Id 請先用 search_projects 查到。",
        PermissionPage = MagicObjectHelper.角色_專案項目,
        PermissionAction = PermissionActions.View,
        Create = services => AIFunctionFactory.Create(
            services.GetRequiredService<ProjectAgentTools>().GetProjectDetailAsync,
            "get_project_detail",
            "讀取專案明細。"),
    };

    public static AiToolDefinition AppendStatusSummaryTool { get; } = new()
    {
        Name = "append_project_status_summary",
        DisplayName = "附加專案狀態摘要",
        Description = $"在指定專案的描述最後面接上一行「{SummaryPrefix}日期】摘要」，原本的描述不變。摘要最多 {MaxSummaryLength} 字。會修改資料，系統會先請使用者核准。",
        PermissionPage = MagicObjectHelper.角色_專案項目,
        PermissionAction = PermissionActions.Edit,
        IsMutating = true,
        Create = services => AIFunctionFactory.Create(
            services.GetRequiredService<ProjectAgentTools>().AppendProjectStatusSummaryAsync,
            "append_project_status_summary",
            "附加專案狀態摘要。"),
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

    public async Task<ProjectDetail?> GetProjectDetailAsync([Description("專案 Id")] int projectId)
    {
        // 同 UpdateProjectDescriptionAsync：看不到的專案回空物件，不洩漏它是否存在。
        var project = await projectService.GetAsync(projectId);
        if (project.Id == 0)
        {
            return null;
        }

        return new ProjectDetail(
            project.Id,
            project.Title,
            project.Status,
            project.Priority,
            project.CompletionPercentage,
            project.Owner,
            project.StartDate?.ToString("yyyy/MM/dd"),
            project.EndDate?.ToString("yyyy/MM/dd"),
            project.Categories,
            project.Teams,
            Truncate(project.Description, MaxDetailDescriptionLength),
            project.Files.Select(file => file.OriginalFileName).ToList(),
            project.UpdatedAt.ToString("yyyy/MM/dd HH:mm"));
    }

    public async Task<string> AppendProjectStatusSummaryAsync(
        [Description("專案 Id")] int projectId,
        [Description("一行狀態摘要（最多 200 字，不要換行）")] string summary)
    {
        var project = await projectService.GetAsync(projectId);
        if (project.Id == 0)
        {
            return $"找不到 Id 為 {projectId} 的專案，或使用者沒有權限存取它，沒有修改。";
        }

        // 摘要收成一行（模型偶爾會帶換行），再截斷。
        var oneLine = string.Join(' ', summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var line = $"{SummaryPrefix}{timeProvider.GetLocalNow():yyyy/MM/dd}】{Truncate(oneLine, MaxSummaryLength)}";
        project.Description = string.IsNullOrWhiteSpace(project.Description)
            ? line
            : project.Description.TrimEnd() + "\n\n" + line;

        var result = await projectService.UpdateAsync(project);
        return result.Success
            ? $"已在專案「{project.Title}」（Id {projectId}）的描述後面附加狀態摘要。"
            : $"附加失敗：{result.Message}";
    }

    private static string Truncate(string? text, int maxLength = MaxDescriptionLength)
        => string.IsNullOrEmpty(text) || text.Length <= maxLength
            ? text ?? string.Empty
            : string.Concat(text.AsSpan(0, maxLength), "…");
}

/// <summary>搜尋結果回給模型的精簡欄位。</summary>
public sealed record ProjectSummary(int Id, string Title, string Status, string Description);

/// <summary>專案明細回給模型的欄位（0.9.144 起）。描述截斷、附件只給檔名。</summary>
public sealed record ProjectDetail(
    int Id,
    string Title,
    string Status,
    string Priority,
    int CompletionPercentage,
    string Owner,
    string? StartDate,
    string? EndDate,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Teams,
    string Description,
    IReadOnlyList<string> FileNames,
    string UpdatedAt);
