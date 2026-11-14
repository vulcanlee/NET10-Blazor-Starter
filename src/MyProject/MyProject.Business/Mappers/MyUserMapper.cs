using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Models.AdapterModel;
using MyProject.Models.Others;
using Riok.Mapperly.Abstractions;

namespace MyProject.Business.Mappers;

/// <summary>
/// 使用者的對應（0.10.23 起以 Mapperly 在編譯時產生，取代 AutoMapper）。
/// <list type="bullet">
/// <item>「→ 畫面」：<c>HasLocalPassword</c> 由密碼雜湊是否為空算出；角色經 <see cref="RoleViewMapper"/>；
/// 額外角色與團隊名稱由服務另外查，不在對應裡。</item>
/// <item>「→ 實體」：帳號去頭尾空白（0.9.171 起）；鎖定、失敗次數、密碼設定時間、安全戳記與兩步驟驗證只由登入、解鎖與密碼原則寫入，
/// 畫面模型上的值不寫回實體（0.9.101 起）；角色導覽屬性不帶；不帶軟刪除欄位。</item>
/// </list>
/// </summary>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
[UseStaticMapper(typeof(RoleViewMapper))]
public static partial class MyUserMapper
{
    [MapperIgnoreTarget(nameof(MyUserAdapterModel.AdditionalRoleIds))]
    [MapperIgnoreTarget(nameof(MyUserAdapterModel.TeamNames))]
    [MapPropertyFromSource(nameof(MyUserAdapterModel.HasLocalPassword), Use = nameof(HasLocalPassword))]
    public static partial MyUserAdapterModel ToModel(MyUser source);

    public static partial List<MyUserAdapterModel> ToModels(IEnumerable<MyUser> source);

    [MapperIgnoreTarget(nameof(MyUser.IsDeleted))]
    [MapperIgnoreTarget(nameof(MyUser.DeletedAt))]
    [MapperIgnoreTarget(nameof(MyUser.DeletedBy))]
    [MapperIgnoreTarget(nameof(MyUser.LockoutEndUtc))]
    [MapperIgnoreTarget(nameof(MyUser.AccessFailedCount))]
    [MapperIgnoreTarget(nameof(MyUser.PasswordChangedAtUtc))]
    [MapperIgnoreTarget(nameof(MyUser.SecurityStamp))]
    [MapperIgnoreTarget(nameof(MyUser.TwoFactorEnabled))]
    [MapperIgnoreTarget(nameof(MyUser.TwoFactorSecret))]
    [MapperIgnoreTarget(nameof(MyUser.TwoFactorLastStep))]
    [MapperIgnoreTarget(nameof(MyUser.RoleView))]
    [MapProperty(nameof(MyUserAdapterModel.Account), nameof(MyUser.Account), Use = nameof(NormalizeAccount))]
    public static partial MyUser ToEntity(MyUserAdapterModel source);

    /// <summary>目前登入者的基本資料；角色清單、團隊與是否已驗證由 CurrentUserLoader 另外填。</summary>
    [MapperIgnoreTarget(nameof(CurrentUser.RoleJson))]
    [MapperIgnoreTarget(nameof(CurrentUser.RoleList))]
    [MapperIgnoreTarget(nameof(CurrentUser.TeamList))]
    [MapperIgnoreTarget(nameof(CurrentUser.IsAuthenticated))]
    public static partial CurrentUser ToCurrentUser(MyUserAdapterModel source);

    [UserMapping(Default = false)]
    private static bool HasLocalPassword(MyUser source) => !string.IsNullOrEmpty(source.Password);

    [UserMapping(Default = false)]
    private static string NormalizeAccount(string? account) => NameNormalizer.Normalize(account);
}
