using System.ComponentModel;
using Microsoft.Extensions.AI;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Components.Commons;
using MyProject.Web.Diagnostics;
using MyProject.Web.Health;
using static MyProject.Web.Ai.Agents.AdminAssistant.AdminAssistantToolText;

namespace MyProject.Web.Ai.Agents.AdminAssistant;

/// <summary>
/// 系統管理助理「監控與診斷」的唯讀工具（0.9.155 起）。權限沿用各頁的管理員專屬鍵；
/// 原頁面有寫查詢／檢視稽核的（日誌查詢、例外查詢與明細），這裡用同一個稽核代碼寫，detail 加 <c>source=admin-assistant</c>。
/// </summary>
public sealed class MonitoringAssistantTools
{
    public const int MaxLogTake = 200;
    public const int LogMessageMaxLength = 500;
    public const int MaxExceptionTake = 50;
    public const int ExceptionMessageMaxLength = 300;
    public const int StackTraceMaxLength = 4000;
    public const int EvidenceMaxLength = 300;

    private readonly ISystemHealthService health;
    private readonly ILogQueryService logs;
    private readonly LogLevelRuntimeState logLevel;
    private readonly ExceptionLogService exceptions;
    private readonly IDatabaseUsageService databaseUsage;
    private readonly IAuditLogService audit;
    private readonly CurrentUserService currentUser;

    public MonitoringAssistantTools(
        ISystemHealthService health,
        ILogQueryService logs,
        LogLevelRuntimeState logLevel,
        ExceptionLogService exceptions,
        IDatabaseUsageService databaseUsage,
        IAuditLogService audit,
        CurrentUserService currentUser)
    {
        this.health = health;
        this.logs = logs;
        this.logLevel = logLevel;
        this.exceptions = exceptions;
        this.databaseUsage = databaseUsage;
        this.audit = audit;
        this.currentUser = currentUser;
    }

    public static AiToolDefinition SystemHealthTool { get; } = Define(
        "get_system_health", "系統健康報告", MagicObjectHelper.角色_系統健康監控,
        "取得系統健康報告：總分、燈號與每個檢查項目（資料庫、磁碟、AI、郵件、排程等）的狀態與證據。"
        + "⚠️ 會實際做一次 AI 探測並登入 SMTP（花一點 AI 費用），同一段對話不要重複呼叫。",
        tools => tools.GetSystemHealthAsync);

    public static AiToolDefinition QueryLogsTool { get; } = Define(
        "query_logs", "查詢日誌", MagicObjectHelper.角色_日誌檢視,
        $"查詢系統日誌（NLog 檔案），回傳最新的幾筆。預設最近 24 小時、最長 3 天；筆數最多 {MaxLogTake}；訊息超過 {LogMessageMaxLength} 字會截斷。",
        tools => tools.QueryLogsAsync);

    public static AiToolDefinition LogLevelTool { get; } = Define(
        "get_log_level", "查看日誌等級", MagicObjectHelper.角色_日誌等級設定,
        "查看目前的日誌等級：系統預設、執行中的覆寫，以及實際生效的等級。",
        tools => tools.GetLogLevel);

    public static AiToolDefinition SearchExceptionsTool { get; } = Define(
        "search_exceptions", "查詢系統例外", MagicObjectHelper.角色_系統例外紀錄,
        $"查詢系統例外紀錄（同一種例外合併成一筆並計次），依最後發生時間由新到舊。預設最近 7 天；筆數最多 {MaxExceptionTake}。",
        tools => tools.SearchExceptionsAsync);

    public static AiToolDefinition ExceptionDetailTool { get; } = Define(
        "get_exception_detail", "查看例外堆疊", MagicObjectHelper.角色_系統例外紀錄,
        $"用例外編號（search_exceptions 回傳的 id）讀取完整堆疊，超過 {StackTraceMaxLength} 字會截斷。",
        tools => tools.GetExceptionDetailAsync);

