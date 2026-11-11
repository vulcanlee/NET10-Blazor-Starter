using System.Linq.Expressions;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Factories;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;

namespace MyProject.Business.Services.DataAccess;

/// <summary>
/// Agent 執行器存取對話用的窄介面（0.9.143 起）：只有存與讀自己的。測試以記憶體版替身取代。
/// </summary>
public interface IAiAgentConversationStore
{
    /// <summary>儲存（新增或覆寫）目前使用者的一段對話。全程不拋出：存不起來只記警告，對話照樣進行。</summary>
    Task SaveAsync(AiAgentConversationSnapshot snapshot);

    /// <summary>讀回目前使用者的一段對話；不存在或不是本人的回 null。</summary>
    Task<AiAgentConversationLoaded?> LoadMineAsync(Guid conversationId);
}

/// <summary>
/// Agent 對話的資料存取（0.9.143 起）：儲存、我的對話清單、改名、刪除與自動過期。
///
/// 資料表只放中繼資料，內容一律寫進 <see cref="AiAgentConversationFileStore"/>。
/// ⚠️ 一律只能存取<b>目前使用者自己的</b>對話（取自 <see cref="CurrentUserService"/>，本服務只由 Blazor 畫面呼叫）；
/// 別人的對話不論存在與否都當作找不到，不洩漏它是否存在。日誌只記 ConversationId，絕不記內容與標題。
/// </summary>
public class AiAgentConversationService : IAiAgentConversationStore
{
    /// <summary>自動標題取第一則訊息的前幾個字。</summary>
    public const int AutoTitleLength = 40;

    /// <summary>標題長度上限（改名時檢查）。</summary>
    public const int MaxTitleLength = MyProject.Dtos.Ai.AgentConversationLimits.MaxTitleLength;

    /// <summary>「我的對話」清單最多列幾段（依最後對話時間 desc）。</summary>
    public const int MaxListCount = 100;

    /// <summary>
    /// 內容檔的序列化設定：中文不跳脫（同 AI 對話紀錄，檔案才小、可直接閱讀）；不縮排（session 可能很長）。
    /// </summary>
    public static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IDbContextFactory<BackendDBContext> contextFactory;
    private readonly AiAgentConversationFileStore fileStore;
    private readonly CurrentUserService currentUserService;
    private readonly IAuditLogService auditLogService;
    private readonly IOptionsMonitor<AiAgentConversationSettings> settings;
    private readonly ILogger<AiAgentConversationService> logger;

    // ⚠️ 只能有一個建構式：DataAccessServiceLifetimeTests 用 GetConstructors().Single()。
    public AiAgentConversationService(
        IDbContextFactory<BackendDBContext> contextFactory,
        AiAgentConversationFileStore fileStore,
        CurrentUserService currentUserService,
        IAuditLogService auditLogService,
        IOptionsMonitor<AiAgentConversationSettings> settings,
        ILogger<AiAgentConversationService> logger)
    {
        this.contextFactory = contextFactory;
        this.fileStore = fileStore;
        this.currentUserService = currentUserService;
        this.auditLogService = auditLogService;
        this.settings = settings;
        this.logger = logger;
    }

    public int RetentionDays => settings.CurrentValue.RetentionDays;

    private int CurrentUserId => currentUserService.CurrentUser.Id;

