using System.Text;
using System.Text.Json;

namespace MyProject.Tests;

/// <summary>
/// appsettings.json 的註解守門（0.9.176 起，速查表 §6.32）。
///
/// 1. 每個屬性都要有行尾 <c>//</c> 註解，說明怎麼用、有什麼要注意（新增設定時一起寫）。
///    例外：只開一個物件的行（<c>"X": {</c>，說明寫在區段上方）、AI 計費第二個以後的模型（欄位與第一個相同，不重複說明）。
/// 2. 正式環境一定要處理的鍵要有標記：<see cref="ProductionMark"/>（用 appsettings.Production.json 覆蓋）或
///    <see cref="SecretMark"/>（機密，用環境變數提供）。<c>StartupSafetyValidator</c> 在 Production 會擋的鍵一定在清單裡。
/// 3. 標成機密的鍵，出貨值只能是空字串或開發用的佔位值 —— 真的機密不可以進版控。
/// 4. 註解裡不可寫出 <c>"SystemVersion":</c>：<c>scripts/New-StarterProject.ps1</c> 用 regex 取代版本號，註解寫了會被一起換掉。
/// </summary>
public sealed class AppSettingsCommentConventionTests
{
    public const string ProductionMark = "⚠️【正式必改】";
    public const string SecretMark = "🔒【機密】";

    /// <summary>正式環境要用 appsettings.Production.json 覆蓋的鍵（含 <c>StartupSafetyValidator</c> 會擋的非機密鍵）。</summary>
    private static readonly string[] Production =
    [
        "SystemSettings:ExternalFileSystem:DatabasePath",
        "SystemSettings:ExternalFileSystem:DownloadPath",
        "SystemSettings:ExternalFileSystem:UploadPath",
        "SystemSettings:ExternalFileSystem:ProjectFilePath",
        "SystemSettings:ExternalFileSystem:ExceptionPath",
        "SystemSettings:ExternalFileSystem:TokenUsagePath",
        "SystemSettings:ExternalFileSystem:AiCallLogPath",
        "SystemSettings:ExternalFileSystem:AiAgentConversationPath",
        "SystemSettings:ExternalFileSystem:AuditContentPath",
        "SystemSettings:ExternalFileSystem:DataProtectionKeyPath",
        "SystemSettings:ExternalFileSystem:BackupPath",
        "SystemSettings:ExternalFileSystem:AuditKeyPath",
        "NLog:BasePath",
        "AllowedHosts",
        "BootstrapSettings:SupportEmail",
        "Swagger:EnabledInProduction",
        "Security:ReturnExceptionDetails",
        "Cors:AllowedOrigins",
        "EmailSettings:Provider",
        "EmailSettings:Host",
        "EmailSettings:FromAddress",
        "EmailSettings:PublicBaseUrl",
        "AiSettings:Endpoint",
        "AiSettings:Model",
        "GoogleOAuthSettings:Enabled",
        "GoogleOAuthSettings:ClientId",
    ];

    /// <summary>機密：正式環境用環境變數提供（<c>區段__鍵</c>），不寫進任何 json 檔。</summary>
    private static readonly string[] Secrets =
    [
        "JwtSettings:SigningKey",
        "BootstrapSettings:SupportPassword",
        "AiSettings:ApiKey",
        "EmailSettings:UserName",
        "EmailSettings:Password",
        "GoogleOAuthSettings:ClientSecret",
        "CacheSettings:RedisConnection",
        "AutoMapper:LicenseKey",
    ];

    public static TheoryData<string> ProductionKeys => [.. Production];

    public static TheoryData<string> SecretKeys => [.. Secrets];

    /// <summary>機密鍵允許的出貨值：空字串，或一看就知道是開發用的佔位值。</summary>
    private static readonly string[] AllowedSecretPlaceholders = ["", "support", "DevelopmentOnly-ChangeThisJwtSigningKey-AtLeast32Chars"];

    [Fact]
    public void EveryProperty_ShouldHaveATrailingComment()
    {
        var missing = ReadLines()
            .Where(x => x.Key is not null && x.OpensObject == false && string.IsNullOrWhiteSpace(x.Comment) && x.IsRepeatedPricingModel == false)
            .Select(x => $"第 {x.Number} 行 {x.Path}")
            .ToList();

        Assert.True(missing.Count == 0, "appsettings.json 每個屬性都要有行尾 // 註解，說明怎麼用、要注意什麼：\n" + string.Join("\n", missing));
    }

    [Theory]
    [MemberData(nameof(ProductionKeys))]
    public void ProductionKeys_ShouldBeMarked(string path)
        => Assert.Contains(ProductionMark, Line(path).Comment ?? string.Empty);

