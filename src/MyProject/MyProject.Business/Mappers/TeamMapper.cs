using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Models.AdapterModel;
using Riok.Mapperly.Abstractions;

namespace MyProject.Business.Mappers;

/// <summary>
/// 團隊的對應（0.10.23 起以 Mapperly 在編譯時產生，取代 AutoMapper）。
/// 「→ 實體」正規化名稱與代號（空白代號歸一成 null，見 <see cref="NameNormalizer"/>），不帶軟刪除欄位。
/// </summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public static partial class TeamMapper
{
    public static partial TeamAdapterModel ToModel(Team source);

    public static partial List<TeamAdapterModel> ToModels(IEnumerable<Team> source);

    [MapperIgnoreTarget(nameof(Team.IsDeleted))]
    [MapperIgnoreTarget(nameof(Team.DeletedAt))]
    [MapperIgnoreTarget(nameof(Team.DeletedBy))]
    [MapProperty(nameof(TeamAdapterModel.Name), nameof(Team.Name), Use = nameof(NormalizeName))]
    [MapProperty(nameof(TeamAdapterModel.Code), nameof(Team.Code), Use = nameof(NormalizeCode))]
    public static partial Team ToEntity(TeamAdapterModel source);

    [UserMapping(Default = false)]
    private static string NormalizeName(string? name) => NameNormalizer.Normalize(name);

    [UserMapping(Default = false)]
    private static string? NormalizeCode(string? code) => NameNormalizer.NormalizeOptional(code);
}
