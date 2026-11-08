using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyProject.Models.Systems;

namespace MyProject.Business.Services.Other.AuditIntegrity;

/// <summary>稽核簽章金鑰：32 bytes 與它的代號。</summary>
public sealed record AuditIntegrityKey(byte[] Bytes, string KeyId);

/// <summary>金鑰檔的狀態（完整性檢查時確認）。</summary>
public enum AuditKeyFileStatus
{
    /// <summary>金鑰檔在，內容和執行中的金鑰相同。</summary>
    Ok,
    /// <summary>金鑰檔不見了，已用執行中的金鑰寫回去。</summary>
    Restored,
    /// <summary>金鑰檔被換成別的內容；目前仍用執行中的金鑰，下次重新啟動就會改用檔案裡的金鑰。</summary>
    Replaced,
}

public interface IAuditIntegrityKeyStore
{
    /// <summary>取得金鑰；第一次呼叫時讀檔，檔案不存在就產生。</summary>
    AuditIntegrityKey GetKey();

    /// <summary>確認金鑰檔還在、沒被換掉；不見了就寫回去。</summary>
    AuditKeyFileStatus CheckFile();

    /// <summary>
    /// 金鑰是本行程產生的（金鑰檔原本不存在）。寫入端只在這種情況、或檢查點記的是另一把金鑰時，
    /// 才接受「鏈頭的金鑰代號和目前不同」是真的換了金鑰，而不是有人改了代號想跳過簽章檢查。
    /// </summary>
    bool CreatedNewKey { get; }
}

/// <summary>
/// 稽核防竄改金鑰（0.9.175 起）：<c>AuditKeyPath/audit-hmac.key</c>，內容是 32 bytes 亂數的 base64。
///
/// - <b>延遲載入</b>：第一次寫稽核時才讀檔或產生，啟動與 <c>dotnet ef</c> 階段都不會碰到。
/// - <b>兩個行程同時產生</b>（IIS 重疊回收）：各自先寫暫存檔再 <see cref="File.Move(string, string, bool)"/>（不覆寫），
///   輸的一方刪掉自己的暫存檔，改讀對方的。
/// - <b>刻意不進備份</b>，見 <see cref="ExternalFileSystem.AuditKeyPath"/>。
/// </summary>
public sealed class AuditIntegrityKeyStore : IAuditIntegrityKeyStore
{
    public const string KeyFileName = "audit-hmac.key";
    private const int KeyLength = 32;

    private readonly IOptions<SystemSettings> settings;
    private readonly ILogger<AuditIntegrityKeyStore> logger;
    private readonly Lock gate = new();
    private AuditIntegrityKey? cached;

    public bool CreatedNewKey { get; private set; }

    public AuditIntegrityKeyStore(IOptions<SystemSettings> settings, ILogger<AuditIntegrityKeyStore> logger)
    {
        this.settings = settings;
        this.logger = logger;
    }

    private string KeyFilePath
    {
        get
        {
            var root = settings.Value.ExternalFileSystem.AuditKeyPath;
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new InvalidOperationException("SystemSettings:ExternalFileSystem:AuditKeyPath 未設定，無法簽章稽核紀錄。");
            }

            return Path.Combine(root, KeyFileName);
        }
    }

    public AuditIntegrityKey GetKey()
    {
        lock (gate)
        {
            return cached ??= LoadOrCreate();
        }
    }

    public AuditKeyFileStatus CheckFile()
    {
        lock (gate)
        {
            var key = cached ??= LoadOrCreate();
            var path = KeyFilePath;
            if (File.Exists(path) == false)
            {
                logger.LogError("Audit integrity key file was missing and has been restored. Path={Path}", path);
                // 寫不進去代表另一個行程剛產生了別的金鑰：檔案裡的已經不是執行中的這一把。
                return WriteNew(path, key.Bytes) ? AuditKeyFileStatus.Restored : AuditKeyFileStatus.Replaced;
            }

            var onDisk = Read(path);
            if (CryptographicOperations.FixedTimeEquals(onDisk, key.Bytes))
            {
                return AuditKeyFileStatus.Ok;
            }

            logger.LogError("Audit integrity key file content differs from the key in use. Path={Path}", path);
            return AuditKeyFileStatus.Replaced;
        }
    }

    private AuditIntegrityKey LoadOrCreate()
    {
        var path = KeyFilePath;
        if (File.Exists(path) == false)
        {
            var bytes = RandomNumberGenerator.GetBytes(KeyLength);
            if (WriteNew(path, bytes))
            {
                // 第一次啟用是正常的；已有簽章紀錄時才是金鑰遺失，那由鏈頭的 KeyId 對不上來發現並記 Error。
                logger.LogWarning("Audit integrity key file created. Path={Path}", path);
                CreatedNewKey = true;
                return new AuditIntegrityKey(bytes, AuditHashV1.ComputeKeyId(bytes));
            }
        }

        var existing = Read(path);
        return new AuditIntegrityKey(existing, AuditHashV1.ComputeKeyId(existing));
    }

    /// <summary>寫入新金鑰檔；目標已存在（被別的行程搶先）時回 false。</summary>
    private static bool WriteNew(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $"{KeyFileName}.{Guid.NewGuid():N}.tmp");
        WithRetry(() => File.WriteAllText(temp, Convert.ToBase64String(bytes)));
        try
        {
            return WithRetry(() =>
            {
                try
                {
                    File.Move(temp, path, overwrite: false);
                    return true;
                }
                catch (IOException) when (File.Exists(path))
                {
                    // 被別的行程（或執行緒）搶先建立：改用它的。
                    return false;
                }
            });
        }
        finally
        {
            WithRetry(() => File.Delete(temp));
        }
    }

    private static byte[] Read(string path)
    {
        var bytes = Convert.FromBase64String(WithRetry(() => File.ReadAllText(path)).Trim());
        if (bytes.Length != KeyLength)
        {
            throw new InvalidOperationException($"稽核金鑰檔格式不正確（應為 {KeyLength} bytes）：{path}");
        }

        return bytes;
    }

    /// <summary>
    /// 剛建立的檔案可能被防毒軟體或另一個行程短暫鎖住（共用違規），讀寫失敗時稍等重試幾次；
    /// 不重試的話，那一筆稽核會因為取不到金鑰而寫不進去。
    /// </summary>
    private static T WithRetry<T>(Func<T> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }

    private static void WithRetry(Action action) => WithRetry(() =>
    {
        action();
        return true;
    });
}