    [Theory]
    [MemberData(nameof(SecretKeys))]
    public void SecretKeys_ShouldBeMarked_AndShipOnlyPlaceholders(string path)
    {
        var line = Line(path);
        Assert.Contains(SecretMark, line.Comment ?? string.Empty);
        Assert.Contains(path.Replace(":", "__", StringComparison.Ordinal), line.Comment ?? string.Empty);

        using var document = JsonDocument.Parse(File.ReadAllText(AppSettingsPath()), ShippedAppSettings.JsonOptions);
        var value = path == "AutoMapper:LicenseKey"
            ? document.RootElement.GetProperty(path).GetString()
            : path.Split(':').Aggregate(document.RootElement, (element, key) => element.GetProperty(key)).GetString();
        Assert.Contains(value, AllowedSecretPlaceholders);
    }

    /// <summary>標記只用在清單裡的鍵：清單和設定檔要一起維護，否則標記會失去「一看就知道」的意義。</summary>
    [Fact]
    public void Marks_ShouldOnlyAppearOnListedKeys()
    {
        var listed = Production.Concat(Secrets).ToHashSet(StringComparer.Ordinal);
        var stray = ReadLines()
            .Where(x => x.Key is not null && x.Comment is not null
                && (x.Comment.Contains(ProductionMark, StringComparison.Ordinal) || x.Comment.Contains(SecretMark, StringComparison.Ordinal))
                && listed.Contains(x.Path) == false)
            .Select(x => x.Path)
            .ToList();

        Assert.True(stray.Count == 0, "這些鍵有標記但不在清單裡，請一起更新 AppSettingsCommentConventionTests：" + string.Join("、", stray));
    }

    /// <summary>
    /// 「系統參數頁可改」要和 <c>SystemParameterCatalog.All</c> 完全一致：頁面上能改的鍵都要寫，寫了的鍵一定在頁面上。
    /// 這是註解最容易和程式脫節的地方（頁面加一個參數、忘了改註解）。
    /// </summary>
    [Fact]
    public void SystemParameterNote_ShouldMatchTheCatalog()
    {
        const string note = "系統參數頁可改";
        var catalog = MyProject.Web.Configuration.Parameters.SystemParameterCatalog.All.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var noted = ReadLines()
            .Where(x => x.Key is not null && (x.Comment ?? string.Empty).Contains(note, StringComparison.Ordinal))
            .Select(x => x.Path)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(catalog.Except(noted).Any() == false, $"這些鍵可在系統參數頁調整，註解要寫「{note}」：" + string.Join("、", catalog.Except(noted)));
        Assert.True(noted.Except(catalog).Any() == false, $"這些鍵不在系統參數頁，註解不可寫「{note}」：" + string.Join("、", noted.Except(catalog)));
    }

    /// <summary>
    /// 不可有結尾多餘的逗號：網站讀設定時允許，但 <c>scripts/Restore-Backup.ps1</c> 規定用 Windows PowerShell 5.1 執行，
    /// 它的 <c>ConvertFrom-Json</c> 不允許，還原時會讀不到路徑。
    /// </summary>
    [Fact]
    public void Document_ShouldHaveNoTrailingCommas()
    {
        var strict = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = false };
        using var document = JsonDocument.Parse(File.ReadAllText(AppSettingsPath()), strict);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    /// <summary>
    /// 排序（0.9.176 起）：含標記的區段全部排在沒有標記的區段前面（第一部分「正式環境要處理的設定」），
    /// 同一層裡有標記的屬性（或底下有標記的物件）排在沒有標記的前面。部署的人從上往下照做，看到第二部分就可以停。
    /// </summary>
    [Fact]
    public void MarkedSettings_ShouldComeFirst()
    {
        var lines = ReadLines().Where(x => x.Key is not null).ToList();
        bool IsMarked(ParsedLine line) => HasMark(line.Comment)
            || lines.Any(x => x.Path.StartsWith(line.Path + ":", StringComparison.Ordinal) && HasMark(x.Comment));

        var problems = new List<string>();
        foreach (var siblings in lines.GroupBy(x => ParentOf(x)))
        {
            var seenUnmarked = false;
            foreach (var line in siblings)
            {
                if (IsMarked(line) == false)
                {
                    seenUnmarked = true;
                }
                else if (seenUnmarked)
                {
                    problems.Add($"第 {line.Number} 行 {line.Path}");
                }
            }
        }

        Assert.True(problems.Count == 0, "有標記的設定要排在同一層沒有標記的前面：" + string.Join("、", problems));
    }

