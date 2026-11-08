using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyProject.Models.Systems;

namespace MyProject.Business.Services.Other.AuditIntegrity;

/// <summary>檢查點：上一次每日完整性檢查看到的狀態。</summary>
public sealed record AuditChainCheckpoint(string KeyId, DateTime VerifiedAtUtc, List<AuditChainCheckpointEntry> Chains);

/// <summary>一條鏈在檢查點當下的終點與鏈頭重建時間。</summary>
public sealed record AuditChainCheckpointEntry(int Chain, long LastSeq, string LastHash, DateTime? RebuiltAt);

/// <summary>
/// 稽核雜湊鏈的檢查點（0.9.175 起）：<c>AuditKeyPath/audit-chain-checkpoint.json</c>，放在資料庫以外。
///
/// 鏈頭在資料庫裡，能改資料庫的人可以「刪掉最新幾筆再放回一份舊鏈頭」，或整份資料庫倒回舊版；
/// 這些只有和資料庫以外的紀錄比對才看得出來。<b>只由每天的排程更新</b>（手動驗證只比對不更新），
/// 所以金鑰更換、鏈頭重建、資料庫倒回這類一次性事件，會在看到它的那一次排程報一次，之後以新狀態為基準。
/// </summary>
public sealed class AuditChainCheckpointStore
{
    public const string FileName = "audit-chain-checkpoint.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IOptions<SystemSettings> settings;
    private readonly ILogger<AuditChainCheckpointStore> logger;

    public AuditChainCheckpointStore(IOptions<SystemSettings> settings, ILogger<AuditChainCheckpointStore> logger)
    {
        this.settings = settings;
        this.logger = logger;
    }

    private string FilePath => Path.Combine(settings.Value.ExternalFileSystem.AuditKeyPath, FileName);

    /// <summary>
    /// 讀檢查點。還沒有（第一次檢查、或金鑰目錄未設定）時回 true、<paramref name="checkpoint"/> 為 null；
    /// 檔案在但讀不懂（被改壞）時回 false —— 不可以悄悄當成第一次執行，否則一次性的比對全部被跳過。
    /// </summary>
    public bool TryLoad(out AuditChainCheckpoint? checkpoint)
    {
        checkpoint = null;
        if (string.IsNullOrWhiteSpace(settings.Value.ExternalFileSystem.AuditKeyPath) || File.Exists(FilePath) == false)
        {
            return true;
        }

        try
        {
            checkpoint = JsonSerializer.Deserialize<AuditChainCheckpoint>(File.ReadAllText(FilePath));
            return checkpoint is not null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read the audit chain checkpoint. Path={Path}", FilePath);
            return false;
        }
    }

    /// <summary>寫檢查點（先寫暫存檔再覆蓋，避免寫一半）。失敗時拋出。</summary>
    public void Save(AuditChainCheckpoint checkpoint)
    {
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(checkpoint, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}
