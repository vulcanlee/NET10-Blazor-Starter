using System.Text.RegularExpressions;
using MyProject.Models.Systems;

namespace MyProject.Tests;

/// <summary>
/// 腳手架腳本的守門。
///
/// 1. 會執行 <c>dotnet ef</c> 的腳本都要把外部路徑導到暫存資料夾（速查表 §6.6）。
///
/// <c>dotnet ef</c> 會執行 <c>Program.cs</c> 到 <c>Build()</c> 為止，途中依設定檔建立 <see cref="ExternalFileSystem"/> 的每個目錄；
/// 沒隔離的話，產生 CRUD 模組或新專案時會在開發機的 <c>C:\temp\…</c> 建目錄、寫日誌。
/// 新增一個路徑時兩支腳本都要加，漏了不會壞、只會默默寫錯地方，所以由這裡守門（0.9.175 加 <c>AuditKeyPath</c> 時兩邊都要手動補）。
/// 2. <c>New-StarterProject.ps1</c> 最後的殘留掃描必須是零命中：文件寫明「正常複刻不會印出任何警告」，
///    一條固定出現的假警報會讓人忽略真正沒換到的東西。
/// </summary>
public sealed class ScaffoldScriptConventionTests
{
    /// <summary>
    /// 殘留掃描會找 JWT 開發用佔位金鑰的前綴；完整的佔位值會被腳本換成新代號，但只寫前綴（例如在說明文字裡）就換不到、一定被掃到。
    /// 腳本自己不做取代也不掃描，所以排除。
    /// </summary>
    [Fact]
    public void NoFile_ShouldContainTheBarePlaceholderPrefix()
    {
        // 拆成兩段拼起來：這個檔案自己也不能出現完整的前綴字樣。
        const string prefix = "DevelopmentOnly-" + "ChangeThisJwtSigningKey";
        const string full = prefix + "-AtLeast32Chars";
        string[] textExtensions = [".cs", ".csproj", ".slnx", ".json", ".md", ".razor", ".css", ".js", ".ps1", ".yml", ".yaml", ".config", ".xml", ".tmpl", ".gitignore", ".props"];
        var root = FindRepoRoot();
        var offenders = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => textExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => path.Split(Path.DirectorySeparatorChar).Any(x => x is ".git" or "bin" or "obj" or "artifacts" or "node_modules") == false)
            .Where(path => Path.GetFileName(path) != "New-StarterProject.ps1")
            .Where(path => File.ReadAllText(path).Replace(full, string.Empty, StringComparison.Ordinal).Contains(prefix, StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();

        Assert.True(offenders.Count == 0, "這些檔案只寫了 JWT 佔位金鑰的前綴，複刻新專案時殘留掃描會固定印出警告（請改寫成「DevelopmentOnly-…」）：" + string.Join("、", offenders));
    }

    /// <summary>會啟動主機的腳本（dotnet ef 與實機驗證用的 Start-E2EHost.ps1，0.10.0 起）。</summary>
    public static TheoryData<string> Scripts => ["New-CrudModule.ps1", "New-StarterProject.ps1", "Start-E2EHost.ps1"];

    [Theory]
    [MemberData(nameof(Scripts))]
    public void EfScripts_ShouldIsolateEveryExternalPath_AndTheLogFolder(string script)
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), "scripts", script));
        var isolated = Regex.Matches(text, @"SystemSettings__ExternalFileSystem__(\w+)")
            .Select(x => x.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        var expected = typeof(ExternalFileSystem).GetProperties()
            .Where(x => x.PropertyType == typeof(string))
            .Select(x => x.Name)
            .ToList();

        var missing = expected.Where(x => isolated.Contains(x) == false).ToList();
        Assert.True(missing.Count == 0, $"{script} 執行 dotnet ef 前沒有把這些路徑導到暫存資料夾：" + string.Join("、", missing));
        Assert.Contains("NLog__BasePath", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 衍生專案會清空 migration、重建單一的 <c>Init</c>，所以驗證腳手架自己升級路徑的測試（指定某一個 migration 名稱、
    /// 或用 <c>IMigrator</c> 遷移到中間版本）在新專案必定失敗，<c>New-StarterProject.ps1</c> 要刪掉它們。
    /// 新增這類測試卻沒加進腳本的 <c>$starterMigrationTests</c>，新專案一建立就有測試失敗（0.9.171 的
    /// <c>AccountRoleUniqueIndexMigrationTests</c> 就漏了）。
    /// </summary>
    [Fact]
    public void StarterMigrationTests_ShouldBeRemovedByTheStarterScript()
    {
        var root = FindRepoRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "New-StarterProject.ps1"));
        var listLine = Regex.Match(script, @"\$starterMigrationTests\s*=\s*@\(([^)]*)\)");
        Assert.True(listLine.Success, "New-StarterProject.ps1 找不到 $starterMigrationTests 清單。");
        var listed = Regex.Matches(listLine.Groups[1].Value, "\"([^\"]+)\"").Select(x => x.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        var testsRoot = Path.Combine(root, "src", "MyProject", "MyProject.Tests");
        var migrationTests = Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => path.Split(Path.DirectorySeparatorChar).Any(x => x is "bin" or "obj") == false)
            .Where(path => Path.GetFileName(path) != nameof(ScaffoldScriptConventionTests) + ".cs")
            .Where(path => Regex.IsMatch(File.ReadAllText(path), "IMigrator|\"[0-9]{14}_[A-Za-z]"))
            .Select(Path.GetFileName)
            .ToList();

        // 衍生專案裡這些測試已被刪掉，清單比對自然通過；在腳手架本身才會真的比對到。
        var missing = migrationTests.Where(x => listed.Contains(x!) == false).ToList();
        Assert.True(missing.Count == 0, "這些測試依賴腳手架自己的 migration 歷史，請加進 New-StarterProject.ps1 的 $starterMigrationTests：" + string.Join("、", missing));
    }

    private static string FindRepoRoot() => SourceTree.RepoRoot;
}
