using System.Reflection;
using MyProject.AccessDatas.Models;
using MyProject.Business.Mappers;
using MyProject.Models.AdapterModel;
using MyProject.Models.Others;

namespace MyProject.Tests;

/// <summary>
/// 資料物件與畫面模型的對應（0.10.23 起以 Mapperly 取代 AutoMapper，對應在編譯時產生）。
/// 守住改寫前的行為：標籤字串與清單互轉、名稱與代號正規化、計算欄位，
/// 以及「→ 實體」一律不帶軟刪除欄位、帳號的安全欄位與導覽屬性。
/// </summary>
public sealed class MapperTests
{
    [Fact]
    public void Project_ToModel_SplitsTags_AndMapsFiles()
    {
        var entity = new Project
        {
            Id = 3,
            Title = "標題",
            Categories = "甲\n乙",
            Teams = null,
            Files = [new ProjectFile { Id = 9, OriginalFileName = "a.pdf", FileSize = 12 }],
        };

        var model = ProjectMapper.ToModel(entity);

        Assert.Equal(["甲", "乙"], model.Categories);
        Assert.Empty(model.Teams);
        Assert.Equal(("a.pdf", 12L), (Assert.Single(model.Files).OriginalFileName, model.Files[0].FileSize));
    }

    [Fact]
    public void Project_ToEntity_JoinsTags_AndSkipsSoftDeleteAndFiles()
    {
        var model = new ProjectAdapterModel
        {
            Title = "標題",
            Categories = ["甲", "乙"],
            Teams = [],
            IsDeleted = true,
            DeletedAt = DateTime.Now,
            DeletedBy = "someone",
            Files = [new ProjectFileAdapterModel { Id = 9 }],
        };

        var entity = ProjectMapper.ToEntity(model);

        Assert.Equal(TagStringHelperProbe.Stored(["甲", "乙"]), entity.Categories);
        Assert.Equal(TagStringHelperProbe.Stored([]), entity.Teams);
        AssertNotDeleted(entity);
        Assert.Empty(entity.Files);
    }

    [Fact]
    public void Category_ToEntity_NormalizesTheName_AndJoinsTeams()
    {
        var entity = CategoryMapper.ToEntity(new CategoryAdapterModel { Name = "　分類 \t", Teams = ["A"], IsDeleted = true, DeletedBy = "x" });

        Assert.Equal("分類", entity.Name);
        Assert.Equal(TagStringHelperProbe.Stored(["A"]), entity.Teams);
        AssertNotDeleted(entity);
        Assert.Equal(["A"], CategoryMapper.ToModel(entity).Teams);
    }