    /// <summary>自動標題：第一則訊息的空白（含換行）收成一個空格，取前 <see cref="AutoTitleLength"/> 字。</summary>
    public static string TitleFrom(string message)
    {
        var collapsed = string.Join(' ', message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= AutoTitleLength ? collapsed : string.Concat(collapsed.AsSpan(0, AutoTitleLength), "…");
    }

    public async Task SaveAsync(AiAgentConversationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var user = currentUserService.CurrentUser;
        if (user.Id <= 0)
        {
            logger.LogWarning("Skipped saving AI agent conversation without a signed-in user. ConversationId={ConversationId}", snapshot.ConversationId);
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(snapshot.Content, FileJsonOptions);
            var now = DateTime.Now;
            await using var context = await contextFactory.CreateDbContextAsync();
            var item = await context.AiAgentConversation.FirstOrDefaultAsync(x => x.ConversationId == snapshot.ConversationId);

            if (item is null)
            {
                var contentFile = await fileStore.WriteAsync(now, snapshot.ConversationId, json);
                item = new AiAgentConversation
                {
                    ConversationId = snapshot.ConversationId,
                    UserId = user.Id,
                    Account = user.Account,
                    AgentName = snapshot.AgentName,
                    Title = Truncate(snapshot.Title),
                    CreatedAt = now,
                    UpdatedAt = now,
                    HasPendingApproval = snapshot.HasPendingApproval,
                    ContentFile = contentFile,
                };

                try
                {
                    await context.AiAgentConversation.AddAsync(item);
                    await context.SaveChangesAsync();
                }
                catch (Exception)
                {
                    // 資料列沒建起來，剛才那個檔就是孤兒 —— 一律清掉。
                    fileStore.Delete(contentFile);
                    throw;
                }

                return;
            }

            if (item.UserId != user.Id)
            {
                logger.LogWarning("Refused to save another user's AI agent conversation. ConversationId={ConversationId}", snapshot.ConversationId);
                return;
            }

            // 路徑由建立時間與 ConversationId 決定，覆寫同一個檔；寫失敗時保留原本的值（舊內容仍可讀）。
            item.ContentFile = await fileStore.WriteAsync(item.CreatedAt, item.ConversationId, json) ?? item.ContentFile;
            item.UpdatedAt = now;
            item.HasPendingApproval = snapshot.HasPendingApproval;
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save AI agent conversation. ConversationId={ConversationId}", snapshot.ConversationId);
        }
    }

    public async Task<AiAgentConversationLoaded?> LoadMineAsync(Guid conversationId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var item = await Mine(context).FirstOrDefaultAsync(x => x.ConversationId == conversationId);
        if (item is null)
        {
            return null;
        }

        return new AiAgentConversationLoaded(ToSummary(item), Deserialize(await fileStore.ReadAsync(item.ContentFile), conversationId));
    }

    /// <summary>目前使用者在某個 Agent 的對話，依最後對話時間 desc，最多 <see cref="MaxListCount"/> 段。</summary>
    public async Task<IReadOnlyList<AiAgentConversationSummary>> ListMineAsync(string agentName)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var items = await Mine(context)
            .Where(x => x.AgentName == agentName)
            .OrderByDescending(x => x.UpdatedAt)
            .ThenByDescending(x => x.Id)
            .Take(MaxListCount)
            .ToListAsync();
        return items.Select(ToSummary).ToList();
    }

    /// <summary>改標題（只能改自己的）。不更新最後對話時間、不寫稽核（標題只給本人看）。</summary>
    public async Task<VerifyRecordResult> RenameAsync(Guid conversationId, string title)
    {
        var trimmed = title?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return VerifyRecordResultFactory.Invalid("標題不可空白。");
        }

        if (trimmed.Length > MaxTitleLength)
        {
            return VerifyRecordResultFactory.Invalid($"標題最多 {MaxTitleLength} 個字。");
        }

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            var item = await Mine(context).FirstOrDefaultAsync(x => x.ConversationId == conversationId);
            if (item is null)
            {
                return VerifyRecordResultFactory.NotFound("找不到這段對話。");
            }

