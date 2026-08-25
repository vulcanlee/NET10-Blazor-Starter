using System.Text.RegularExpressions;

namespace MyProject.Tests;

/// <summary>
/// 日誌撰寫慣例的守門測試。
///
/// 本專案的日誌是可觀測性的唯一來源，而且管理員能從 /logs 頁面**匯出原始檔案**，
/// 因此「不寫入敏感資料」與「訊息可被檢索」兩件事必須機械化保證 ——
/// 文件與 copilot-instructions 只是建議，唯有測試會擋下 PR。
///
/// 這些規則在導入當下就已全數綠燈，因此它是回歸防護而不是待辦清單。
/// </summary>
public sealed class LoggingConventionTests
{
    /// <summary>
    /// 擷取日誌呼叫的訊息樣板。可選的前綴用來吸收 LogError(ex, "...") 的例外參數。
    /// </summary>
    private static readonly Regex LogCall = new(
        @"Log(Trace|Debug|Information|Warning|Error|Critical)\s*\(\s*(?:[A-Za-z0-9_\.]+\s*,\s*)?""((?:[^""\\]|\\.)*)""",
        RegexOptions.Compiled);

    private static readonly Regex Placeholder = new(@"\{([^{}]+)\}", RegexOptions.Compiled);

    /// <summary>
    /// 絕不可作為日誌佔位名稱的字詞。比對不分大小寫的子字串。
    /// </summary>
    private static readonly string[] ForbiddenPlaceholderParts =
    [
        "password", "passwd", "pwd", "token", "secret", "salt",
        "captcha", "email", "mail", "phone", "mobile", "apikey",
        "signingkey", "clientsecret",
    ];

    /// <summary>
    /// 含敏感字詞、但實際上是旗標或計數而非機密值本身的佔位名稱。
    /// 逐一列舉而非放寬規則，讓每個例外都必須被有意識地加入。
    /// </summary>
    private static readonly string[] SafePlaceholderNames =
    [
        "NeedChangePassword",   // 布林旗標：是否需要變更密碼，不含密碼內容
    ];