    [Theory]
    [InlineData(" T01 ", "T01")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Team_ToEntity_NormalizesNameAndOptionalCode(string? code, string? expected)
    {
        var entity = TeamMapper.ToEntity(new TeamAdapterModel { Name = " 團隊 ", Code = code, IsDeleted = true });

        Assert.Equal("團隊", entity.Name);
        Assert.Equal(expected, entity.Code);
        AssertNotDeleted(entity);
    }

    [Fact]
    public void RoleView_RoundTrip_KeepsDefaultTeams_AndNormalizesTheName()
    {
        var entity = RoleViewMapper.ToEntity(new RoleViewAdapterModel { Name = " 角色 ", DefaultTeams = ["A", "B"], IsDeleted = true });

        Assert.Equal("角色", entity.Name);
        AssertNotDeleted(entity);
        Assert.Equal(["A", "B"], RoleViewMapper.ToModel(entity).DefaultTeams);
    }

    [Fact]
    public void MyUser_ToModel_ComputesHasLocalPassword_AndMapsTheRole()
    {
        var model = MyUserMapper.ToModel(new MyUser
        {
            Account = "amy",
            Password = "hash",
            RoleView = new RoleView { Name = "角色", DefaultTeamsJson = "[\"A\"]" },
        });

        Assert.True(model.HasLocalPassword);
        Assert.Equal(["A"], model.RoleView!.DefaultTeams);
        Assert.False(MyUserMapper.ToModel(new MyUser { Account = "g", Password = string.Empty }).HasLocalPassword);
    }

    /// <summary>鎖定、密碼設定時間、安全戳記與兩步驟驗證只由登入與密碼原則寫入，畫面模型上的值不寫回實體。</summary>
    [Fact]
    public void MyUser_ToEntity_TrimsTheAccount_AndNeverCopiesSecurityFields()
    {
        var entity = MyUserMapper.ToEntity(new MyUserAdapterModel
        {
            Account = " amy ",
            LockoutEndUtc = DateTime.UtcNow,
            PasswordChangedAtUtc = DateTime.UtcNow,
            TwoFactorEnabled = true,
            RoleView = new RoleViewAdapterModel { Name = "角色" },
            IsDeleted = true,
        });

        Assert.Equal("amy", entity.Account);
        Assert.Null(entity.LockoutEndUtc);
        Assert.Null(entity.PasswordChangedAtUtc);
        Assert.False(entity.TwoFactorEnabled);
        Assert.Null(entity.TwoFactorSecret);
        Assert.Null(entity.TwoFactorLastStep);
        Assert.Null(entity.RoleView);
        AssertNotDeleted(entity);
    }

    [Fact]
    public void MyUser_ToCurrentUser_CopiesIdentity_AndLeavesRolesForTheLoader()
    {
        var user = MyUserMapper.ToCurrentUser(new MyUserAdapterModel { Id = 5, Account = "amy", Name = "艾咪", IsAdmin = true, TeamNames = ["A"] });

        Assert.Equal((5, "amy", "艾咪", true), (user.Id, user.Account, user.Name, user.IsAdmin));
        Assert.False(user.IsAuthenticated);
        Assert.Empty(user.RoleList);
        Assert.Empty(user.TeamList);
    }

    [Fact]
    public void AiCallLog_ToModel_ComputesHasContent()
    {
        Assert.True(LogMappers.ToModel(new AiCallLog { ContentFile = "202611/a.json" }).HasContent);
        Assert.False(LogMappers.ToModel(new AiCallLog { ContentFile = null }).HasContent);
    }

    [Fact]
    public void AuditLog_ToModel_KeepsTheUtcTimeAsIs()
    {
        var at = new DateTime(2026, 11, 14, 1, 2, 3, DateTimeKind.Utc);

        Assert.Equal(at, LogMappers.ToModel(new AuditLog { Action = "X", OccurredAt = at }).OccurredAt);
    }

    /// <summary>
    /// 守門：每一個「→ 實體」（回傳 <see cref="ISoftDeletable"/>）的對應方法都不能帶入軟刪除欄位；
    /// 刪除與還原只能經由服務的刪除／還原方法（見 SoftDeleteHelper）。以實際呼叫驗證（Mapperly 的標註只在編譯時存在），
    /// 新增實體的對應時忘了加 [MapperIgnoreTarget] 會在這裡失敗。
    /// </summary>
    [Fact]
    public void EveryMappingToASoftDeletableEntity_ShouldIgnoreTheSoftDeleteFields()
    {
        var methods = typeof(ProjectMapper).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(ProjectMapper).Namespace && type.IsAbstract && type.IsSealed)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => typeof(ISoftDeletable).IsAssignableFrom(method.ReturnType) && method.GetParameters().Length == 1)
            .ToList();

        Assert.NotEmpty(methods);
        foreach (var method in methods)
        {
            var sourceType = method.GetParameters()[0].ParameterType;
            var source = Activator.CreateInstance(sourceType)!;
            sourceType.GetProperty(nameof(ISoftDeletable.IsDeleted))?.SetValue(source, true);
            sourceType.GetProperty(nameof(ISoftDeletable.DeletedAt))?.SetValue(source, DateTime.Now);
            sourceType.GetProperty(nameof(ISoftDeletable.DeletedBy))?.SetValue(source, "someone");

            var entity = (ISoftDeletable)method.Invoke(null, [source])!;

            Assert.True(
                entity is { IsDeleted: false, DeletedAt: null, DeletedBy: null },
                $"{method.DeclaringType!.Name}.{method.Name} 必須以 [MapperIgnoreTarget] 忽略 IsDeleted、DeletedAt、DeletedBy。");
        }
    }

    private static void AssertNotDeleted(ISoftDeletable entity)
    {
        Assert.False(entity.IsDeleted);
        Assert.Null(entity.DeletedAt);
        Assert.Null(entity.DeletedBy);
    }

    /// <summary>標籤的儲存格式以 TagStringHelper 為準（測試不重寫一份規則）。</summary>
    private static class TagStringHelperProbe
    {
        public static string? Stored(List<string> tags) => MyProject.Business.Helpers.TagStringHelper.ToStored(tags);
    }
}
