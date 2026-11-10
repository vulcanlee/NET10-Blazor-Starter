using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MyProject.Models.Systems;
using MyProject.Web.Configuration.Validation;

namespace MyProject.Tests;

/// <summary>
/// 稽核防竄改的守門（0.9.175 起，速查表 §6.31）。
///
/// 1. 只有 <c>AuditChainStore</c> 可以新增、修改、刪除 <c>AuditLog</c> 與 <c>AuditChainHead</c>：
///    繞過它寫進去的紀錄沒有簽章，每天的完整性檢查會當成竄改通報。
/// 2. 金鑰目錄不可與其他資料目錄重疊：那些目錄會被系統備份整個打包，金鑰進了備份，拿到備份的人就能重算簽章。
/// </summary>
public sealed class AuditIntegrityConventionTests
{
    private static readonly Regex[] Mutations =
    [
        new(@"\.(AuditLog|AuditChainHead)\.(Add|AddRange|Remove|RemoveRange|Update|UpdateRange|Attach)\(", RegexOptions.Compiled),
        new(@"\.(AuditLog|AuditChainHead)\b[^;]*\.Execute(Delete|Update)", RegexOptions.Compiled | RegexOptions.Singleline),
        new(@"(INSERT\s+INTO|DELETE\s+FROM|UPDATE)\s+""?(AuditLog|AuditChainHead)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    ];

    /// <summary>
    /// 碰到稽核資料表（<c>.AuditLog</c>、<c>.AuditChainHead</c>、<c>Set&lt;AuditLog&gt;</c>）的檔案裡，
    /// 不可以有新增／修改的呼叫或任何 <c>ExecuteDelete</c>／<c>ExecuteUpdate</c>：
    /// 「先把查詢存成變數再刪」（0.9.174 之前清除的寫法）逐句比對抓不到，所以以整個檔案判斷。
    /// </summary>
    private static bool IsMutating(string text)
    {
        if (Mutations.Any(regex => regex.IsMatch(text)))
        {
            return true;
        }

        var touchesAuditTables = Regex.IsMatch(text, @"\.(AuditLog|AuditChainHead)\b|Set<(AuditLog|AuditChainHead)>");
        return touchesAuditTables && Regex.IsMatch(text, @"Execute(Delete|Update)|Set<(AuditLog|AuditChainHead)>\(\)\.(Add|Remove|Update|Attach)");
    }

    [Fact]
    public void OnlyTheChainStore_ShouldWriteOrDeleteAuditRows()
    {
        var root = FindSourceRoot();
        var offenders = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")
                        && !path.Contains("MyProject.Tests")
                        && !path.EndsWith("AuditChainStore.cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => IsMutating(File.ReadAllText(path)))
            .Select(Path.GetFileName)
            .Distinct()
            .ToList();

        Assert.True(offenders.Count == 0, "只有 AuditChainStore 可以新增、修改、刪除稽核紀錄與鏈頭：" + string.Join("、", offenders));
    }

    public static TheoryData<string> DataPathKeys =>
    [
        nameof(ExternalFileSystem.DatabasePath), nameof(ExternalFileSystem.DownloadPath), nameof(ExternalFileSystem.UploadPath),
        nameof(ExternalFileSystem.ProjectFilePath), nameof(ExternalFileSystem.ExceptionPath), nameof(ExternalFileSystem.TokenUsagePath),
        nameof(ExternalFileSystem.AiCallLogPath), nameof(ExternalFileSystem.AiAgentConversationPath), nameof(ExternalFileSystem.AuditContentPath),
        nameof(ExternalFileSystem.DataProtectionKeyPath),
    ];

    [Theory]
    [MemberData(nameof(DataPathKeys))]
    public void AuditKeyPath_OverlappingADataPath_ShouldFailValidation(string key)
    {
        var inside = ValidPaths();
        inside.AuditKeyPath = Path.Combine(GetPath(inside, key), "AuditKey");
        AssertAuditKeyPathInvalid(inside);

        var containing = ValidPaths();
        containing.AuditKeyPath = Path.GetDirectoryName(GetPath(containing, key))!;
        AssertAuditKeyPathInvalid(containing);
    }

    [Fact]
    public void AuditKeyPath_InsideTheSiteDirectory_OrMissing_ShouldFailValidation()
    {
        var site = ValidPaths();
        site.AuditKeyPath = Path.Combine(AppContext.BaseDirectory, "AuditKey");
        AssertAuditKeyPathInvalid(site);

        var missing = ValidPaths();
        missing.AuditKeyPath = string.Empty;
        AssertAuditKeyPathInvalid(missing);
    }

    [Fact]
    public void AuditKeyPath_InItsOwnFolder_ShouldPass()
    {
        var result = new SystemSettingsValidator().Validate(null, WithName(ValidPaths()));
        Assert.True(result.Succeeded, string.Join(" | ", result.Failures ?? []));
    }

    private static ExternalFileSystem ValidPaths() => new()
    {
        DatabasePath = @"C:\data\app\DB",
        DownloadPath = @"C:\data\app\Download",
        UploadPath = @"C:\data\app\Upload",
        ProjectFilePath = @"C:\data\app\ProjectFile",
        ExceptionPath = @"C:\data\app\Exception",
        TokenUsagePath = @"C:\data\app\TokenUsage",
        AiCallLogPath = @"C:\data\app\AiCallLog",
        AiAgentConversationPath = @"C:\data\app\AiAgentConversation",
        AuditContentPath = @"C:\data\app\AuditContent",
        DataProtectionKeyPath = @"C:\data\app\Keys",
        BackupPath = @"D:\backup\app",
        AuditKeyPath = @"E:\secrets\AuditKey",
    };

    private static string GetPath(ExternalFileSystem paths, string key)
        => (string)typeof(ExternalFileSystem).GetProperty(key)!.GetValue(paths)!;

    private static SystemSettings WithName(ExternalFileSystem paths)
    {
        var value = new SystemSettings { ExternalFileSystem = paths };
        value.SystemInformation.SystemName = "測試";
        return value;
    }

    private static void AssertAuditKeyPathInvalid(ExternalFileSystem paths)
    {
        var result = new SystemSettingsValidator().Validate(null, WithName(paths));
        Assert.True(result.Failed, $"AuditKeyPath={paths.AuditKeyPath} 應該被擋下。");
        Assert.Contains(result.Failures!, x => x.StartsWith("SystemSettings:ExternalFileSystem:AuditKeyPath", StringComparison.Ordinal));
    }

    private static string FindSourceRoot() => SourceTree.SourceRoot;
}