    public static AiToolDefinition DatabaseUsageTool { get; } = Define(
        "get_database_usage", "資料庫用量", MagicObjectHelper.角色_資料庫用量,
        "取得 SQLite 資料庫的檔案大小（含 WAL）與各資料表的大小估計。",
        tools => tools.GetDatabaseUsageAsync);

    public static IReadOnlyList<AiToolDefinition> ReadTools { get; } =
        [SystemHealthTool, QueryLogsTool, LogLevelTool, SearchExceptionsTool, ExceptionDetailTool, DatabaseUsageTool];

    public async Task<string> GetSystemHealthAsync(CancellationToken cancellationToken = default)
    {
        var report = await health.GetReportAsync(cancellationToken);
        return ToJson(new
        {
            checkedAt = Format(report.CheckedAt.LocalDateTime),
            report.Score,
            report.Status,
            report.Light,
            items = report.Items.Select(item => new
            {
                item.Name,
                item.Category,
                item.Status,
                item.Light,
                evidence = Truncate(item.Evidence, EvidenceMaxLength),
                failureMessage = item.FailureMessage is null ? null : Truncate(item.FailureMessage, EvidenceMaxLength),
            }),
        });
    }

    public async Task<string> QueryLogsAsync(
        [Description("起始時間，" + TimeFormatHint + "；不填＝結束時間往前 24 小時")] string? start = null,
        [Description("結束時間，" + TimeFormatHint + "；不填＝現在")] string? end = null,
        [Description("最低等級：trace、debug、info、warn、error、fatal；不填＝不限")] string? minimumLevel = null,
        [Description("關鍵字（比對整筆原文）")] string? keyword = null,
        [Description("錯誤追蹤碼（TraceId，完全相同才符合）")] string? traceCode = null,
        [Description("最多回傳幾筆（最新的），1～200，預設 50")] int take = 50,
        CancellationToken cancellationToken = default)
    {
        var (startTime, endTime, note) = ParseRange(start, end, DateTime.Now, TimeSpan.FromHours(24), LogQueryRequest.MaxSpan);
        var level = ParseLevel(minimumLevel);
        var request = new LogQueryRequest
        {
            StartTime = startTime,
            EndTime = endTime,
            Take = Clamp(take, 1, MaxLogTake),
            MinimumLevel = level,
            Keyword = keyword?.Trim() ?? string.Empty,
            TraceCode = traceCode?.Trim() ?? string.Empty,
        };

        var result = await logs.QueryAsync(request, cancellationToken);

        await ViewAudit.WriteSearchAsync(audit, currentUser, AuditActions.LogViewer.Search, "LogQuery",
            AuditSearchCondition.Of("start", result.AppliedStartTime), AuditSearchCondition.Of("end", result.AppliedEndTime),
            AuditSearchCondition.Of("minimumLevel", level), AuditSearchCondition.Of("take", request.Take),
            AuditSearchCondition.Text("keyword", request.Keyword), AuditSearchCondition.Text("traceCode", request.TraceCode),
            AuditSearchCondition.Of("rows", result.Entries.Count), AuditSearchCondition.Of("source", AuditSource));

        return ToJson(new
        {
            appliedStart = Format(result.AppliedStartTime),
            appliedEnd = Format(result.AppliedEndTime),
            note,
            message = string.IsNullOrWhiteSpace(result.Message) ? null : result.Message,
            warnings = result.Warnings.Count == 0 ? null : result.Warnings,
            count = result.Entries.Count,
            entries = result.Entries.Select(entry => new
            {
                time = Format(entry.Timestamp),
                entry.Level,
                traceId = string.IsNullOrEmpty(entry.TraceId) ? null : entry.TraceId,
                entry.Logger,
                message = Truncate(entry.Message, LogMessageMaxLength),
                hasException = entry.HasException ? true : (bool?)null,
            }),
        });
    }

