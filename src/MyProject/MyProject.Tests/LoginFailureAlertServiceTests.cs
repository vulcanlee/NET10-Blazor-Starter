using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;

namespace MyProject.Tests;

/// <summary>
/// 登入失敗告警（0.9.166）：全系統在時間窗內的登入失敗次數達門檻時，通知全體管理員並寄信。
/// 同一帳號連續輸錯由帳號鎖定處理（<c>MyUserServiceLogin.NotifyLockedAsync</c>），這裡抓的是橫向猜測：
/// 不存在的帳號、多個帳號各試幾次、多個 IP 慢慢試。
/// </summary>
public sealed class LoginFailureAlertServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection connection;
    private readonly TestDbContextFactory factory;
    private readonly LoginFailureAlertSettings settings = new() { Threshold = 3, WindowMinutes = 10, CooldownMinutes = 60 };
    private readonly RecordingNotificationSender notifications = new();
    private readonly ManualTimeProvider clock = new(Now);
    private readonly LoginFailureAlertState state = new();

    public LoginFailureAlertServiceTests()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        factory = new TestDbContextFactory(connection);
        using var context = factory.CreateDbContext();
        context.Database.EnsureCreated();
    }

    public void Dispose() => connection.Dispose();

    private LoginFailureAlertService Service(INotificationSender? sender = null)
        => new(factory, new StaticOptionsMonitor<LoginFailureAlertSettings>(settings), sender ?? notifications, state, clock,
            NullLogger<LoginFailureAlertService>.Instance);

    private void Audit(string action, string? account = "guess", string? ip = "203.0.113.5", TimeSpan? ago = null)
    {
        using var context = factory.CreateDbContext();
        context.AuditLog.Add(new AuditLog
        {
            OccurredAt = clock.GetUtcNow().UtcDateTime - (ago ?? TimeSpan.FromMinutes(1)),
            Action = action,
            Success = false,
            ActorAccount = account,
            ClientIp = ip,
        });
        context.SaveChanges();
    }

    private void Failures(int count, string account = "guess", string ip = "203.0.113.5")
    {
        for (var i = 0; i < count; i++)
        {
            Audit(AuditActions.Login.Failed, account, ip);
        }
    }

    [Fact]
    public async Task BelowThreshold_DoesNotNotify()
    {
        Failures(2);

        await Service().EvaluateAsync();

        Assert.Empty(notifications.Requests);
    }

    [Fact]
    public async Task ReachingThreshold_NotifiesAllAdminsByEmail_WithTopAccountsAndIps()
    {
        Failures(2, "admin", "203.0.113.5");
        Audit(AuditActions.Login.TwoFactorFailed, "alice", "198.51.100.7");

        await Service().EvaluateAsync();

        var request = Assert.Single(notifications.Requests);
        Assert.Equal(NotificationCategories.LoginFailureBurst, request.Category);
        Assert.True(request.Target.Admins);
        Assert.True(request.AlsoEmail);
        Assert.Equal("/audit-logs", request.Link);
        Assert.Contains("10 分鐘內 3 次", request.Title, StringComparison.Ordinal);
        Assert.Contains("admin（2 次）", request.Body, StringComparison.Ordinal);
        Assert.Contains("alice（1 次）", request.Body, StringComparison.Ordinal);
        Assert.Contains("203.0.113.5（2 次）", request.Body, StringComparison.Ordinal);
        Assert.Contains("198.51.100.7（1 次）", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailuresOutsideWindow_AreNotCounted()
    {
        Failures(2);
        Audit(AuditActions.Login.Failed, ago: TimeSpan.FromMinutes(11));

        await Service().EvaluateAsync();

        Assert.Empty(notifications.Requests);
    }

    [Fact]
    public async Task OnlyLoginFailureActionsCount()
    {
        Audit(AuditActions.Login.Failed);
        Audit(AuditActions.Login.Success);
        Audit(AuditActions.Login.Disabled);
        Audit(AuditActions.Permission.Denied);

        await Service().EvaluateAsync();
        Assert.Empty(notifications.Requests);

        Audit(AuditActions.Login.LockedOut);
        Audit(AuditActions.Login.SsoFailed, account: null);

        await Service().EvaluateAsync();
        Assert.Single(notifications.Requests);
    }

    [Fact]
    public async Task WithinCooldown_DoesNotRepeat_AndAlertsAgainAfterCooldown()
    {
        Failures(3);
        await Service().EvaluateAsync();

        clock.Advance(TimeSpan.FromMinutes(30));
        Failures(3);
        await Service().EvaluateAsync();
        Assert.Single(notifications.Requests);

        clock.Advance(TimeSpan.FromMinutes(31));
        Failures(3);
        await Service().EvaluateAsync();
        Assert.Equal(2, notifications.Requests.Count);
    }

    [Fact]
    public async Task ZeroThreshold_DisablesAlert()
    {
        settings.Threshold = 0;
        Failures(5);

        await Service().EvaluateAsync();

        Assert.Empty(notifications.Requests);
    }

    [Fact]
    public async Task LongAccountText_IsTruncated()
    {
        var typed = new string('x', 200);
        Failures(3, typed);

        await Service().EvaluateAsync();

        var request = Assert.Single(notifications.Requests);
        Assert.DoesNotContain(typed, request.Body, StringComparison.Ordinal);
        Assert.Contains(new string('x', LoginFailureAlertService.MaxLabelLength) + "…", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SenderThrows_EvaluateDoesNotThrow()
    {
        Failures(3);

        var exception = await Record.ExceptionAsync(() => Service(new ThrowingSender()).EvaluateAsync());

        Assert.Null(exception);
    }

    [Fact]
    public async Task SendFailed_DoesNotStartCooldown()
    {
        Failures(3);
        var failing = new FailedSender();
        await Service(failing).EvaluateAsync();

        Failures(1);
        await Service().EvaluateAsync();

        Assert.Equal(1, failing.Calls);
        Assert.Single(notifications.Requests);
    }

    [Fact]
    public async Task FullIpv6Address_IsNotTruncated()
    {
        const string ipv6 = "2001:0db8:85a3:1234:5678:8a2e:0370:7334";
        Failures(3, ip: ipv6);

        await Service().EvaluateAsync();

        Assert.Contains(ipv6 + "（3 次）", Assert.Single(notifications.Requests).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidSettings_EvaluateDoesNotThrow()
    {
        Failures(3);
        var service = new LoginFailureAlertService(factory, new ThrowingOptions(), notifications, state, clock,
            NullLogger<LoginFailureAlertService>.Instance);

        var exception = await Record.ExceptionAsync(service.EvaluateAsync);

        Assert.Null(exception);
        Assert.Empty(notifications.Requests);
    }

    private sealed class FailedSender : INotificationSender
    {
        public int Calls { get; private set; }

        public Task<NotificationSendResult> SendAsync(NotificationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new NotificationSendResult(0, 0, Failed: true));
        }
    }

    private sealed class ThrowingOptions : Microsoft.Extensions.Options.IOptionsMonitor<LoginFailureAlertSettings>
    {
        public LoginFailureAlertSettings CurrentValue => throw new InvalidOperationException("invalid settings");

        public LoginFailureAlertSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<LoginFailureAlertSettings, string?> listener) => null;
    }

    private sealed class ThrowingSender : INotificationSender
    {
        public Task<NotificationSendResult> SendAsync(NotificationRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
    }
}
