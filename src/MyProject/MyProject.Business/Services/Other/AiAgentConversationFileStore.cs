using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyProject.Models.Systems;

namespace MyProject.Business.Services.Other;

/// <summary>
/// Agent 對話的內容檔存放（0.9.143 起）。
///
/// ⚠️ <b>檔案生命週期的唯一入口。</b>寫入、刪除與自動過期都必須經過這裡。作法與 <see cref="AiCallLogFileStore"/> 一致，
/// 另外比照 <see cref="AuditContentFileStore"/> 檢查根目錄：路徑來自資料庫，不讓 <c>../</c> 這類值讀刪到根目錄以外的檔案。
///
/// 相對路徑為 <c>{yyyyMM(建立時間)}/{ConversationId:N}.json</c>，每次儲存整份覆寫。
/// 全部方法<b>絕不拋出</b>：存檔失敗不可以影響對話本身。日誌只記路徑，絕不記內容。
/// </summary>
public class AiAgentConversationFileStore
{
    private readonly IOptions<SystemSettings> systemSettingsOptions;
    private readonly ILogger<AiAgentConversationFileStore> logger;

    public AiAgentConversationFileStore(
        IOptions<SystemSettings> systemSettingsOptions,
        ILogger<AiAgentConversationFileStore> logger)
    {
        this.systemSettingsOptions = systemSettingsOptions;
        this.logger = logger;
    }

    private const int MaxAttempts = 5;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    private string RootPath => systemSettingsOptions.Value.ExternalFileSystem.AiAgentConversationPath;

    /// <summary>寫入（覆寫）內容檔。</summary>
    /// <returns>成功時回傳相對路徑；失敗回 null。</returns>
    public async Task<string?> WriteAsync(DateTime createdAt, Guid conversationId, string content)
    {
        var relativePath = $"{createdAt:yyyyMM}/{conversationId:N}.json";
        var fullPath = TryResolveFullPath(relativePath);
        if (fullPath is null || string.IsNullOrEmpty(content))
        {
            return null;
        }

        try
        {
            // 寫入時允許別人同時開著這個檔（0.10.7 獨立審查）：另一個請求正在讀（例如打開對話時等不到鎖而直接讀檔），
            // 寫入也不可失敗 —— 失敗的話資料庫的「待核准」旗標與內容檔不一致，對話會卡死。被其他程式短暫鎖住時重試幾次。
            // （Windows 不允許把檔案換到別人開著的檔案上，所以不用「暫存檔換上」。讀到寫一半的內容時，讀取端會重試。）
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await using var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write,
                        FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
                    await stream.WriteAsync(bytes);
                    break;
                }
                catch (IOException) when (attempt < MaxAttempts)
                {
                    await Task.Delay(RetryDelay);
                }
            }

            return relativePath;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write AI agent conversation file. Path={RelativePath}", relativePath);
            return null;
        }
    }

    /// <summary>讀取內容檔。檔案不存在、路徑不合法或讀取失敗一律回 null。</summary>
    public async Task<string?> ReadAsync(string? relativePath)
    {
        var fullPath = TryResolveFullPath(relativePath);
        if (fullPath is null)
        {
            return null;
        }

        try
        {
            if (File.Exists(fullPath) == false)
            {
                return null;
            }

            // 允許別人同時寫入（見 WriteAsync）；被短暫鎖住時重試幾次。
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
                    using var reader = new StreamReader(stream);
                    return await reader.ReadToEndAsync();
                }
                catch (IOException) when (attempt < MaxAttempts && File.Exists(fullPath))
                {
                    await Task.Delay(RetryDelay);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read AI agent conversation file. Path={RelativePath}", relativePath);
            return null;
        }
    }

    /// <summary>刪除單一檔案。檔案不存在視為成功。</summary>
    public void Delete(string? relativePath)
    {
        var fullPath = TryResolveFullPath(relativePath);
        if (fullPath is null)
        {
            return;
        }

        try
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete AI agent conversation file. Path={RelativePath}", relativePath);
        }
    }

    /// <summary>
    /// 刪掉空的 <c>{yyyyMM}</c> 目錄（清理的收尾）。
    /// ⚠️ 不能像 AI 對話紀錄那樣整個刪掉舊月份：月份是<b>建立</b>時間，過期卻看<b>最後對話</b>時間，
    /// 半年前開的對話昨天還在聊，檔案仍在半年前的目錄裡。
    /// </summary>
    public void DeleteEmptyMonthFolders()
    {
        if (string.IsNullOrWhiteSpace(RootPath) || Directory.Exists(RootPath) == false)
        {
            return;
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(RootPath))
            {
                if (DateTime.TryParseExact(Path.GetFileName(directory), "yyyyMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                    && Directory.EnumerateFileSystemEntries(directory).Any() == false)
                {
                    Directory.Delete(directory);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove empty AI agent conversation folders.");
        }
    }

    /// <summary>
    /// 相對路徑 → 完整路徑；結果不在根目錄底下（<c>../</c>、絕對路徑）或根目錄未設定時回 null（同 <see cref="AuditContentFileStore"/>）。
    /// </summary>
    internal string? TryResolveFullPath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(RootPath) || string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootPath));
        var fullPath = Path.GetFullPath(Path.Combine(root, normalized));
        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
    }
}