    /// <summary>
    /// 絕不可作為日誌「引數」的屬性名稱。這些都是本專案真實存在的屬性，
    /// 因此精準度高：樣板可能寫 {Value}，但引數若是 user.Password 一樣是外洩。
    /// </summary>
    private static readonly Regex ForbiddenArgument = new(
        @"\.(Password|Salt|Email|SigningKey|ClientSecret|RefreshToken|AccessToken|CaptchaCode|TwoFactorSecret)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// CJK 範圍。日誌訊息一律英文，中文保留給 ApiResult.Message 與畫面通知，
    /// 這樣日後才好用關鍵字檢索。
    /// </summary>
    private static readonly Regex Cjk = new(@"[㐀-鿿豈-﫿＀-￯　-〿]", RegexOptions.Compiled);

    private static readonly Regex PascalCase = new(@"^[A-Z][A-Za-z0-9]*$", RegexOptions.Compiled);

    private static IEnumerable<(string File, string Level, string Template, string Line)> EnumerateLogCalls()
    {
        var root = FindSourceRoot();
        var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}")
                        && !path.Contains("MyProject.Tests"));

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match match in LogCall.Matches(text))
            {
                var lineNumber = text.Take(match.Index).Count(c => c == '\n') + 1;
                yield return (Path.GetFileName(file), match.Groups[1].Value, match.Groups[2].Value,
                    $"{Path.GetFileName(file)}:{lineNumber}");
            }
        }
    }

    [Fact]
    public void LogMessages_ShouldBeEnglish()
    {
        var violations = EnumerateLogCalls()
            .Where(call => Cjk.IsMatch(call.Template))
            .Select(call => $"{call.Line} -> {call.Template}")
            .ToList();

        Assert.True(violations.Count == 0,
            "日誌訊息一律英文（中文保留給 ApiResult.Message 與畫面通知）："
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void LogPlaceholders_ShouldBePascalCase()
    {
        var violations = new List<string>();
        foreach (var call in EnumerateLogCalls())
        {
            foreach (Match placeholder in Placeholder.Matches(call.Template))
            {
                var name = placeholder.Groups[1].Value;
                if (!PascalCase.IsMatch(name))
                {
                    violations.Add($"{call.Line} -> {{{name}}}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "日誌佔位名稱須為 PascalCase，方便日後檢索："
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void LogPlaceholders_ShouldNotNameSensitiveData()
    {
        var violations = new List<string>();
        foreach (var call in EnumerateLogCalls())
        {
            foreach (Match placeholder in Placeholder.Matches(call.Template))
            {
                var name = placeholder.Groups[1].Value;
                if (SafePlaceholderNames.Contains(name, StringComparer.Ordinal))
                {
                    continue;
                }

                var forbidden = ForbiddenPlaceholderParts
                    .FirstOrDefault(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));
                if (forbidden is not null)
                {
                    violations.Add($"{call.Line} -> {{{name}}}（含「{forbidden}」）");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "日誌不得寫入敏感資料或個資。可記錄的身分資訊只有 Account 與 UserId："
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void LogMessages_ShouldNotUseDestructuring()
    {
        // {@Model} 會把整個物件序列化進日誌，是最容易一次外洩全部欄位的寫法。
        var violations = EnumerateLogCalls()
            .Where(call => call.Template.Contains("{@", StringComparison.Ordinal))
            .Select(call => $"{call.Line} -> {call.Template}")
            .ToList();

        Assert.True(violations.Count == 0,
            "不得使用 {@} 解構整包物件，請逐一列出需要的欄位："
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void LogArguments_ShouldNotReferenceSensitiveProperties()
    {
        var root = FindSourceRoot();
        var violations = new List<string>();

        // 樣板可能寫成無害的 {Value}，但引數若是 user.Password 一樣會外洩，
        // 因此另外掃一次「呼叫括號內的引數運算式」。
        var callWithArgs = new Regex(
            @"Log(?:Trace|Debug|Information|Warning|Error|Critical)\s*\(([^;]{0,600}?)\);",
            RegexOptions.Compiled | RegexOptions.Singleline);

        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(p => (p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                     && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}")
                     && !p.Contains("MyProject.Tests")))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in callWithArgs.Matches(text))
            {
                var hit = ForbiddenArgument.Match(match.Groups[1].Value);
                if (hit.Success)
                {
                    var lineNumber = text.Take(match.Index).Count(c => c == '\n') + 1;
                    violations.Add($"{Path.GetFileName(file)}:{lineNumber} -> {hit.Value}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "日誌引數不得取用敏感屬性："
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// 會出問題的地方必須有 logger。下一個新增的服務或檢視才不會又忘記寫日誌。
    /// </summary>
    [Theory]
    [InlineData("MyProject.Business", "Services")]
    [InlineData("MyProject.Business", "Repositories")]
    [InlineData("MyProject.Web", "Controllers")]
    [InlineData("MyProject.Web", "Diagnostics")]
    public void BehaviourClasses_ShouldHoldALogger(string project, string folder)
    {
        var root = Path.Combine(FindSourceRoot(), project, folder);
        if (!Directory.Exists(root))
        {
            return;
        }

        var violations = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !ExemptFromLoggerRequirement(path))
            .Where(path => !File.ReadAllText(path).Contains("ILogger<", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(violations.Count == 0,
            $"{project}/{folder} 下的類別必須注入 ILogger（純資料/純函式類別請加入豁免清單）："
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// 豁免：介面、設定與模型等純宣告檔案，以及沒有行為的狀態持有者與純函式類別。
    /// 硬要它們注入 logger 只會產生永遠不會被寫出的雜訊。
    /// </summary>
    private static bool ExemptFromLoggerRequirement(string path)
    {
        var name = Path.GetFileName(path);

        if (name.StartsWith('I') && name.Length > 1 && char.IsUpper(name[1]))
        {
            return true;
        }

        string[] exempt =
        [
            "CurrentUserService.cs",        // 僅持有目前使用者狀態，無任何行為
            "RolePermissionService.cs",     // 純粹回傳靜態權限結構
            "AuthenticationCheckResult.cs", // 列舉
            "LogModels.cs",                 // 模型與純轉換
            "DatabaseUsageModels.cs",       // 模型
            "NLogFilePathResolver.cs",      // 純路徑組字串
            "TotpService.cs",               // 純密碼學運算；所有輸入輸出都是機密，加 logger 只會誘使人記錄它
        ];

        if (name.EndsWith("Extensions.cs", StringComparison.Ordinal))
        {
            // 靜態擴充方法類別，沒有可注入的建構式
            return true;
        }

        return exempt.Contains(name, StringComparer.Ordinal)
            || name.EndsWith("Settings.cs", StringComparison.Ordinal)
            || name.EndsWith("Dto.cs", StringComparison.Ordinal);
    }

    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "MyProject.Web");
            if (Directory.Exists(candidate))
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
