using Microsoft.Extensions.Logging.Abstractions;
using MyProject.Business.Services.Other;
using MyProject.Models.Others;
using MyProject.Models.Systems;
using MyProject.Web.Ai.Agents.AdminAssistant;
using MyProject.Web.Configuration;
using MyProject.Web.Diagnostics;
using MyProject.Web.Email;
using MyProject.Web.Health;
using NLog.Config;
using NLog.Targets;
using NLogLevel = NLog.LogLevel;
using NLogManager = NLog.LogManager;

namespace MyProject.Tests;

/// <summary>
/// 系統管理助理「監控與診斷」的異動工具（0.9.158 起）：日誌等級套用與還原（稽核同日誌等級頁，加 via）、
/// 測試信與測試通知（稽核由服務寫，工具不重複寫）。會動到行程層級的 NLog 設定，所以和日誌等級測試同一個序列化集合。
/// </summary>
[Collection(nameof(NLogConfigurationCollection))]
public sealed class MonitoringMutationToolsTests : IDisposable
{
    private readonly LoggingConfiguration? originalConfiguration = NLogManager.Configuration;
    private readonly LogLevelRuntimeState state = new(NullLogger<LogLevelRuntimeState>.Instance);
    private readonly RecordingAuditLogService audit = new();
    private readonly RecordingAuditLogService serviceAudit = new();
    private readonly CapturingEmailSender emailSender = new();
    private readonly RecordingNotificationSender notificationSender = new();
    private readonly CurrentUserService currentUser = new() { CurrentUser = new CurrentUser { Id = 7, Account = "admin", IsAdmin = true } };

    public MonitoringMutationToolsTests()
    {
        var configuration = new LoggingConfiguration();
        var target = new MemoryTarget("mem");
        configuration.AddTarget(target);
        configuration.LoggingRules.Add(new LoggingRule("*", NLogLevel.Info, target));
        NLogManager.Configuration = configuration;
        state.Initialize();
    }

    public void Dispose()
    {
        state.Dispose();
        NLogManager.Configuration = originalConfiguration;
    }

    [Fact]
    public async Task SetLogLevel_Debug_AppliesAndAuditsLikeThePage()
    {
        var text = await CreateTools().SetLogLevelAsync("debug");

        Assert.Equal(LogLevelRank.Debug, state.EffectiveLevel);
        Assert.Contains("DEBUG", text, StringComparison.Ordinal);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("LogLevel.Apply", entry.Action);
        Assert.Equal(7, entry.ActorUserId);
        Assert.Contains("由 INFO 調整為 DEBUG（僅執行期有效）", entry.Detail, StringComparison.Ordinal);
        Assert.Contains("via=admin-assistant", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetLogLevel_Unknown_RejectedWithoutChange()
    {
        var text = await CreateTools().SetLogLevelAsync("verbose");

        Assert.Contains("DEBUG", text, StringComparison.Ordinal);
        Assert.Equal(LogLevelRank.Info, state.EffectiveLevel);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task RestoreLogLevel_NoOverride_NothingToRestore()
    {
        var text = await CreateTools().RestoreLogLevelAsync();

        Assert.Contains("沒有", text, StringComparison.Ordinal);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task RestoreLogLevel_AfterApply_RestoresAndAudits()
    {
        var tools = CreateTools();
        await tools.SetLogLevelAsync("DEBUG");

        await tools.RestoreLogLevelAsync();

        Assert.Equal(LogLevelRank.Info, state.EffectiveLevel);
        Assert.Null(state.OverrideLevel);
        Assert.Equal("LogLevel.Restore", audit.Entries[^1].Action);
        Assert.Contains("由 DEBUG 調整為 INFO", audit.Entries[^1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendTestEmail_DelegatesToService_ToolWritesNoAudit()
    {
        var text = await CreateTools().SendTestEmailAsync("ops@example.com");

        Assert.Contains("Pickup", text, StringComparison.Ordinal);
        Assert.Equal("ops@example.com", Assert.Single(emailSender.Sent).To);
        Assert.Equal(7, Assert.Single(serviceAudit.Entries).ActorUserId);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task SendTestNotification_DelegatesToService()
    {
        var text = await CreateTools().SendTestNotificationAsync();

        Assert.Contains("1 位", text, StringComparison.Ordinal);
        Assert.Single(notificationSender.Requests);
        Assert.Equal("Notification.Test", Assert.Single(serviceAudit.Entries).Action);
        Assert.Empty(audit.Entries);
    }

    private MonitoringAssistantTools CreateTools()
    {
        var emailTest = new EmailTestService(
            emailSender,
            serviceAudit,
            new StaticOptionsMonitor<EmailSettings>(new EmailSettings { Provider = "Pickup", Host = "smtp.example.com", FromAddress = "noreply@example.com" }),
            new SystemIdentity(new StaticOptionsMonitor<SystemSettings>(new SystemSettings())),
            NullLogger<EmailTestService>.Instance);
        var notificationTest = new NotificationTestService(notificationSender, serviceAudit, TimeProvider.System, NullLogger<NotificationTestService>.Instance);
        return new MonitoringAssistantTools(null!, null!, state, null!, null!, audit, currentUser, emailTest, notificationTest);
    }
}
