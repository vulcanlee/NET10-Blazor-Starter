using System.ComponentModel;
using Microsoft.Extensions.AI;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Auditing;
using MyProject.Web.Diagnostics;
using MyProject.Web.Email;
using MyProject.Web.Health;
using static MyProject.Web.Ai.Agents.AdminAssistant.AdminAssistantToolText;

namespace MyProject.Web.Ai.Agents.AdminAssistant;

/// <summary>
/// 系統管理助理「監控與診斷」的唯讀工具（0.9.155 起）。權限沿用各頁的管理員專屬鍵；
/// 原頁面有寫查詢／檢視稽核的（日誌查詢、例外查詢與明細），這裡用同一個稽核代碼寫，detail 加 <c>via=admin-assistant</c>（0.9.157 起；之前是 source=）。
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
    private readonly EmailTestService emailTest;
    private readonly NotificationTestService notificationTest;

    public MonitoringAssistantTools(
        ISystemHealthService health,
        ILogQueryService logs,
        LogLevelRuntimeState logLevel,
        ExceptionLogService exceptions,
        IDatabaseUsageService databaseUsage,
        IAuditLogService audit,
        CurrentUserService currentUser,
        EmailTestService emailTest,
        NotificationTestService notificationTest)
    {
        this.health = health;
        this.logs = logs;
        this.logLevel = logLevel;
        this.exceptions = exceptions;
        this.databaseUsage = databaseUsage;
        this.audit = audit;
        this.currentUser = currentUser;
        this.emailTest = emailTest;
        this.notificationTest = notificationTest;
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

    // ── 異動工具（0.9.158 起）：每一個都要使用者核准。日誌等級的稽核由這裡寫（同日誌等級頁，加 via）；測試信與測試通知由服務寫。──

    public static AiToolDefinition SetLogLevelTool { get; } = DefineMutating(
        "set_log_level", "調整日誌等級", MagicObjectHelper.角色_日誌等級設定, PermissionActions.Edit,
        "暫時調整系統日誌的最低等級（TRACE、DEBUG、INFO、WARN、ERROR、FATAL），只在執行期有效，重新啟動後回到 nlog.config 的設定。",
        "只在執行期有效（重新啟動後回到預設）。調成 TRACE／DEBUG 會大幅增加日誌量，排查完請記得調回來。",
        tools => tools.SetLogLevelAsync);

    public static AiToolDefinition RestoreLogLevelTool { get; } = DefineMutating(
        "restore_log_level", "日誌等級還原預設", MagicObjectHelper.角色_日誌等級設定, PermissionActions.Edit,
        "取消執行期的日誌等級覆寫，回到 nlog.config 的系統預設等級。",
        "回到 nlog.config 的系統預設等級。",
        tools => tools.RestoreLogLevelAsync);

    public static AiToolDefinition SendTestEmailTool { get; } = DefineMutating(
        "send_test_email", "寄送測試信", MagicObjectHelper.角色_系統健康監控, PermissionActions.Edit,
        "用目前的寄信設定寄一封測試信到指定的 Email，確認寄信功能正常。",
        "會真的寄出一封信（Pickup 模式只寫入資料夾）。稽核只記寄信方式與成敗，不記收件者。",
        tools => tools.SendTestEmailAsync);

    public static AiToolDefinition SendTestNotificationTool { get; } = DefineMutating(
        "send_test_notification", "發送測試通知", MagicObjectHelper.角色_系統健康監控, PermissionActions.Edit,
        "發一則測試通知給所有啟用中的管理員，確認右上角鈴鐺與即時更新正常。",
        "所有啟用中的管理員都會收到這則通知。",
        tools => tools.SendTestNotificationAsync);

    public static IReadOnlyList<AiToolDefinition> MutatingTools { get; } =
        [SetLogLevelTool, RestoreLogLevelTool, SendTestEmailTool, SendTestNotificationTool];

    // ── 不可復原的工具（0.9.159 起）：稽核同系統例外紀錄頁（頁面寫的），成功後才寫並加 via。──

    public static AiToolDefinition DeleteExceptionsTool { get; } = DefineIrreversible(
        "delete_exceptions", "刪除例外紀錄", MagicObjectHelper.角色_系統例外紀錄, PermissionActions.Delete,
        $"用例外編號（search_exceptions 回傳的 id）刪除例外紀錄與堆疊檔，一次最多 {AdminAssistantMutation.MaxBatch} 筆。",
        "例外紀錄與堆疊檔會被刪除，無法復原。相同的例外若再次發生，會重新出現並從 1 次開始計算。",
        tools => tools.DeleteExceptionsAsync);

    public static AiToolDefinition ClearExceptionsTool { get; } = DefineIrreversible(
        "clear_exceptions", "清空全部例外紀錄", MagicObjectHelper.角色_系統例外紀錄, PermissionActions.Delete,
        "刪除全部例外紀錄與整個堆疊檔目錄（只清除一段期間的請用 custom_purge 的 exception-log）。",
        "會刪除所有例外紀錄與整個堆疊檔目錄，無法復原。",
        tools => tools.ClearExceptionsAsync);

    public static IReadOnlyList<AiToolDefinition> IrreversibleTools { get; } = [DeleteExceptionsTool, ClearExceptionsTool];

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
        var (startTime, endTime, rangeNote) = ParseRange(start, end, DateTime.Now, TimeSpan.FromHours(24), LogQueryRequest.MaxSpan);
        var notes = new List<string>();
        var level = ParseLevel(minimumLevel);
        if (level == LogLevelRank.Any && string.IsNullOrWhiteSpace(minimumLevel) == false
            && string.Equals(minimumLevel.Trim(), "any", StringComparison.OrdinalIgnoreCase) == false)
        {
            notes.Add($"最低等級「{minimumLevel}」無法辨識（可用 trace、debug、info、warn、error、fatal），改為不限等級");
        }

        var request = new LogQueryRequest
        {
            StartTime = startTime,
            EndTime = endTime,
            Take = ClampWithNote(take, 1, MaxLogTake, "筆數", notes),
            MinimumLevel = level,
            Keyword = keyword?.Trim() ?? string.Empty,
            TraceCode = traceCode?.Trim() ?? string.Empty,
        };

        var result = await logs.QueryAsync(request, cancellationToken);

        await ViewAudit.WriteSearchAsync(audit, currentUser, AuditActions.LogViewer.Search, "LogQuery",
            AuditSearchCondition.Of("start", result.AppliedStartTime), AuditSearchCondition.Of("end", result.AppliedEndTime),
            AuditSearchCondition.Of("minimumLevel", level), AuditSearchCondition.Of("take", request.Take),
            AuditSearchCondition.Text("keyword", request.Keyword), AuditSearchCondition.Text("traceCode", request.TraceCode),
            AuditSearchCondition.Of("rows", result.Entries.Count), AuditSearchCondition.Of("via", AuditSource));

        return ToJson(new
        {
            appliedStart = Format(result.AppliedStartTime),
            appliedEnd = Format(result.AppliedEndTime),
            note = JoinNotes([rangeNote, .. notes]),
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

    public async Task<string> SetLogLevelAsync([Description("新的最低等級：TRACE、DEBUG、INFO、WARN、ERROR、FATAL")] string level)
    {
        var target = LogLevelRankHelper.FromLevelText(level?.Trim().ToUpperInvariant() ?? string.Empty, LogLevelRank.Unknown);
        if (LogLevelRankHelper.IsRealLevel(target) == false)
        {
            return $"等級「{level}」無法辨識，沒有變更。可用：TRACE、DEBUG、INFO、WARN、ERROR、FATAL。";
        }

        var previous = logLevel.EffectiveLevel;
        if (logLevel.OverrideLevel == target)
        {
            return $"目前已經是 {LogLevelRankHelper.ToLevelText(target)}，沒有變更。";
        }

        if (logLevel.Apply(target) == false)
        {
            return "套用失敗（NLog 設定無法使用），沒有變更。請查看日誌等級設定頁。";
        }

        await WriteLogLevelAuditAsync(AuditActions.LogLevel.Apply, previous, target);
        return $"已將日誌最低等級由 {LogLevelRankHelper.ToLevelText(previous)} 調整為 {LogLevelRankHelper.ToLevelText(target)}（只在執行期有效，重新啟動後回到預設）。";
    }

    public async Task<string> RestoreLogLevelAsync()
    {
        if (logLevel.OverrideLevel is null)
        {
            return $"目前沒有執行期覆寫，已經是系統預設等級 {LogLevelRankHelper.ToLevelText(logLevel.SystemDefaultLevel)}，沒有變更。";
        }

        var previous = logLevel.EffectiveLevel;
        if (logLevel.RestoreDefault() == false)
        {
            return "還原失敗（NLog 設定無法使用），沒有變更。請查看日誌等級設定頁。";
        }

        await WriteLogLevelAuditAsync(AuditActions.LogLevel.Restore, previous, logLevel.EffectiveLevel);
        return $"已還原為系統預設等級 {LogLevelRankHelper.ToLevelText(logLevel.EffectiveLevel)}。";
    }

    public async Task<string> SendTestEmailAsync([Description("收件者 Email")] string to, CancellationToken cancellationToken = default)
    {
        var user = currentUser.CurrentUser;
        var result = await emailTest.SendAsync(to, user.Id, user.Account, cancellationToken);
        return result.Success ? result.Message : $"沒有寄出：{result.Message}";
    }

    public async Task<string> SendTestNotificationAsync(CancellationToken cancellationToken = default)
    {
        var user = currentUser.CurrentUser;
        var result = await notificationTest.SendAsync(user.Id, user.Account, cancellationToken);
        return result.Success ? result.Message : $"沒有發送成功：{result.Message}";
    }

    /// <summary>同日誌等級設定頁的稽核（代碼、目標、文字都相同），detail 另加 <c>via=admin-assistant</c>。</summary>
    private Task WriteLogLevelAuditAsync(string action, LogLevelRank previous, LogLevelRank current)
    {
        var user = currentUser.CurrentUser;
        return audit.WriteAsync(
            action, success: true, actorUserId: user.Id, actorAccount: user.Account,
            targetType: "NLog", targetId: "logger:*",
            detail: $"日誌最低等級由 {LogLevelRankHelper.ToLevelText(previous)} 調整為 {LogLevelRankHelper.ToLevelText(current)}（僅執行期有效）。; via={AuditSource}");
    }

    public async Task<string> DeleteExceptionsAsync([Description("例外編號清單（search_exceptions 回傳的 id），最多 50 個")] int[] ids)
    {
        var (items, error) = AdminAssistantMutation.ValidateIds(ids);
        if (error is not null)
        {
            return error;
        }

        var result = new BatchResult("刪除例外紀錄");
        foreach (var id in items)
        {
            var item = await exceptions.GetByIdAsync(id);
            if (item is null)
            {
                result.Add($"#{id}", false, "找不到這筆例外紀錄");
                continue;
            }

            var outcome = await exceptions.DeleteAsync(id);
            if (outcome.Success)
            {
                await ViewAudit.WriteAsync(audit, currentUser, AuditActions.ExceptionLog.Delete, "ExceptionLog",
                    id.ToString(System.Globalization.CultureInfo.InvariantCulture), $"type={item.ShortExceptionType}; via={AuditSource}");
            }

            result.Add($"#{id} {item.ShortExceptionType}", outcome.Success, outcome.Success ? null : outcome.Message);
        }

        return ToJson(result.ToOutput());
    }

    public async Task<string> ClearExceptionsAsync()
    {
        var result = await exceptions.ClearAllAsync();
        if (result.Success == false)
        {
            return $"沒有清空：{result.Message}";
        }

        await ViewAudit.WriteAsync(audit, currentUser, AuditActions.ExceptionLog.ClearAll, "ExceptionLog", "*",
            $"清空全部例外紀錄：{result.Message}; via={AuditSource}");
        return "已清空全部例外紀錄與堆疊檔目錄。";
    }

    public async Task<string> SearchExceptionsAsync(
        [Description("起始時間（最後發生時間），" + TimeFormatHint + "；不填＝往前 7 天")] string? start = null,
        [Description("結束時間，" + TimeFormatHint + "；不填＝現在")] string? end = null,
        [Description("關鍵字（例外類型、訊息、頁面等）")] string? keyword = null,
        [Description("發生時的登入帳號")] string? account = null,
        [Description("最多回傳幾筆，1～50，預設 20")] int take = 20,
        [Description("沒給起始時間時，查結束時間往前幾小時（1～8760），例如巡檢用 24；不填＝往前 7 天")] int? hours = null)
    {
        var notes = new List<string>();
        var defaultSpan = hours is { } h ? TimeSpan.FromHours(ClampWithNote(h, 1, 8760, "小時數", notes)) : TimeSpan.FromDays(7);
        var (startTime, endTime, rangeNote) = ParseRange(start, end, DateTime.Now, defaultSpan, TimeSpan.FromDays(366));
        var query = new ExceptionLogQuery
        {
            StartTime = startTime,
            EndTime = endTime,
            Keyword = keyword?.Trim() ?? string.Empty,
            Account = account?.Trim() ?? string.Empty,
            CurrentPage = 1,
            PageSize = ClampWithNote(take, 1, MaxExceptionTake, "筆數", notes),
        };

        var result = await exceptions.GetAsync(query);

        await ViewAudit.WriteSearchAsync(audit, currentUser, AuditActions.ExceptionLog.Search, "ExceptionLog",
            AuditSearchCondition.Of("start", startTime), AuditSearchCondition.Of("end", endTime),
            AuditSearchCondition.Text("account", query.Account), AuditSearchCondition.Text("keyword", query.Keyword),
            AuditSearchCondition.Of("via", AuditSource));

        return ToJson(new
        {
            appliedStart = Format(startTime),
            appliedEnd = Format(endTime),
            note = JoinNotes([rangeNote, .. notes]),
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
            id.ToString(System.Globalization.CultureInfo.InvariantCulture), $"via={AuditSource}");

        return ToJson(new { id, stackTrace = Truncate(stackTrace, StackTraceMaxLength) });
    }

    /// <summary>資料庫用量。0.9.157 起不送出 <see cref="DatabaseUsageReport.DatabaseFilePath"/>（伺服器完整路徑），只給檔名。</summary>
    public async Task<string> GetDatabaseUsageAsync(CancellationToken cancellationToken = default)
    {
        var report = await databaseUsage.GetReportAsync(cancellationToken);
        report.DatabaseFilePath = string.Empty;
        return ToJson(report);
    }

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

    private static AiToolDefinition DefineIrreversible(
        string name, string displayName, string permissionPage, string permissionAction, string description, string approvalNote,
        Func<MonitoringAssistantTools, Delegate> method)
        => DefineMutating(name, displayName, permissionPage, permissionAction, description, approvalNote, method) with { IsIrreversible = true };

    private static AiToolDefinition DefineMutating(
        string name, string displayName, string permissionPage, string permissionAction, string description, string approvalNote,
        Func<MonitoringAssistantTools, Delegate> method)
        => Define(name, displayName, permissionPage, description, method) with
        {
            PermissionAction = permissionAction,
            IsMutating = true,
            ApprovalNote = approvalNote,
        };
}
