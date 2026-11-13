namespace MyProject.Tests;

/// <summary>
/// 測試讀取原始碼、設定檔、文件與腳本時唯一的路徑來源（0.10.0 起）。
/// 從測試執行目錄往上找到含 <c>src/MyProject/MyProject.slnx</c> 的 repo 根目錄，其餘路徑都由它推導。
/// ⚠️ 不要在個別測試檔自己往上找目錄（<see cref="SourceTreeConventionTests"/> 守門）：
/// WASM 遷移會把畫面搬到 <see cref="Client"/>，路徑的變動只該改這一處。
/// </summary>
internal static class SourceTree
{
    private static readonly Lazy<string> repoRoot = new(FindRepoRoot);

    /// <summary>repo 根目錄（含 docs、scripts、src）。</summary>
    public static string RepoRoot => repoRoot.Value;

    /// <summary><c>src/MyProject</c>，方案檔與各專案所在目錄。</summary>
    public static string SourceRoot => Path.Combine(RepoRoot, "src", "MyProject");

    /// <summary>伺服器主機專案 <c>MyProject.Web</c>。</summary>
    public static string Web => Path.Combine(SourceRoot, "MyProject.Web");

    /// <summary>WASM 前端專案 <c>MyProject.Web.Client</c>。</summary>
    public static string Client => Path.Combine(SourceRoot, "MyProject.Web.Client");

    /// <summary>
    /// 前端的靜態檔根目錄 <c>MyProject.Web.Client/wwwroot</c>（0.10.10 起，WASM 遷移 M11）：<c>index.html</c>、<c>theme.css</c>、<c>app.css</c>、
    /// <c>js/</c>、<c>images/</c>、<c>lib/</c>、<c>appsettings.json</c>。0.10.9 以前這些在 <c>MyProject.Web/wwwroot</c>，主機頁是 <c>MyProject.Web/Components/App.razor</c>。
    /// </summary>
    public static string ClientWebRoot => Path.Combine(Client, "wwwroot");

    /// <summary>
    /// 所有畫面程式的根目錄：0.10.9 起（WASM 遷移 M10 移除 Server 模式）只有 WASM 的 <c>MyProject.Web.Client</c>。
    /// 「每個頁面都要…」這類規則掃這裡（0.10.1～0.10.8 遷移期間另含 Server 版的 <c>MyProject.Web/Components</c>）。
    /// </summary>
    public static IReadOnlyList<string> UiRoots => [Client];

    /// <summary>
    /// 畫面檔案（例如 <c>Views/Categories/CategoryViewView.razor</c>）在 <see cref="UiRoots"/> 哪一處就回哪一處的完整路徑（0.10.2 起）。
    /// 都沒有時丟例外。
    /// </summary>
    public static string UiFile(string relativePath)
        => UiRoots.Select(root => Path.Combine(root, relativePath)).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"在 {string.Join("、", UiRoots)} 底下都找不到 {relativePath}。");

    /// <summary>
    /// <see cref="UiRoots"/> 底下符合 <paramref name="searchPattern"/>（例如 <c>*.razor</c>）的所有檔案，不含 <c>bin</c>、<c>obj</c>（0.10.9 起）。
    /// ⚠️ 呼叫端仍要斷言結果不是空的：掃描路徑失效時守門測試不能變成永遠通過。
    /// </summary>
    public static IReadOnlyList<string> UiFiles(string searchPattern)
        => UiRoots
            .SelectMany(root => Directory.EnumerateFiles(root, searchPattern, SearchOption.AllDirectories)
                .Where(path => !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Any(x => x is "bin" or "obj")))
            .ToList();

    /// <summary>repo 的 <c>docs</c> 目錄。</summary>
    public static string Docs => Path.Combine(RepoRoot, "docs");

    /// <summary>repo 的 <c>scripts</c> 目錄。</summary>
    public static string Scripts => Path.Combine(RepoRoot, "scripts");

    /// <summary><c>MyProject.Web</c> 底下的相對路徑。</summary>
    public static string WebFile(string relativePath) => Path.Combine(Web, relativePath);

    /// <summary>
    /// 依序在 repo 根目錄與 <c>src/MyProject</c> 底下找相對路徑（檔案或目錄皆可），
    /// 讓 <c>docs</c> 與 <c>MyProject.Web</c> 這類名稱都能直接解析。
    /// </summary>
    public static string Resolve(string relativePath)
    {
        foreach (var root in new[] { RepoRoot, SourceRoot })
        {
            var candidate = Path.Combine(root, relativePath);
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"在 repo 根目錄與 src/MyProject 底下都找不到 {relativePath}。");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "MyProject", "MyProject.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"從 {AppContext.BaseDirectory} 往上找不到含 src/MyProject/MyProject.slnx 的 repo 根目錄。");
    }
}
