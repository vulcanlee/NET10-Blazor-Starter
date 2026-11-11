using System.Xml.Linq;
using MyProject.Web.Controllers;

namespace MyProject.Tests;

/// <summary>
/// IIS 的 web.config（0.10.3 起）：附件經 HTTP 上傳，IIS 預設只收約 28.6 MB，沒放寬的話大檔在 IIS 上直接 404.13，
/// 本機（Kestrel）卻測不出來。這裡確認放寬的值與程式端的上限一致。
/// </summary>
public sealed class WebConfigTests
{
    [Fact]
    public void RequestLimits_ShouldAllowTheLargestProjectUpload()
    {
        var config = XDocument.Load(SourceTree.WebFile("web.config"));

        var limits = Assert.Single(config.Descendants("requestLimits"));

        Assert.Equal(ProjectController.MaxRequestBytes, long.Parse(limits.Attribute("maxAllowedContentLength")!.Value));
    }
}
