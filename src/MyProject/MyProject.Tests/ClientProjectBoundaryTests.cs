using System.Xml.Linq;

namespace MyProject.Tests;

/// <summary>
/// WASM 前端（<c>MyProject.Web.Client</c>，0.10.0 起）在使用者的瀏覽器執行，只能參考可以公開的共用專案：
/// Dtos、Models、Share。參考 Business、AccessDatas 或 Web 會把伺服器端的程式（資料庫、金鑰、商業規則）打包給瀏覽器下載。
/// </summary>
public class ClientProjectBoundaryTests
{
    [Fact]
    public void ClientProject_ShouldOnlyReferenceTheSharedProjects()
    {
        var csproj = XDocument.Load(Path.Combine(SourceTree.Client, "MyProject.Web.Client.csproj"));

        var references = csproj.Descendants("ProjectReference")
            .Select(x => Path.GetFileNameWithoutExtension(x.Attribute("Include")!.Value.Replace("\\", "/", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["MyProject.Dtos", "MyProject.Models", "MyProject.Share"], references);
        Assert.Equal("Microsoft.NET.Sdk.BlazorWebAssembly", csproj.Root!.Attribute("Sdk")!.Value);
    }
}
