using System.Globalization;

namespace MyProject.Business.Services.Other;

/// <summary>
/// 「設定的根目錄底下、以 <c>{yyyyMM}/…</c> 存放」的檔案操作（0.10.44 起，整併自七個檔案存放類別裡重複的寫法）。
///
/// <para>
/// 所有路徑都先經 <see cref="TryResolve"/>：相對路徑解析後<b>必須在根目錄底下</b>，<c>../</c>、絕對路徑或根目錄沒設定一律當作不合法。
/// 路徑來自資料庫，不檢查的話被改過的值能讀刪根目錄以外的檔案（0.10.43 以前 AI 對話紀錄、Token 用量原始檔、例外堆疊三個類別沒有檢查）。
/// </para>
/// <para>
/// ⚠️ 這裡<b>不接例外、不寫日誌</b>：失敗一律往外丟，由各存放類別自己接住、用自己的訊息記錄（例外堆疊位在例外記錄管線內，
/// 不得用 ILogger，所以只能由它自己決定怎麼處理）。各類別「絕不拋出」與「日誌只記路徑、不記內容」的約定也留在各類別。
/// </para>
/// </summary>
public sealed class RootedFiles
{
    private readonly Func<string> rootPath;

    /// <param name="rootPath">每次使用時才讀，設定在執行期間被改掉也跟得上（同原本各類別的 <c>RootPath</c> 屬性）。</param>
    public RootedFiles(Func<string> rootPath)
    {
        this.rootPath = rootPath;
    }

    public string RootPath => rootPath();

    /// <summary>根目錄有設定。</summary>
    public bool HasRoot => string.IsNullOrWhiteSpace(RootPath) == false;

    /// <summary>
    /// 相對路徑 → 完整路徑；結果不在根目錄底下（<c>../</c>、絕對路徑）或根目錄未設定時回 null。
    /// 根目錄未設定一律拒絕：<c>Path.Combine("", x)</c> 會退化成相對於工作目錄的路徑。
    /// </summary>
    public string? TryResolve(string? relativePath)
    {
        if (HasRoot == false || string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootPath));

        // 磁碟或檔案系統的根（D:\、/）去不掉結尾的分隔符號，不能再加一個，否則所有路徑都會被當成越界。
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, normalized));
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
    }

    /// <summary>寫入文字（UTF-8 無 BOM，同 <see cref="File.WriteAllTextAsync(string, string?, CancellationToken)"/>），需要時建立目錄。</summary>
    /// <returns>路徑不合法時回 false（沒有寫）；寫入失敗丟出例外。</returns>
    public async Task<bool> WriteTextAsync(string relativePath, string content)
    {
        var fullPath = TryResolve(relativePath);
        if (fullPath is null)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, content);
        return true;
    }

    /// <summary>寫入位元組，需要時建立目錄。</summary>
    /// <returns>路徑不合法時回 false（沒有寫）；寫入失敗丟出例外。</returns>
    public async Task<bool> WriteBytesAsync(string relativePath, byte[] bytes)
    {
        var fullPath = TryResolve(relativePath);
        if (fullPath is null)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, bytes);
        return true;
    }

    /// <summary>讀取文字。路徑不合法或檔案不存在回 null；讀取失敗丟出例外。</summary>
    public async Task<string?> ReadTextAsync(string? relativePath)
    {
        var fullPath = TryResolve(relativePath);
        return fullPath is null || File.Exists(fullPath) == false ? null : await File.ReadAllTextAsync(fullPath);
    }

    /// <summary>讀取位元組。路徑不合法或檔案不存在回 null；讀取失敗丟出例外。</summary>
    public async Task<byte[]?> ReadBytesAsync(string? relativePath)
    {
        var fullPath = TryResolve(relativePath);
        return fullPath is null || File.Exists(fullPath) == false ? null : await File.ReadAllBytesAsync(fullPath);
    }

    /// <summary>刪除單一檔案。路徑不合法或檔案不存在什麼都不做；刪除失敗丟出例外。</summary>
    public void Delete(string? relativePath)
    {
        var fullPath = TryResolve(relativePath);
        if (fullPath is not null && File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
    }

    /// <summary>
    /// 清空根目錄（「清空全部」用）。刻意刪內容而非刪根目錄本身：目錄由 Program.cs 啟動時建立，刪掉就不會再有人補。
    /// 根目錄沒設定或不存在時什麼都不做；刪除失敗丟出例外。
    /// </summary>
    public void DeleteAll()
    {
        if (HasRoot == false || Directory.Exists(RootPath) == false)
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(RootPath))
        {
            Directory.Delete(directory, recursive: true);
        }

        foreach (var file in Directory.EnumerateFiles(RootPath))
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// 刪除早於 <paramref name="threshold"/> 所在月份的整個 <c>{yyyyMM}</c> 目錄（自動過期的收尾：逐筆刪檔後剩下的空目錄，
    /// 以及「檔已寫、資料列沒建起來」留下的孤兒檔）。門檻所在月份本身不動；名稱不是月份的目錄不動。刪除失敗丟出例外。
    /// </summary>
    public void DeleteMonthsBefore(DateTime threshold)
    {
        if (HasRoot == false || Directory.Exists(RootPath) == false)
        {
            return;
        }

        var boundary = new DateTime(threshold.Year, threshold.Month, 1);
        foreach (var directory in Directory.EnumerateDirectories(RootPath))
        {
            if (TryParseMonth(directory, out var month) && month < boundary)
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>刪掉空的 <c>{yyyyMM}</c> 目錄（月份不代表過期的資料用，例如 Agent 對話）。刪除失敗丟出例外。</summary>
    public void DeleteEmptyMonthFolders()
    {
        if (HasRoot == false || Directory.Exists(RootPath) == false)
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(RootPath))
        {
            if (TryParseMonth(directory, out _) && Directory.EnumerateFileSystemEntries(directory).Any() == false)
            {
                Directory.Delete(directory);
            }
        }
    }

    private static bool TryParseMonth(string directory, out DateTime month)
        => DateTime.TryParseExact(Path.GetFileName(directory), "yyyyMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out month);
}
