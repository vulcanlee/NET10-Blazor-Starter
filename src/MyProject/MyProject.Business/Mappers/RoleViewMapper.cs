using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Models.AdapterModel;
using Riok.Mapperly.Abstractions;

namespace MyProject.Business.Mappers;

/// <summary>
/// 角色的對應（0.10.23 起以 Mapperly 在編譯時產生，取代 AutoMapper）。
/// 預設團隊在資料表是 JSON 字串（<see cref="TeamJsonHelper"/>）；權限矩陣（<c>RolePermission</c>）由服務另外組，不在對應裡。
/// 「→ 實體」正規化名稱、不帶軟刪除欄位。
/// </summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public static partial class RoleViewMapper
{
    [MapperIgnoreTarget(nameof(RoleViewAdapterModel.RolePermission))]
    [MapProperty(nameof(RoleView.DefaultTeamsJson), nameof(RoleViewAdapterModel.DefaultTeams), Use = nameof(ToTeams))]
    public static partial RoleViewAdapterModel ToModel(RoleView source);

    public static partial List<RoleViewAdapterModel> ToModels(IEnumerable<RoleView> source);

    [MapperIgnoreTarget(nameof(RoleView.IsDeleted))]
    [MapperIgnoreTarget(nameof(RoleView.DeletedAt))]
    [MapperIgnoreTarget(nameof(RoleView.DeletedBy))]
    [MapProperty(nameof(RoleViewAdapterModel.Name), nameof(RoleView.Name), Use = nameof(NormalizeName))]
    [MapProperty(nameof(RoleViewAdapterModel.DefaultTeams), nameof(RoleView.DefaultTeamsJson), Use = nameof(ToTeamsJson))]
    public static partial RoleView ToEntity(RoleViewAdapterModel source);

    [UserMapping(Default = false)]
    private static string NormalizeName(string? name) => NameNormalizer.Normalize(name);

    [UserMapping(Default = false)]
    private static List<string> ToTeams(string? json) => TeamJsonHelper.Deserialize(json);

    [UserMapping(Default = false)]
    private static string ToTeamsJson(List<string> teams) => TeamJsonHelper.Serialize(teams);
}
