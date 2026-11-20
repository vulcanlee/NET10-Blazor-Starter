using System.Runtime.InteropServices;
using System.Text;

namespace MyProject.Web.SystemReports;

/// <summary>一顆磁碟（或網路分享、掛載點）的容量。</summary>
/// <param name="Root">磁碟的根目錄（例如 <c>C:\</c>、<c>\\server\share\</c>、<c>/var</c>）。</param>
public sealed record VolumeInfo(string Root, long TotalBytes, long FreeBytes);

/// <summary>
/// 找出路徑所在的磁碟並讀容量（0.10.39 起，系統巡檢報告）。
///
/// <list type="bullet">
/// <item>Windows：<c>GetVolumePathNameW</c> 找到真正的磁碟根目錄（處理掛載資料夾與 UNC 網路分享，<see cref="DriveInfo"/> 遇到 UNC 會丟例外），
/// 再用 <c>GetDiskFreeSpaceExW</c> 讀容量（回的是這個帳號可用的空間，有配額時比較準）。</item>
/// <item>其他系統：從 <see cref="DriveInfo.GetDrives"/> 找最長的掛載點前綴。</item>
/// <item>路徑還不存在（例如備份目錄還沒建）就往上找存在的父目錄。</item>
/// </list>
/// 讀不到（權限、離線）回 null，不丟例外。
/// </summary>
public static class VolumeResolver
{
    public static VolumeInfo? Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var existing = NearestExisting(Path.GetFullPath(path));
            if (existing is null)
            {
                return null;
            }

            return OperatingSystem.IsWindows() ? ResolveWindows(existing) : ResolveUnix(existing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>往上找第一個存在的目錄（路徑本身是檔案時用它的目錄）。</summary>
    internal static string? NearestExisting(string fullPath)
    {
        var current = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath) : fullPath;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(current))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    private static VolumeInfo? ResolveWindows(string directory)
    {
        var buffer = new StringBuilder(1024);
        var root = GetVolumePathNameW(directory, buffer, (uint)buffer.Capacity) ? buffer.ToString() : Path.GetPathRoot(directory);
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        if (!root.EndsWith(Path.DirectorySeparatorChar))
        {
            root += Path.DirectorySeparatorChar;
        }

        return GetDiskFreeSpaceExW(root, out var freeForCaller, out var total, out _)
            ? new VolumeInfo(root, (long)total, (long)freeForCaller)
            : null;
    }

    private static VolumeInfo? ResolveUnix(string directory)
    {
        var drive = DriveInfo.GetDrives()
            .Where(x => x.IsReady && directory.StartsWith(x.RootDirectory.FullName, StringComparison.Ordinal))
            .OrderByDescending(x => x.RootDirectory.FullName.Length)
            .FirstOrDefault();
        return drive is null ? null : new VolumeInfo(drive.RootDirectory.FullName, drive.TotalSize, drive.AvailableFreeSpace);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string lpszFileName, StringBuilder lpszVolumePathName, uint cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string lpDirectoryName, out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);
}
