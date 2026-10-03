using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MyProject.Business.Startup;

/// <summary>
/// 跨行程的初始化鎖：對資料庫檔旁的 <c>&lt;資料庫檔&gt;.migration.lock</c> 開啟獨佔（<see cref="FileShare.None"/>）的檔案控制代碼。
///
/// 為什麼需要：EF Core 9 起 <c>Migrate()</c> 自己有鎖，但之後的 seed 沒有 ——
/// <c>RoleView.Name</c> 與 <c>MyUser.Account</c> 沒有唯一索引，兩個行程同時 seed 會建出重複的預設角色與 support 帳號。
/// 會同時啟動兩個行程的情況：IIS web garden、兩個站台共用同一個資料庫、短時間內連續回收、手動多開。
///
/// 為什麼是檔案鎖：named Mutex 只在同一台機器的同一個工作階段有效，檔案鎖則與行程類型無關；
/// 行程結束（包括被砍）時作業系統會自動釋放。鎖檔刻意不刪：刪除會和下一個開檔的行程競爭。
/// 停站時可以安全刪除它。
/// </summary>
internal static class DatabaseInitializationLock
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ProgressLogInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 取得鎖；回傳值 Dispose 時釋放。記憶體資料庫或沒有檔案路徑時不需要鎖，回傳空操作。
    /// 權限不足、路徑錯誤等不是「別人佔著」的錯誤會立即丟出，不會空等到逾時。
    /// </summary>
    public static async Task<IDisposable> AcquireAsync(
        string? databasePath,
        TimeSpan timeout,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(databasePath)
            || databasePath.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return NoopLock.Instance;
        }

        var lockPath = Path.GetFullPath(databasePath) + ".migration.lock";
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        var watch = Stopwatch.StartNew();
        var nextProgressLog = ProgressLogInterval;
        var waited = false;

        while (true)
        {
            try
            {
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (waited)
                {
                    logger.LogInformation(
                        "Acquired database initialization lock after waiting. ElapsedMs={ElapsedMs} LockPath={LockPath}",
                        watch.ElapsedMilliseconds,
                        lockPath);
                }

                return stream;
            }
            catch (IOException ex) when (IsHeldByAnotherHandle(ex))
            {
                if (watch.Elapsed >= timeout)
                {
                    throw new TimeoutException(
                        $"等待資料庫初始化鎖逾時（{timeout.TotalSeconds:0} 秒）：{lockPath}。"
                            + "可能有另一個行程正在初始化同一個資料庫；若確定沒有其他行程在執行，停站後可刪除這個鎖檔。",
                        ex);
                }

                if (!waited)
                {
                    waited = true;
                    logger.LogInformation(
                        "Database initialization lock is held by another process; waiting. LockPath={LockPath}",
                        lockPath);
                }
                else if (watch.Elapsed >= nextProgressLog)
                {
                    nextProgressLog += ProgressLogInterval;
                    logger.LogWarning(
                        "Still waiting for the database initialization lock. ElapsedMs={ElapsedMs} LockPath={LockPath}",
                        watch.ElapsedMilliseconds,
                        lockPath);
                }

                await Task.Delay(RetryInterval, cancellationToken);
            }
        }
    }

    /// <summary>只有「別的控制代碼佔著這個檔案」才值得重試；其餘 IO 錯誤（磁碟、權限、路徑）重試也不會好。</summary>
    private static bool IsHeldByAnotherHandle(IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        return code is ErrorSharingViolation or ErrorLockViolation;
    }

    private sealed class NoopLock : IDisposable
    {
        public static readonly NoopLock Instance = new();

        public void Dispose()
        {
        }
    }
}
