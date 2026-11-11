using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MyProject.Business.Services.Other;

/// <summary>
/// 工作階段版本一被改，就清掉 <see cref="SecurityStampCache"/>（0.10.0 起，singleton，掛在 DbContext 選項上）。
///
/// 以前改版本的地方（改密碼、停用、刪除、強制登出、改角色、開關兩步驟驗證）都直接寫資料庫、沒有清快取，
/// 被強制登出的人最多還能再用 <c>SessionSettings.ValidationIntervalMinutes</c>（預設 5 分鐘）。
/// 在資料庫指令這一層攔，不論是追蹤實體存檔或 <c>ExecuteUpdate</c>、現在或日後新增的程式路徑，都不必記得手動清。
///
/// 時機：指令執行後立刻清一次；指令若在交易中（SaveChanges 的隱含交易或明確交易），交易提交後**再清一次**，
/// 避免提交前有別的請求把舊版本重新讀進快取。清的是整個快取：版本很少變動，代價只是之後幾次查詢回到資料庫。
/// </summary>
public sealed partial class SecurityStampInvalidationInterceptor : DbCommandInterceptor, IDbTransactionInterceptor
{
    private readonly SecurityStampCache cache;
    private readonly ILogger<SecurityStampInvalidationInterceptor> logger;
    private readonly ConditionalWeakTable<DbContext, object> pendingCommit = [];

    public SecurityStampInvalidationInterceptor(SecurityStampCache cache, ILogger<SecurityStampInvalidationInterceptor> logger)
    {
        this.cache = cache;
        this.logger = logger;
    }

    /// <summary>這段 SQL 是否會改到工作階段狀態：對 <c>MyUser</c> 的 UPDATE 且有設定 <c>SecurityStamp</c>，或刪除 <c>MyUser</c>。</summary>
    public static bool ChangesSessionState(string sql)
        => DeleteUser().IsMatch(sql) || (UpdateUser().IsMatch(sql) && sql.Contains("\"SecurityStamp\" =", StringComparison.Ordinal));

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Observe(command, eventData);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Observe(command, eventData);
        return ValueTask.FromResult(result);
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Observe(command, eventData);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        Observe(command, eventData);
        return ValueTask.FromResult(result);
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => OnCommitted(eventData.Context);

    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        OnCommitted(eventData.Context);
        return Task.CompletedTask;
    }

    private void Observe(DbCommand command, CommandEventData eventData)
    {
        if (!ChangesSessionState(command.CommandText))
        {
            return;
        }

        cache.Clear();
        logger.LogDebug("Session state changed; the security stamp cache was cleared.");
        if (command.Transaction is not null && eventData.Context is { } context)
        {
            pendingCommit.AddOrUpdate(context, new object());
        }
    }

    private void OnCommitted(DbContext? context)
    {
        if (context is not null && pendingCommit.Remove(context))
        {
            cache.Clear();
        }
    }

    [GeneratedRegex("""\bUPDATE\s+"MyUser"\s""", RegexOptions.IgnoreCase)]
    private static partial Regex UpdateUser();

    [GeneratedRegex("""\bDELETE\s+FROM\s+"MyUser"\s""", RegexOptions.IgnoreCase)]
    private static partial Regex DeleteUser();
}