    private static bool HasMark(string? comment)
        => comment is not null && (comment.Contains(ProductionMark, StringComparison.Ordinal) || comment.Contains(SecretMark, StringComparison.Ordinal));

    /// <summary>同一層的分組鍵：最上層的成員（含鍵名本身帶冒號的 <c>AutoMapper:LicenseKey</c>）都歸在根層。</summary>
    private static string ParentOf(ParsedLine line)
        => line.Number > 0 && line.Path == line.Key ? string.Empty : line.Path[..^(line.Key!.Length + 1)];

    [Fact]
    public void Comments_ShouldNotContainTheVersionKey()
    {
        var offenders = ReadLines()
            .Where(x => (x.Comment ?? string.Empty).Contains("\"SystemVersion\"", StringComparison.Ordinal))
            .Select(x => $"第 {x.Number} 行")
            .ToList();

        Assert.True(offenders.Count == 0, "註解不可寫出 \"SystemVersion\"（New-StarterProject.ps1 會用 regex 取代它）：" + string.Join("、", offenders));
    }

    private static ParsedLine Line(string path)
    {
        var line = ReadLines().SingleOrDefault(x => x.Path == path);
        Assert.True(line is not null, $"appsettings.json 找不到 {path}。鍵若改名，請一併更新這條測試。");
        return line!;
    }

    private sealed record ParsedLine(int Number, string? Key, string Path, bool OpensObject, string? Comment, bool IsRepeatedPricingModel);

    /// <summary>
    /// 逐行讀 appsettings.json：找出屬性名稱、完整路徑（以 <c>:</c> 串接）與行尾註解。
    /// 只處理本專案的排版（一行一個屬性，或 <c>"X": { "Cron": "…" }</c> 這種單行物件），夠用就好。
    /// </summary>
    private static List<ParsedLine> ReadLines()
    {
        var result = new List<ParsedLine>();
        var stack = new List<string>();
        var pricingModelIndex = -1;
        var lines = File.ReadAllLines(AppSettingsPath());

        for (var i = 0; i < lines.Length; i++)
        {
            var (code, comment) = SplitComment(lines[i]);
            var trimmed = code.Trim();
            string? key = null;
            var opensObject = false;

            if (trimmed.StartsWith('"'))
            {
                var end = trimmed.IndexOf('"', 1);
                key = trimmed[1..end];
                var rest = trimmed[(end + 1)..].TrimStart().TrimStart(':').Trim().TrimEnd(',').Trim();
                opensObject = rest == "{";
            }

            var inPricingModels = stack.Count >= 2 && stack[^2] == "AiPricingSettings" && stack[^1] == "Models";
            if (key is not null && opensObject && inPricingModels)
            {
                pricingModelIndex++;
            }

            var underPricingModel = stack.Count >= 3 && stack[1] == "AiPricingSettings" && stack[2] == "Models";
            var repeated = (underPricingModel || inPricingModels) && pricingModelIndex > 0;
            if (key is not null)
            {
                var path = string.Join(':', stack.Skip(1).Append(key));
                result.Add(new ParsedLine(i + 1, key, path, opensObject, comment, repeated));
            }

            foreach (var ch in StripStrings(code))
            {
                if (ch == '{')
                {
                    stack.Add(opensObject && key is not null ? key : string.Empty);
                }
                else if (ch == '}')
                {
                    stack.RemoveAt(stack.Count - 1);
                }
            }
        }

        return result;
    }

    /// <summary>把一行拆成程式碼與 <c>//</c> 之後的註解；字串裡的 <c>//</c>（例如網址）不算。</summary>
    private static (string Code, string? Comment) SplitComment(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inString)
            {
                if (ch == '\\')
                {
                    i++;
                }
                else if (ch == '"')
                {
                    inString = false;
                }
            }
            else if (ch == '"')
            {
                inString = true;
            }
            else if (ch == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                return (line[..i], line[(i + 2)..].Trim());
            }
        }

        return (line, null);
    }

    /// <summary>去掉字串內容，只留結構字元，用來數大括號。</summary>
    private static string StripStrings(string code)
    {
        var builder = new StringBuilder();
        var inString = false;
        for (var i = 0; i < code.Length; i++)
        {
            var ch = code[i];
            if (inString)
            {
                if (ch == '\\')
                {
                    i++;
                }
                else if (ch == '"')
                {
                    inString = false;
                }
            }
            else if (ch == '"')
            {
                inString = true;
            }
            else
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static string AppSettingsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "MyProject", "MyProject.Web", "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("找不到 src/MyProject/MyProject.Web/appsettings.json。");
    }
}
