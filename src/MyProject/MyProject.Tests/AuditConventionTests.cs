using System.Reflection;
using System.Text.RegularExpressions;
using MyProject.Business.Helpers;

namespace MyProject.Tests;

/// <summary>
/// 稽核動作代碼的守門測試（LOG-14）。
///
/// 0.9.78 之前約 30 個代碼散在各呼叫點的字串字面值裡，打錯字只會在稽核頁多出一個孤兒代碼。
/// 現在代碼只能定義在 <see cref="AuditActions"/>，呼叫點一律引用常數。
/// </summary>
public sealed class AuditConventionTests
{
    /// <summary>
    /// 寫稽核的呼叫：IAuditLogService.WriteAsync，以及第一個參數是動作代碼的包裝方法
    /// （WriteAuditAsync／WriteSelfAuditAsync／WriteAiAuditAsync）。WriteExportAuditAsync 的參數是匯出格式，不在此列。
    /// </summary>
    private static readonly Regex AuditCallWithLiteral = new(
        @"(?:[Aa]uditLogService\.WriteAsync|\bWrite(?:Self|Ai)?AuditAsync)\(\s*""",
        RegexOptions.Compiled);

    [Fact]
    public void AuditCalls_ShouldNotPassStringLiteralActions()
    {
        var violations = EnumerateSourceFiles()
            .SelectMany(file => FindMatches(file, AuditCallWithLiteral))
            .ToList();

        Assert.True(violations.Count == 0,
            "稽核動作代碼必須引用 AuditActions 的常數，不可寫字串字面值："
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void KnownActionCodes_ShouldOnlyBeSpelledOutInAuditActions()
    {
        // 連三元運算式（round == 0 ? "A" : "B"）或區域變數這類繞過上一條規則的寫法也一併擋下。
        // 只檢查帶「.」的代碼：單字代碼（Logout）同時也是稽核頁的分類名稱，會出現在標籤顏色對照裡。
        var codes = AllActionCodes().Where(code => code.Contains('.')).ToList();
        var violations = new List<string>();

        foreach (var file in EnumerateSourceFiles())
        {
            if (Path.GetFileName(file) == "AuditActions.cs")
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (var code in codes)
            {
                var index = text.IndexOf($"\"{code}\"", StringComparison.Ordinal);
                if (index >= 0)
                {
                    var line = text.Take(index).Count(c => c == '\n') + 1;
                    violations.Add($"{Path.GetFileName(file)}:{line} -> \"{code}\"");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "稽核動作代碼只能在 AuditActions 定義，其他地方請引用常數："
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void ActionCodes_ShouldBeUniqueAndWellFormed()
    {
        var codes = AllActionCodes();

        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, code => Assert.Matches(@"^[A-Z][A-Za-z]*(\.[A-Z][A-Za-z]*)*$", code));
    }

    /// <summary>
    /// 瀏覽類（0.9.173 起）：換頁、搜尋、開單筆來看，不改變任何資料。資料頁的「異動歷程」排除它們。
    /// 規則：只有常數名稱叫 <c>Search</c>／<c>View</c> 的才是瀏覽類。改了資料的動作（例如 <c>ReviewView</c> 這種名字）
    /// 值若剛好以 <c>.View</c> 結尾，會被異動歷程漏掉，這條就會轉紅。
    /// 以常數名稱比對而不是明列清單：CRUD 產生器每個新模組都會加 <c>Search</c>／<c>View</c>，明列會讓產生的模組必紅。
    /// </summary>
    [Fact]
    public void IsBrowsing_ShouldBeTrueExactlyForSearchAndViewConstants()
    {
        var violations = AllActionFields()
            .Where(field => AuditActions.IsBrowsing(field.Value) != (field.Name is "Search" or "View"))
            .Select(field => $"{field.Name} = {field.Value}")
            .ToList();

        Assert.True(violations.Count == 0,
            "瀏覽類動作的常數必須叫 Search／View，其他動作的值不可以 .Search／.View 結尾："
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
        Assert.True(AuditActions.IsBrowsing(AuditActions.Page.View));
        Assert.False(AuditActions.IsBrowsing(null));
    }

    /// <summary>
    /// 帶「修改前後差異」的動作（0.9.174 起，受「記錄修改前後差異」開關管）：常數名稱叫 <c>Update</c> 的，加上本人改姓名的
    /// <c>User.ProfileUpdate</c>。CRUD 產生器的新模組也是 <c>Update</c>，自動適用。其他動作帶的內容（例如匯出了哪些 Id）不是差異、不受開關影響。
    /// </summary>
    [Fact]
    public void CarriesFieldDiff_ShouldBeTrueExactlyForUpdateConstants()
    {
        var violations = AllActionFields()
            .Where(field => AuditActions.CarriesFieldDiff(field.Value)
                != (field.Name == "Update" || field.Value == AuditActions.User.ProfileUpdate))
            .Select(field => $"{field.Name} = {field.Value}")
            .ToList();

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
        Assert.False(AuditActions.CarriesFieldDiff(AuditActions.AiCallLog.Export));
        Assert.False(AuditActions.CarriesFieldDiff(null));
    }

    private static IEnumerable<(string Name, string Value)> AllActionFields()
        => typeof(AuditActions)
            .GetNestedTypes()
            .Append(typeof(AuditActions))
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (field.Name, (string)field.GetRawConstantValue()!));

    private static List<string> AllActionCodes()
        => typeof(AuditActions)
            .GetNestedTypes()
            .Append(typeof(AuditActions))
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

    private static IEnumerable<string> FindMatches(string file, Regex regex)
    {
        var text = File.ReadAllText(file);
        foreach (Match match in regex.Matches(text))
        {
            var line = text.Take(match.Index).Count(c => c == '\n') + 1;
            yield return $"{Path.GetFileName(file)}:{line}";
        }
    }

    private static IEnumerable<string> EnumerateSourceFiles()
    {
        var root = FindSourceRoot();
        return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}")
                        && !path.Contains("MyProject.Tests"));
    }

    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "MyProject.Web")))
            {
                return dir.FullName;
            }

            var srcCandidate = Path.Combine(dir.FullName, "src", "MyProject");
            if (Directory.Exists(Path.Combine(srcCandidate, "MyProject.Web")))
            {
                return srcCandidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("找不到 src/MyProject。");
    }
}
