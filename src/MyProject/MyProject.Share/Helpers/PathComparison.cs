namespace MyProject.Share.Helpers;

/// <summary>
/// 比對檔案路徑時的大小寫規則（0.10.47 起）：與 .NET 自己判斷路徑是否分大小寫的規則相同——
/// Windows、macOS 的檔案系統預設不分大小寫，只差大小寫的就是同一個資料夾；Linux 等分大小寫，<c>files</c> 與 <c>Files</c> 是兩個資料夾。
/// 「路徑是否在根目錄底下」這類檢查一律用 <see cref="Current"/>：一律不分大小寫的話，在 Linux 上只差大小寫的同層資料夾會被當成在根目錄底下。
/// </summary>
public static class PathComparison
{
    /// <summary>目前作業系統的檔案路徑比對方式。</summary>
    public static StringComparison Current { get; } = For(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());

    /// <summary>檔案系統不分大小寫時用 <see cref="StringComparison.OrdinalIgnoreCase"/>，否則 <see cref="StringComparison.Ordinal"/>。</summary>
    public static StringComparison For(bool caseInsensitiveFileSystem)
        => caseInsensitiveFileSystem ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
