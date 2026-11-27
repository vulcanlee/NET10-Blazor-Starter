using System.Reflection;
using MyProject.Web.Client.Api;

namespace MyProject.Tests;

/// <summary>
/// 前端 API 用戶端的誤用防呆（0.10.47 起）。<see cref="UserApi"/> 繼承 <see cref="CrudApi{TDto, TSearch}"/> 後多了兩個不該用的方法：
/// <list type="bullet">
/// <item><c>UpdateAsync(int, object)</c>：只回成功與否，會丟掉「管理員改到自己帳號」時伺服器回的新 Token；使用者一律用 <c>UpdateAsync(UserDto)</c>。</item>
/// <item><c>NameConflictAsync(object)</c>：只認 409；使用者的帳號檢查還包含格式，要用 <c>AccountConflictAsync</c>。</item>
/// </list>
/// <see cref="UserApi"/> 以 <c>new</c> 遮蔽它們並標成過時（編譯錯誤），這裡確認遮蔽還在。
/// </summary>
public sealed class ClientApiMisuseGuardTests
{
    public static TheoryData<string, Type[]> HiddenUserApiMethods => new()
    {
        { "UpdateAsync", [typeof(int), typeof(object)] },
        { "NameConflictAsync", [typeof(object)] },
    };

    [Theory]
    [MemberData(nameof(HiddenUserApiMethods))]
    public void UserApi_ShouldHideTheInheritedMethodThatLosesInformation(string name, Type[] parameterTypes)
    {
        var method = typeof(UserApi).GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, parameterTypes);

        Assert.NotNull(method);
        var obsolete = method.GetCustomAttribute<ObsoleteAttribute>();
        Assert.NotNull(obsolete);
        Assert.True(obsolete.IsError);
    }
}
