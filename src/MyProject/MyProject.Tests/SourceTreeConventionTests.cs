namespace MyProject.Tests;

/// <summary>
/// 測試要讀原始碼、設定檔或文件時，一律經 <see cref="SourceTree"/> 取得路徑（0.10.0 起）。
/// 以前 37 個測試檔各自從執行目錄往上找 MyProject.Web；WASM 遷移把畫面搬到 MyProject.Web.Client 時，
/// 要改的地方只剩 SourceTree 一處。
/// </summary>
public class SourceTreeConventionTests
{
    [Fact]
    public void TestFiles_ShouldNotWalkUpFromTheBaseDirectoryThemselves()
    {
        var testsRoot = Path.Combine(SourceTree.SourceRoot, "MyProject.Tests");
        var allowed = new[]
        {
            Path.Combine(testsRoot, "Infrastructure", "SourceTree.cs"),
            Path.Combine(testsRoot, nameof(SourceTreeConventionTests) + ".cs"),
        };

        var offenders = Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(testsRoot, path))
            .Where(path => !allowed.Contains(path, StringComparer.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains("new DirectoryInfo(AppContext.BaseDirectory)", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(testsRoot, path))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "下列測試檔自行往上找目錄，請改用 SourceTree：" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void SourceTree_ShouldPointAtTheRealProjectFolders()
    {
        Assert.True(File.Exists(Path.Combine(SourceTree.SourceRoot, "MyProject.slnx")));
        Assert.True(File.Exists(SourceTree.WebFile("MyProject.Web.csproj")));
        Assert.True(File.Exists(Path.Combine(SourceTree.Components, "App.razor")));
        Assert.True(Directory.Exists(Path.Combine(SourceTree.Client, "Pages")));
        Assert.True(File.Exists(Path.Combine(SourceTree.Docs, "README.md")));
        Assert.True(File.Exists(Path.Combine(SourceTree.Scripts, "Invoke-QualityGate.ps1")));
        Assert.Equal(Path.Combine(SourceTree.Docs, "README.md"), SourceTree.Resolve(Path.Combine("docs", "README.md")));
        Assert.Equal(SourceTree.Web, SourceTree.Resolve("MyProject.Web"));
    }

    private static bool IsBuildOutput(string root, string path)
    {
        var segments = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
        return segments.Contains("bin") || segments.Contains("obj");
    }
}
