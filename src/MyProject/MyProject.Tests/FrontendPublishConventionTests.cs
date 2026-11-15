using System.Xml.Linq;

namespace MyProject.Tests;

/// <summary>
/// 同站台發佈時，Web 發佈後會再發佈一次前端（MyProject.Web.csproj 的 UseProcessedFrontendIndexHtml）。
/// &lt;MSBuild&gt; 工作會繼承外層的全域屬性，所以外層發佈用的屬性要用 RemoveProperties 拿掉，前端才發佈得起來。
/// </summary>
public class FrontendPublishConventionTests
{
    /// <summary>
    /// 0.10.30 起：Visual Studio 發佈會把 DeployOnBuild=true 設成全域屬性，傳給前端時，前端在 Build 之後又觸發一次 DotNetPublish，
    /// 造成 MSB4006 循環相依（Publish ← _CopyAspNetCoreFilesToIntermediateOutputPath ← CorePublish ← DotNetPublish ← _PublishBuildAlternative ← Publish）。
    /// 命令列 dotnet publish 不會設 DeployOnBuild，所以品質關卡測不到，只能靠這個測試守門。
    /// </summary>
    [Theory]
    [InlineData("DeployOnBuild")]          // VS 發佈：沒拿掉會 MSB4006 循環相依
    [InlineData("WebPublishProfileFile")]  // VS 發佈：SDK 在 ProjectReference 也拿掉它
    [InlineData("PublishProfile")]
    [InlineData("RuntimeIdentifier")]      // -r win-x64 會讓前端發佈失敗（0.10.10）
    public void FrontendPublish_ShouldNotInheritTheOuterPublishProperty(string property)
    {
        var project = XDocument.Load(SourceTree.WebFile("MyProject.Web.csproj"));
        var target = project.Descendants("Target").Single(x => (string?)x.Attribute("Name") == "UseProcessedFrontendIndexHtml");
        var removed = ((string?)target.Element("MSBuild")?.Attribute("RemoveProperties") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Contains(property, removed);
    }
}
