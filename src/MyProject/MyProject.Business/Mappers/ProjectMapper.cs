using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Models.AdapterModel;
using Riok.Mapperly.Abstractions;

namespace MyProject.Business.Mappers;

/// <summary>
/// 專案與附件的對應（0.10.23 起以 Mapperly 在編譯時產生，取代 AutoMapper）。
/// 分類與團隊在資料表是換行分隔的字串、畫面上是清單，一律經 <see cref="TagStringHelper"/> 轉換。
/// 「→ 實體」不帶軟刪除欄位（刪除與還原只經由服務，見 SoftDeleteHelper）與附件（附件另有上傳流程）。
/// </summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public static partial class ProjectMapper
{
    [MapProperty(nameof(Project.Categories), nameof(ProjectAdapterModel.Categories), Use = nameof(ToTagList))]
    [MapProperty(nameof(Project.Teams), nameof(ProjectAdapterModel.Teams), Use = nameof(ToTagList))]
    public static partial ProjectAdapterModel ToModel(Project source);

    public static partial List<ProjectAdapterModel> ToModels(IEnumerable<Project> source);

    [MapperIgnoreTarget(nameof(Project.IsDeleted))]
    [MapperIgnoreTarget(nameof(Project.DeletedAt))]
    [MapperIgnoreTarget(nameof(Project.DeletedBy))]
    [MapperIgnoreTarget(nameof(Project.Files))]
    [MapProperty(nameof(ProjectAdapterModel.Categories), nameof(Project.Categories), Use = nameof(ToStoredTags))]
    [MapProperty(nameof(ProjectAdapterModel.Teams), nameof(Project.Teams), Use = nameof(ToStoredTags))]
    public static partial Project ToEntity(ProjectAdapterModel source);

    public static partial ProjectFileAdapterModel ToModel(ProjectFile source);

    [UserMapping(Default = false)]
    private static List<string> ToTagList(string? stored) => TagStringHelper.ToList(stored);

    [UserMapping(Default = false)]
    private static string? ToStoredTags(List<string> tags) => TagStringHelper.ToStored(tags);
}
