using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MyProject.Business.Services.Other;
using MyProject.Web.Components.Commons;

namespace MyProject.Tests.Components;

/// <summary>
/// 通知裡的「複製」按鈕：結果直接顯示在按鈕文字上，幾秒後恢復成「複製」。
/// 0.9.130 起，管理員複製的內容另外附上完整例外（0.10.9 起只有 WASM 頁面，Server 版另外依 ExceptionDetailPolicy 放行的規則隨之移除）。
/// </summary>
public sealed class ToastCopyButtonTests : ComponentTestBase
{
    private const string CopyFunction = "appClipboard.copyText";
    private const string Detail = "System.InvalidOperationException: boom\n   at MyProject.Web.Sample.Run()";

    [Fact]
    public void Click_WhenCopied_ShouldShowCopied_ThenRestore()
    {
        AddCopyServices(Services);
        JSInterop.Setup<bool>(CopyFunction, _ => true).SetResult(true);
        var cut = RenderButton();

        cut.Find("button").Click();

        Assert.Equal("已複製", cut.Find("button").TextContent.Trim());
        cut.WaitForAssertion(() => Assert.Equal("複製", cut.Find("button").TextContent.Trim()), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Click_WhenBrowserRefuses_ShouldShowCopyFailed()
    {
        AddCopyServices(Services);
        JSInterop.Setup<bool>(CopyFunction, _ => true).SetResult(false);
        var cut = RenderButton();

        cut.Find("button").Click();

        Assert.Equal("複製失敗", cut.Find("button").TextContent.Trim());
    }

    [Fact]
    public void Click_ShouldCopyTitleTextTraceCodeTimeAndThePageItAppearedOn()
    {
        AddCopyServices(Services);

        Assert.Equal(
            string.Join(Environment.NewLine,
                "系統訊息",
                "名稱重複",
                "錯誤追蹤碼：ABCD1234",
                "時間：2026/10/19 14:03:22",
                "頁面：http://localhost/"),
            ClickAndReadClipboard(detail: null));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Click_WithDetail_ShouldAppendItOnlyForAdmins(bool isAdmin, bool expected)
    {
        AddCopyServices(Services, isAdmin);

        var copied = ClickAndReadClipboard(Detail);

        Assert.Equal(expected, copied.Contains("----- 錯誤詳細資料開始 -----", StringComparison.Ordinal));
        Assert.Equal(expected, copied.Contains(Detail, StringComparison.Ordinal));
        Assert.Contains("名稱重複", copied, StringComparison.Ordinal);
    }

    /// <summary>按鈕需要的服務；預設是一般使用者（拿不到例外細節）。</summary>
    internal static void AddCopyServices(IServiceCollection services, bool isAdmin = false)
    {
        services.AddSingleton(new CurrentUserService { CurrentUser = { IsAdmin = isAdmin } });
        TestPagePermissions.AddTo(services);
    }

    private string ClickAndReadClipboard(string? detail)
    {
        JSInterop.Setup<bool>(CopyFunction, _ => true).SetResult(true);
        var cut = RenderButton(detail);

        cut.Find("button").Click();

        return Assert.IsType<string>(Assert.Single(JSInterop.Invocations[CopyFunction]).Arguments[0]);
    }

    private IRenderedComponent<ToastCopyButton> RenderButton(string? detail = null)
        => Render<ToastCopyButton>(parameters => parameters
            .Add(p => p.Title, "系統訊息")
            .Add(p => p.Description, "名稱重複")
            .Add(p => p.TraceCode, "ABCD1234")
            .Add(p => p.OccurredAt, new DateTime(2026, 10, 19, 14, 3, 22))
            .Add(p => p.Detail, detail));
}
