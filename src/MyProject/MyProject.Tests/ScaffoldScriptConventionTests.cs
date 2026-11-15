using System.Text.RegularExpressions;
using MyProject.Models.Systems;
using MyProject.Web.Configuration.Parameters;

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
        var textExtensions = ScriptTextExtensions();
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

    /// <summary>
    /// 複刻前先確認 dotnet-ef 的主版號與專案的 EF Core 相同（0.10.14 起，升級 .NET 11 時加上）：
    /// 舊版工具讀不到新框架的專案，會在複製完、建立 Init migration 時才失敗，留下半成品。
    /// </summary>
    [Fact]
    public void StarterScript_ShouldCheckTheDotnetEfMajorVersionAgainstEfCore()
    {
        var script = File.ReadAllText(Path.Combine(FindRepoRoot(), "scripts", "New-StarterProject.ps1"));

        Assert.Contains("dotnet ef --version", script, StringComparison.Ordinal);
        Assert.Contains(@"Microsoft\.EntityFrameworkCore\.Design", script, StringComparison.Ordinal);
        Assert.Contains("dotnet tool update --global dotnet-ef --version", script, StringComparison.Ordinal);
        Assert.Contains("-lt [int]$efMajor", script, StringComparison.Ordinal); // 只擋比專案舊的工具；新的可以用
    }

    /// <summary>
    /// EF Core 11 的 <c>.config/dotnet-ef.json</c>（0.10.14 起）：在 repo 任何子目錄下打 <c>dotnet ef …</c> 都不必再帶 <c>--project</c>／<c>--startup-project</c>。
    /// 路徑相對於 <c>.config</c> 的上一層（repo 根目錄）；startup 必須是 API 主機（不可以是 WASM 前端），DbContext 名稱要存在。
    /// </summary>
    [Fact]
    public void DotnetEfConfig_ShouldPointAtTheDataProjectAndTheApiHost()
    {
        var root = FindRepoRoot();
        using var config = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".config", "dotnet-ef.json")));
        var project = config.RootElement.GetProperty("project").GetString()!;
        var startup = config.RootElement.GetProperty("startupProject").GetString()!;
        var context = config.RootElement.GetProperty("context").GetString()!;

        Assert.True(Directory.Exists(Path.Combine(root, project)), project);
        Assert.EndsWith(".AccessDatas", project, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(root, startup)), startup);
        Assert.EndsWith(".Web", startup, StringComparison.Ordinal);
        Assert.Contains($"class {context} ", File.ReadAllText(Path.Combine(root, project, context + ".cs")), StringComparison.Ordinal);
    }

    /// <summary>
    /// 腳本只改寫 <c>$textExtensions</c> 裡的副檔名（0.10.24 起）。含專案代號的檔案，副檔名不在清單裡就不會被換掉：
    /// 0.10.23 以前 <c>scripts/e2e/*.mjs</c> 沒換，新專案的實機腳本仍讀 <c>myproject.auth</c>、預設連 <c>localhost:7144</c>，一跑就失敗。
    /// 掃的是磁碟上的檔案：跳過測試與日誌產物（<c>TestResults</c>、<c>.trx</c>、<c>.log</c>），它們不是範本的一部分。
    /// </summary>
    [Fact]
    public void EveryFileWithTheCodeName_ShouldHaveAnExtensionTheScriptRewrites()
    {
        var root = FindRepoRoot();
        string[] markers = ["MyProject", "myproject"];
        var extensions = ScriptTextExtensions();
        string[] skippedDirectories = [".git", "bin", "obj", ".vs", ".playwright-cli", "output", "artifacts", ".gstack", "PublishProfiles", "node_modules", "TestResults", "worktrees"];

        var offenders = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => path.Split(Path.DirectorySeparatorChar).Any(x => skippedDirectories.Contains(x)) == false)
            .Where(path => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) == false)
            .Where(path => Path.GetExtension(path) is not (".user" or ".suo" or ".trx" or ".log")) // 腳本不複製的個人檔（$excludedFilePatterns）與本機產物
            .Where(path => new FileInfo(path).Length < 5 * 1024 * 1024)
            .Where(path => File.ReadAllBytes(path).Contains((byte)0) == false) // 圖片、字型等二進位檔
            .Where(path => markers.Any(marker => File.ReadAllText(path).Contains(marker, StringComparison.Ordinal)))
            .Select(path => Path.GetRelativePath(root, path))
            .ToList();

        Assert.True(offenders.Count == 0, "這些檔案含專案代號，但副檔名不在 New-StarterProject.ps1 的 $textExtensions，複刻時不會被換掉：" + string.Join("、", offenders));
    }

    /// <summary>
    /// 系統名稱只在固定位置換（0.10.24 起，<c>$systemNameTargets</c>）：衍生專案的名稱可能剛好是代號或常見詞，全檔取代會在下一代改壞命名空間與文件。
    /// 每個必換的位置都要存在、含出貨的系統名稱、副檔名在 <c>$textExtensions</c>（否則根本不會被讀到）；改了檔名或搬了位置要一起改清單。
    /// </summary>
    [Fact]
    public void SystemNameTargets_ShouldExist_AndContainTheShippedName()
    {
        var root = FindRepoRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "New-StarterProject.ps1"));
        var extensions = ScriptTextExtensions();
        var (systemName, _) = ShippedSystemInformation();
        var targets = Regex.Matches(script, @"@\{ Path = '(?<path>[^']+)'; Kind = '(?<kind>\w+)'; Required = \$(?<required>true|false) \}")
            .Select(x => (Path: string.Format(System.Globalization.CultureInfo.InvariantCulture, x.Groups["path"].Value, "MyProject"), Kind: x.Groups["kind"].Value, Required: x.Groups["required"].Value == "true"))
            .ToList();

        Assert.Equal(["AppSettings", "Manifest", "LoadingCard", "LoadingScreen", "Help"], targets.Select(x => x.Kind));
        foreach (var target in targets.Where(x => x.Required))
        {
            var path = Path.Combine(root, target.Path);
            Assert.True(File.Exists(path), $"{target.Kind} 的檔案不存在：{target.Path}");
            Assert.Contains(Path.GetExtension(path), extensions);
            Assert.Contains(systemName, File.ReadAllText(path), StringComparison.Ordinal);
        }

        Assert.True(Directory.Exists(Path.Combine(root, targets.Single(x => x.Kind == "Help").Path)));
    }

    /// <summary>
    /// 畫面上的系統名稱由腳本一次換掉（0.10.24 起）：<c>-SystemName</c> 必填（新專案不可帶著腳手架的名稱出生），<c>-SystemDescription</c> 選填；
    /// 長度上限與「系統參數」頁相同，並擋掉會弄壞 JSON、HTML 與 C# 字串的字元。來源的名稱與簡介從來源的 appsettings.json 讀（再往下衍生一代時才換得到）。
    /// </summary>
    [Fact]
    public void StarterScript_ShouldRequireASystemName_WithTheSameLimitsAsTheParameterPage()
    {
        var script = File.ReadAllText(Path.Combine(FindRepoRoot(), "scripts", "New-StarterProject.ps1"));
        var nameMax = SystemParameterCatalog.All.Single(x => x.PropertyPath == "SystemInformation:SystemName").MaxLength;
        var descriptionMax = SystemParameterCatalog.All.Single(x => x.PropertyPath == "SystemInformation:SystemDescription").MaxLength;

        Assert.Matches(@"\[Parameter\(Mandatory = \$true\)\]\s*\[ValidateLength\(1, " + nameMax + @"\)\]\s*\[ValidatePattern\([^\n]+\)\]\s*\[string\]\$SystemName", script);
        Assert.Matches(@"\[ValidateLength\(1, " + descriptionMax + @"\)\]\s*\[ValidatePattern\([^\n]+\)\]\s*\[string\]\$SystemDescription", script);

        // 來源的名稱與簡介從來源的 appsettings.json 讀（不寫死：衍生專案再往下衍生時，來源已經不是腳手架的名稱）。
        Assert.Contains("""$sourceSystemName = [regex]::Match($sourceAppSettingsContent, '"SystemName"\s*:\s*"(?<value>[^"]*)"')""", script, StringComparison.Ordinal);
        Assert.Contains("""$sourceSystemDescription = [regex]::Match($sourceAppSettingsContent, '"SystemDescription"\s*:\s*"(?<value>[^"]*)"')""", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// 名稱的驗證樣式：擋掉 <c>"</c>、<c>\</c>、<c>&lt;</c>、<c>&gt;</c>、<c>&amp;</c>、<c>|</c>（說明的表格）、控制／格式／私用區字元（佔位字串用私用區）、
    /// 行與段落分隔字元（C# 字串會編譯錯誤）與頭尾空白，其他（中文、全形標點、空格）都可以。以腳本裡實際的樣式驗證，避免只看到有 ValidatePattern 卻擋錯。
    /// </summary>
    [Theory]
    [InlineData("艾克米企業系統", true)]
    [InlineData("Acme ERP 管理平台", true)]
    [InlineData("艾克米公司內部專案與人員管理系統。", true)]
    [InlineData("A\"B", false)]
    [InlineData(@"A\B", false)]
    [InlineData("A<B>", false)]
    [InlineData("A&B", false)]
    [InlineData("A|B", false)]
    [InlineData(" 前面有空白", false)]
    [InlineData("後面有空白 ", false)]
    [InlineData("換\n行", false)]
    [InlineData("換\u2028行", false)]
    [InlineData("私\uE000用", false)]
    public void StarterScript_SystemNamePattern_ShouldRejectCharactersThatBreakTheReplacedFiles(string value, bool accepted)
    {
        var script = File.ReadAllText(Path.Combine(FindRepoRoot(), "scripts", "New-StarterProject.ps1"));
        var pattern = Regex.Match(script, @"\[ValidatePattern\('(?<p>[^']+)'\)\]\s*\[string\]\$SystemName").Groups["p"].Value;
        Assert.False(string.IsNullOrEmpty(pattern), "找不到 $SystemName 的 ValidatePattern。");

        // PowerShell 的 ValidatePattern 不分大小寫；這裡的樣式沒有英文字母，大小寫不影響。
        Assert.Equal(accepted, Regex.IsMatch(value, pattern, RegexOptions.IgnoreCase));
    }

    /// <summary>
    /// 新代號含舊代號時（例如從 <c>Acme.Sn</c> 衍生 <c>Acme.Sn2</c>），殘留掃描要先把新代號從該行拿掉再判斷（0.10.24 起）：
    /// 0.10.23 以前每個換好的地方都被當成殘留，實測印出 5,920 條假警告，真正沒換到的東西就被淹沒了。
    /// </summary>
    [Fact]
    public void StarterScript_ResidualScan_ShouldIgnoreTheNewCodeNameThatContainsTheOldOne()
    {
        var script = File.ReadAllText(Path.Combine(FindRepoRoot(), "scripts", "New-StarterProject.ps1"));

        Assert.Matches(@"\$newValues\s*=\s*@\(\$ProjectName, \$ProjectName\.ToLowerInvariant\(\)\)", script);
        Assert.Contains("$rest = $rest.Replace($value, '')", script, StringComparison.Ordinal);
    }

    private static List<string> ScriptTextExtensions()
    {
        var script = File.ReadAllText(Path.Combine(FindRepoRoot(), "scripts", "New-StarterProject.ps1"));
        var list = Regex.Match(script, @"\$textExtensions\s*=\s*@\(([^)]*)\)");
        Assert.True(list.Success, "New-StarterProject.ps1 找不到 $textExtensions 清單。");
        return Regex.Matches(list.Groups[1].Value, "\"([^\"]+)\"").Select(x => x.Groups[1].Value).ToList();
    }

    private static (string Name, string Description) ShippedSystemInformation()
    {
        using var settings = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SourceTree.WebFile("appsettings.json")), ShippedAppSettings.JsonOptions);
        var information = settings.RootElement.GetProperty("SystemSettings").GetProperty("SystemInformation");
        return (information.GetProperty("SystemName").GetString()!, information.GetProperty("SystemDescription").GetString()!);
    }

    private static string FindRepoRoot() => SourceTree.RepoRoot;
}