    public string GetLogLevel() => ToJson(new
    {
        systemDefault = logLevel.SystemDefaultLevel,
        @override = logLevel.OverrideLevel,
        effective = logLevel.EffectiveLevel,
        logLevel.IsAvailable,
    });

    public async Task<string> SearchExceptionsAsync(
        [Description("起始時間（最後發生時間），" + TimeFormatHint + "；不填＝往前 7 天")] string? start = null,
        [Description("結束時間，" + TimeFormatHint + "；不填＝現在")] string? end = null,
        [Description("關鍵字（例外類型、訊息、頁面等）")] string? keyword = null,
        [Description("發生時的登入帳號")] string? account = null,
        [Description("最多回傳幾筆，1～50，預設 20")] int take = 20)
    {
        var (startTime, endTime, note) = ParseRange(start, end, DateTime.Now, TimeSpan.FromDays(7), TimeSpan.FromDays(366));
        var query = new ExceptionLogQuery
        {
            StartTime = startTime,
            EndTime = endTime,
            Keyword = keyword?.Trim() ?? string.Empty,
            Account = account?.Trim() ?? string.Empty,
            CurrentPage = 1,
            PageSize = Clamp(take, 1, MaxExceptionTake),
        };

        var result = await exceptions.GetAsync(query);

        await ViewAudit.WriteSearchAsync(audit, currentUser, AuditActions.ExceptionLog.Search, "ExceptionLog",
            AuditSearchCondition.Of("start", startTime), AuditSearchCondition.Of("end", endTime),
            AuditSearchCondition.Text("account", query.Account), AuditSearchCondition.Text("keyword", query.Keyword),
            AuditSearchCondition.Of("source", AuditSource));

        return ToJson(new
        {
            appliedStart = Format(startTime),
            appliedEnd = Format(endTime),
            note,
            totalCount = result.Count,
            items = result.Result.Select(item => new
            {
                item.Id,
                type = item.ExceptionType,
                message = Truncate(item.Message, ExceptionMessageMaxLength),
                item.Source,
                item.Page,
                item.Account,
                count = item.OccurrenceCount,
                firstOccurredAt = Format(item.FirstOccurredAt),
                lastOccurredAt = Format(item.LastOccurredAt),
                lastTraceId = item.LastTraceId,
            }),
        });
    }

    public async Task<string> GetExceptionDetailAsync([Description("例外編號（search_exceptions 回傳的 id）")] int id)
    {
        var stackTrace = await exceptions.GetStackTraceAsync(id);
        if (string.IsNullOrWhiteSpace(stackTrace))
        {
            return $"找不到例外 #{id} 的堆疊（編號不存在，或堆疊檔已被清除）。";
        }

        await ViewAudit.WriteAsync(audit, currentUser, AuditActions.ExceptionLog.View, "ExceptionLog",
            id.ToString(System.Globalization.CultureInfo.InvariantCulture), $"source={AuditSource}");

        return ToJson(new { id, stackTrace = Truncate(stackTrace, StackTraceMaxLength) });
    }

    public async Task<string> GetDatabaseUsageAsync(CancellationToken cancellationToken = default)
        => ToJson(await databaseUsage.GetReportAsync(cancellationToken));

    private static LogLevelRank ParseLevel(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "trace" => LogLevelRank.Trace,
        "debug" => LogLevelRank.Debug,
        "info" or "information" => LogLevelRank.Info,
        "warn" or "warning" => LogLevelRank.Warn,
        "error" => LogLevelRank.Error,
        "fatal" or "critical" => LogLevelRank.Fatal,
        _ => LogLevelRank.Any,
    };

    private static AiToolDefinition Define(
        string name, string displayName, string permissionPage, string description, Func<MonitoringAssistantTools, Delegate> method)
        => new()
        {
            Name = name,
            DisplayName = displayName,
            Description = description,
            PermissionPage = permissionPage,
            PermissionAction = PermissionActions.View,
            Create = services => AIFunctionFactory.Create(method(services.GetRequiredService<MonitoringAssistantTools>()), name, description),
        };
}