            item.Title = trimmed;
            await context.SaveChangesAsync();
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to rename AI agent conversation. ConversationId={ConversationId}", conversationId);
            return VerifyRecordResultFactory.Error("重新命名失敗。", ex);
        }
    }

    /// <summary>刪除自己的一段對話，連同內容檔；寫 <c>AiAgentConversation.Delete</c> 稽核。</summary>
    public async Task<VerifyRecordResult> DeleteAsync(Guid conversationId)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            var item = await Mine(context).FirstOrDefaultAsync(x => x.ConversationId == conversationId);
            if (item is null)
            {
                return VerifyRecordResultFactory.NotFound("找不到這段對話。");
            }

            var contentFile = item.ContentFile;
            context.AiAgentConversation.Remove(item);
            await context.SaveChangesAsync();
            fileStore.Delete(contentFile);

            var user = currentUserService.CurrentUser;
            await auditLogService.WriteAsync(
                AuditActions.AiAgentConversation.Delete,
                success: true,
                actorUserId: user.Id,
                actorAccount: user.Account,
                targetType: "AiAgentConversation",
                targetId: conversationId.ToString(),
                detail: $"agent={item.AgentName}");
            return VerifyRecordResultFactory.Build(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete AI agent conversation. ConversationId={ConversationId}", conversationId);
            return VerifyRecordResultFactory.Error("刪除對話失敗。", ex);
        }
    }

    /// <summary>
    /// 搶下一段自己的、停在核准的對話（0.10.7 起，WASM 遷移 M8 的核准 API）：以一個條件式 UPDATE 把 <c>HasPendingApproval</c> 從 true 改成 false，
    /// 改到一列才回 true。同一批核准從兩個分頁（甚至兩個行程）同時送來時只有一個搶得到，工具不會執行兩次；
    /// 執行結束存檔時會依實際狀態再寫回這個欄位。
    /// </summary>
    public async Task<bool> TryClaimPendingApprovalAsync(Guid conversationId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var claimed = await Mine(context)
            .Where(x => x.ConversationId == conversationId && x.HasPendingApproval)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.HasPendingApproval, false));
        return claimed == 1;
    }

    /// <summary>自動過期：刪除最後對話早於 <c>RetentionDays</c> 天前（當天 0 點）的對話與內容檔。失敗回 null。</summary>
    public Task<int?> PurgeExpiredAsync(DateTime nowLocal) => PurgeOlderThanAsync(ExpiryThreshold(nowLocal));

    /// <summary>自動過期的門檻：今天往前 <c>RetentionDays</c> 天的 0 點（本地時間，同 AI 對話紀錄）。</summary>
    public DateTime ExpiryThreshold(DateTime nowLocal) => nowLocal.Date.AddDays(-RetentionDays);

    /// <summary>
    /// 刪除最後對話早於 <paramref name="threshold"/>（本地時間）的對話與內容檔：自動過期與「資料保留與清理」頁的「自訂清除」共用。
    /// 不分使用者（這是管理員的清理）。先刪資料列再刪檔（同 AI 對話紀錄）。
    /// </summary>
    /// <returns>刪除的筆數；失敗回 null。</returns>
    public async Task<int?> PurgeOlderThanAsync(DateTime threshold)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync();
            var stale = context.AiAgentConversation.Where(OlderThan(threshold));
            var contentFiles = await stale.Select(x => x.ContentFile).ToListAsync();
            if (contentFiles.Count == 0)
            {
                return 0;
            }

            // 先刪資料列再刪檔：資料列刪失敗就整批不動，不會留下「有列卻沒檔」的狀態。
            // 這兩次查詢之間有人接著聊，UpdatedAt 變新就不再符合條件，資料列不會被刪；但它的檔已在清單裡 ——
            // 所以只刪「資料列確實被刪掉」的那幾個檔。
            var removed = await stale.ExecuteDeleteAsync();
            var survivors = await context.AiAgentConversation
                .Where(x => x.ContentFile != null && contentFiles.Contains(x.ContentFile))
                .Select(x => x.ContentFile)
                .ToListAsync();
            foreach (var contentFile in contentFiles.Except(survivors))
            {
                fileStore.Delete(contentFile);
            }

            fileStore.DeleteEmptyMonthFolders();
            logger.LogInformation("Purged AI agent conversations. Rows={Rows}, Threshold={Threshold}", removed, threshold);
            return removed;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to purge AI agent conversations.");
            return null;
        }
    }

    /// <summary>筆數、最舊一筆（最後對話時間）與自動過期會刪掉的筆數。</summary>
    public Task<RetentionStats> GetRetentionStatsAsync(DateTime nowLocal)
        => GetRetentionStatsBeforeAsync(ExpiryThreshold(nowLocal));

    /// <summary>筆數、最舊一筆與早於 <paramref name="threshold"/> 的筆數 —— 「自訂清除」的預覽也用它。</summary>
    public async Task<RetentionStats> GetRetentionStatsBeforeAsync(DateTime threshold)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var total = await context.AiAgentConversation.CountAsync();
        var oldest = await context.AiAgentConversation.MinAsync(x => (DateTime?)x.UpdatedAt);
        var expired = await context.AiAgentConversation.CountAsync(OlderThan(threshold));
        return new RetentionStats(total, oldest, expired);
    }

    /// <summary>清理條件：自動過期、自訂清除與統計共用，「下次會清掉幾筆」才會等於實際刪掉的筆數。</summary>
    private static Expression<Func<AiAgentConversation, bool>> OlderThan(DateTime threshold)
        => x => x.UpdatedAt < threshold;

    /// <summary>目前使用者自己的對話。未登入（Id 0）時一筆都沒有。</summary>
    private IQueryable<AiAgentConversation> Mine(BackendDBContext context)
    {
        var userId = CurrentUserId;
        return context.AiAgentConversation.Where(x => userId > 0 && x.UserId == userId);
    }

    private static AiAgentConversationSummary ToSummary(AiAgentConversation item)
        => new(item.ConversationId, item.AgentName, item.Title, item.CreatedAt, item.UpdatedAt, item.HasPendingApproval);

    private static string Truncate(string title)
    {
        var trimmed = title.Trim();
        return trimmed.Length <= MaxTitleLength ? trimmed : trimmed[..MaxTitleLength];
    }

    private AiAgentConversationContent? Deserialize(string? json, Guid conversationId)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var content = JsonSerializer.Deserialize<AiAgentConversationContent>(json, FileJsonOptions);
            if (content?.SchemaVersion != AiAgentConversationContent.CurrentSchemaVersion)
            {
                logger.LogWarning("Unsupported AI agent conversation file version. ConversationId={ConversationId}, Version={Version}", conversationId, content?.SchemaVersion);
                return null;
            }

            return content;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse AI agent conversation file. ConversationId={ConversationId}", conversationId);
            return null;
        }
    }
}
