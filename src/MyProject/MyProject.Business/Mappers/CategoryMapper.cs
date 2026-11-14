using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Models.AdapterModel;
using Riok.Mapperly.Abstractions;

namespace MyProject.Business.Mappers;

/// <summary>
/// 分類的對應（0.10.23 起以 Mapperly 在編譯時產生，取代 AutoMapper）。
/// 「→ 實體」一律以 <see cref="NameNormalizer"/> 正規化名稱：不論從哪裡進來，寫進資料庫的值都已去除前後空白，
/// 唯一性檢查才不會出現「比對的與儲存的不是同一個字串」；也不帶軟刪除欄位。
/// </summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public static partial class CategoryMapper
{
    [MapProperty(nameof(Category.Teams), nameof(CategoryAdapterModel.Teams), Use = nameof(ToTagList))]
    public static partial CategoryAdapterModel ToModel(Category source);

    public static partial List<CategoryAdapterModel> ToModels(IEnumerable<Category> source);

    [MapperIgnoreTarget(nameof(Category.IsDeleted))]
    [MapperIgnoreTarget(nameof(Category.DeletedAt))]
    [MapperIgnoreTarget(nameof(Category.DeletedBy))]
    [MapProperty(nameof(CategoryAdapterModel.Name), nameof(Category.Name), Use = nameof(NormalizeName))]
    [MapProperty(nameof(CategoryAdapterModel.Teams), nameof(Category.Teams), Use = nameof(ToStoredTags))]
    public static partial Category ToEntity(CategoryAdapterModel source);

    [UserMapping(Default = false)]
    private static string NormalizeName(string? name) => NameNormalizer.Normalize(name);

    [UserMapping(Default = false)]
    private static List<string> ToTagList(string? stored) => TagStringHelper.ToList(stored);

    [UserMapping(Default = false)]
    private static string? ToStoredTags(List<string> tags) => TagStringHelper.ToStored(tags);
}
