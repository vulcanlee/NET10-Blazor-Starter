using System.Text.RegularExpressions;

namespace MyProject.Tests.Client;

/// <summary>
/// 前端不可寫死伺服器端點的網址（0.10.10 起，WASM 遷移 M11）：雙站台部署時前端與 API 在不同網址，
/// 「整頁導到伺服器」或交給瀏覽器的連結若沒經過 <c>ApiAddress</c>（<c>wwwroot/appsettings.json</c> 的 <c>ApiBaseUrl</c>），
/// 會導到前端的靜態站、只拿到 index.html，而且在同站台的開發機上完全看不出來。
/// API 呼叫本身經 HttpClient（BaseAddress 就是 ApiBaseUrl），不在這裡的檢查範圍。
/// </summary>
public sealed class ServerUrlConventionTests
{
    /// <summary>只存在於 API 主機、不是前端頁面的路徑前綴。</summary>
    private static readonly string[] ServerOnlyPrefixes =
    [
        "/api/", "api/", "/Auths/Google", "Auths/Google", "/signin-google", "/swagger", "/health",
    ];

    private static readonly Regex NavigateToLiteral = new("NavigateTo\\(\\s*\\$?\"(?<url>[^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex HrefLiteral = new("href=\"(?<url>[^\"@]*)\"", RegexOptions.Compiled);
    private static readonly Regex JsNavigation = new("(location\\.(href|assign|replace)|\\.href)\\s*(=|\\()\\s*['\"`](?<url>[^'\"`]*)['\"`]", RegexOptions.Compiled);

    [Fact]
    public void GoogleLoginPath_ShouldOnlyBeComposedByApiAddress()
    {
        var offenders = ClientFiles()
            .Where(path => File.ReadAllText(path).Contains("Auths/Google", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(SourceTree.Client, path))
            .ToList();

        Assert.Equal([Path.Combine("Api", "ApiAddress.cs")], offenders);
    }

    [Fact]
    public void Navigations_ShouldNotHardCodeServerEndpoints()
    {
        var files = ClientFiles();

        // 防空跑：掃描路徑失效時不能變成永遠通過。
        Assert.True(files.Count > 150, $"只掃到 {files.Count} 個檔案，掃描路徑可能失效。");

        var violations = files
            .SelectMany(path =>
            {
                var text = File.ReadAllText(path);
                var patterns = Path.GetExtension(path) switch
                {
                    ".js" => new[] { JsNavigation },
                    ".razor" => [NavigateToLiteral, HrefLiteral],
                    _ => [NavigateToLiteral],
                };

                return patterns
                    .SelectMany(pattern => pattern.Matches(text).Select(match => match.Groups["url"].Value))
                    .Where(url => ServerOnlyPrefixes.Any(prefix => url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    .Select(url => $"{Path.GetRelativePath(SourceTree.Client, path)}：{url}");
            })
            .ToList();

        Assert.True(violations.Count == 0,
            "整頁導到伺服器端點的網址要經過 ApiAddress（雙站台時 API 在另一個網址）："
                + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// 附件的上傳與下載由瀏覽器直接送出（不經過 HttpClient）：網址一律在 <c>BrowserFiles</c> 裡經 <c>ApiAddress</c> 換成完整網址，
    /// 呼叫端照舊傳伺服器給的路徑即可（見 ApiAddressTests.BrowserFiles_ShouldHandAbsoluteApiUrlsToTheBrowser）。
    /// </summary>
    [Fact]
    public void BrowserFiles_ShouldResolveEveryUrlItSendsToTheBrowser()
    {
        var source = File.ReadAllText(Path.Combine(SourceTree.Client, "Commons", "BrowserFiles.cs"));

        var calls = Regex.Matches(source, "Invoke(?:Void)?Async(?:<[^>]+>)?\\(\"(?<name>\\w+)\"[^;]*")
            .Select(match => (Name: match.Groups["name"].Value, Text: match.Value))
            .Where(call => call.Name is "download" or "send")
            .ToList();

        Assert.Equal(2, calls.Count);
        Assert.All(calls, call => Assert.Contains("apiAddress.Absolute(", call.Text, StringComparison.Ordinal));
    }

    private static List<string> ClientFiles()
        => SourceTree.UiFiles("*.cs")
            .Concat(SourceTree.UiFiles("*.razor"))
            .Concat(Directory.EnumerateFiles(Path.Combine(SourceTree.ClientWebRoot, "js"), "*.js"))
            .ToList();
}
