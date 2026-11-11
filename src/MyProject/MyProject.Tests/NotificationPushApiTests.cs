using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.Other;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Dtos.Shell;
using MyProject.Share.Helpers;
using MyProject.Web.Streaming;

namespace MyProject.Tests;

/// <summary>
/// 通知推播串流（0.10.7 起，WASM 遷移 M8；<c>POST /api/v1/notifications/stream</c>）：用真的主機，以 API 登入取得的存取 Token 連線。
/// 送通知後立即推 notifications、公告變動推 announcements、別人的通知不推給我、斷線後訂閱與連線名額都釋放、
/// Token 到期前與連線最長時間到時伺服器主動結束、閒置時有心跳、每位使用者的連線數有上限、沒登入 401。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class NotificationPushApiTests : IClassFixture<ApiTestApplicationFactory>
{
    /// <summary>推播「立即」到達的上限：實機要求 2 秒內，測試主機可能較慢，放寬到 5 秒（仍遠小於 60 秒輪詢）。</summary>
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(5);

    private readonly ApiTestApplicationFactory factory;

    public NotificationPushApiTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    private NotificationPushOptions Options => factory.Services.GetRequiredService<NotificationPushOptions>();

    private NotificationSignal Signal => (NotificationSignal)factory.Services.GetRequiredService<INotificationSignal>();

    [Fact]
    public async Task Stream_WithoutAToken_ShouldReturnApiResult401()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/v1/notifications/stream", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<ApiResult<object>>())!.Success);
    }

    [Fact]
    public async Task Stream_ShouldStartWithReady_AsNdjson()
    {
        var user = await AddUserAsync();
        using var client = await SignedInClientAsync(user);

        await using var stream = await PushStream.OpenAsync(client);

        Assert.Equal(HttpStatusCode.OK, stream.Response.StatusCode);
        Assert.Equal("application/x-ndjson", stream.Response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(PushFrameKinds.Ready, await stream.NextAsync(Arrival));
    }

    [Fact]
    public async Task SendingANotification_ShouldPushNotificationsImmediately()
    {
        var user = await AddUserAsync();
        using var client = await SignedInClientAsync(user);
        await using var stream = await PushStream.OpenAsync(client);
        Assert.Equal(PushFrameKinds.Ready, await stream.NextAsync(Arrival));

        await SendNotificationAsync(user.Id);

        Assert.Equal(PushFrameKinds.Notifications, await stream.NextAsync(Arrival));
    }

    [Fact]
    public async Task MarkingAllRead_ShouldAlsoPushNotifications_SoOtherTabsUpdateTheBell()
    {
        var user = await AddUserAsync();
        await SendNotificationAsync(user.Id);
        using var client = await SignedInClientAsync(user);
        await using var stream = await PushStream.OpenAsync(client);
        Assert.Equal(PushFrameKinds.Ready, await stream.NextAsync(Arrival));

        using var otherTab = await SignedInClientAsync(user);
        Assert.Equal(HttpStatusCode.OK, (await otherTab.PostAsync("/api/v1/notifications/read-all", null)).StatusCode);

        Assert.Equal(PushFrameKinds.Notifications, await stream.NextAsync(Arrival));
    }

    [Fact]
    public async Task AnnouncementChanges_ShouldPushAnnouncements()
    {
        var user = await AddUserAsync();
        using var client = await SignedInClientAsync(user);
        await using var stream = await PushStream.OpenAsync(client);
        Assert.Equal(PushFrameKinds.Ready, await stream.NextAsync(Arrival));

        Signal.PublishAnnouncementsChanged();

        Assert.Equal(PushFrameKinds.Announcements, await stream.NextAsync(Arrival));
    }

    [Fact]
    public async Task AnotherUsersNotifications_ShouldNotBePushedToMe()
    {
        var me = await AddUserAsync();
        var someoneElse = await AddUserAsync();
        using var client = await SignedInClientAsync(me);
        await using var stream = await PushStream.OpenAsync(client);
        Assert.Equal(PushFrameKinds.Ready, await stream.NextAsync(Arrival));

        await SendNotificationAsync(someoneElse.Id);
        Assert.Null(await stream.NextAsync(TimeSpan.FromMilliseconds(800)));

        await SendNotificationAsync(me.Id);
        Assert.Equal(PushFrameKinds.Notifications, await stream.NextAsync(Arrival));
    }

    [Fact]
    public async Task Disconnecting_ShouldReleaseTheSubscriptionsAndTheConnectionSlot()
    {
        var user = await AddUserAsync();
        using var client = await SignedInClientAsync(user);
        var baseline = Signal.SubscriberCount;
        var connections = factory.Services.GetRequiredService<NotificationPushConnections>();

        var stream = await PushStream.OpenAsync(client);
        Assert.Equal(PushFrameKinds.Ready, await stream.NextAsync(Arrival));
        Assert.Equal(baseline + 2, Signal.SubscriberCount);
        Assert.Equal(1, connections.CountOf(user.Id));

        await stream.DisposeAsync();

        await WaitUntilAsync(() => Signal.SubscriberCount == baseline && connections.CountOf(user.Id) == 0);
    }

    [Fact]
    public async Task Stream_ShouldEndBeforeTheAccessTokenExpires_AskingTheClientToRenew()
    {
        var user = await AddUserAsync();
        using var client = await SignedInClientAsync(user);
        var baseline = Signal.SubscriberCount;
        var original = Options.RenewBeforeExpiry;

        // 測試主機的存取 Token 是 30 分鐘：提早量設成「30 分鐘減 1.5 秒」，等於 Token 在 1.5 秒後就算快到期。
        Options.RenewBeforeExpiry = TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(1.5);
        try
        {
            await using var stream = await PushStream.OpenAsync(client);
            Assert.Equal(PushFrameKinds.Ready, await stream.NextAsync(Arrival));

            Assert.Equal(PushFrameKinds.Renew, await stream.NextAsync(Arrival));
            Assert.True(await stream.EndedAsync(Arrival));
        }
        finally
        {
            Options.RenewBeforeExpiry = original;
        }

        await WaitUntilAsync(() => Signal.SubscriberCount == baseline);
    }

    [Fact]
    public async Task Stream_ShouldEndAfterTheMaximumDuration_AskingTheClientToReconnect()
    {
        var user = await AddUserAsync();
        using var client = await SignedInClientAsync(user);
        var original = Options.MaxDuration;
        Options.MaxDuration = TimeSpan.FromSeconds(1);
        try
        {
            await using var stream = await PushStream.OpenAsync(client);
            Assert.Equal(PushFrameKinds.Ready, await stream.NextAsync(Arrival));

            Assert.Equal(PushFrameKinds.Reconnect, await stream.NextAsync(Arrival));
            Assert.True(await stream.EndedAsync(Arrival));
        }
        finally
        {
            Options.MaxDuration = original;
        }
    }

    [Fact]
    public async Task IdleStream_ShouldSendHeartbeats()
    {
        var user = await AddUserAsync();
        using var client = await SignedInClientAsync(user);
        var original = Options.HeartbeatInterval;
        Options.HeartbeatInterval = TimeSpan.FromMilliseconds(200);
        try
        {
            await using var stream = await PushStream.OpenAsync(client);
            Assert.Equal(PushFrameKinds.Ready, await stream.NextAsync(Arrival));

            Assert.Equal(PushFrameKinds.Ping, await stream.NextAsync(Arrival, skipPings: false));
            Assert.Equal(PushFrameKinds.Ping, await stream.NextAsync(Arrival, skipPings: false));
        }
        finally
        {
            Options.HeartbeatInterval = original;
        }
    }

    [Fact]
    public async Task ConnectionsPerUser_ShouldBeLimited_AndFreedWhenOneCloses()
    {
        var user = await AddUserAsync();
        var other = await AddUserAsync();
        using var client = await SignedInClientAsync(user);
        using var otherClient = await SignedInClientAsync(other);
        var streams = new List<PushStream>();
        try
        {
            for (var i = 0; i < Options.MaxConnectionsPerUser; i++)
            {
                var opened = await PushStream.OpenAsync(client);
                streams.Add(opened);
                Assert.Equal(PushFrameKinds.Ready, await opened.NextAsync(Arrival));
            }

            var rejected = await client.PostAsync("/api/v1/notifications/stream", null);
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.False((await rejected.Content.ReadFromJsonAsync<ApiResult<object>>())!.Success);

            // 名額是每位使用者各自計算。
            await using (var others = await PushStream.OpenAsync(otherClient))
            {
                Assert.Equal(PushFrameKinds.Ready, await others.NextAsync(Arrival));
            }

            await streams[0].DisposeAsync();
            streams.RemoveAt(0);
            var connections = factory.Services.GetRequiredService<NotificationPushConnections>();
            await WaitUntilAsync(() => connections.CountOf(user.Id) < Options.MaxConnectionsPerUser);

            var reopened = await PushStream.OpenAsync(client);
            streams.Add(reopened);
            Assert.Equal(PushFrameKinds.Ready, await reopened.NextAsync(Arrival));
        }
        finally
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task ShellInfo_ShouldSayPushIsEnabled_ByDefault()
    {
        using var client = await SignedInClientAsync(await AddUserAsync());

        var info = (await client.GetFromJsonAsync<ApiResult<ShellInfoDto>>("/api/v1/shell/info"))!.Data!;

        Assert.True(info.NotificationPushEnabled);
    }

    [Theory]
    [InlineData(10, 60, 1, 10, false)] // 最長時間先到：時間到要重新連線
    [InlineData(30, 20, 1, 19, true)] // Token 先到期：提早 1 分鐘結束、請前端刷新
    [InlineData(30, 0.5, 1, 0, true)] // Token 已在提早量之內：立刻結束、請前端刷新
    public void ResolveLifetime_ShouldTakeTheEarlierOfTheMaximumAndTheTokenExpiry(
        double maxMinutes, double tokenMinutesLeft, double renewAheadMinutes, double expectedMinutes, bool expectedRenew)
    {
        var now = new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);
        var options = new NotificationPushOptions
        {
            MaxDuration = TimeSpan.FromMinutes(maxMinutes),
            RenewBeforeExpiry = TimeSpan.FromMinutes(renewAheadMinutes),
        };

        var (lifetime, renew) = NotificationPushStream.ResolveLifetime(now, now.AddMinutes(tokenMinutesLeft), options);

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), lifetime);
        Assert.Equal(expectedRenew, renew);
    }

    [Fact]
    public void ResolveLifetime_WithoutAnExpiry_ShouldUseTheMaximumDuration()
    {
        var options = new NotificationPushOptions { MaxDuration = TimeSpan.FromMinutes(30) };

        Assert.Equal((TimeSpan.FromMinutes(30), false), NotificationPushStream.ResolveLifetime(DateTimeOffset.UtcNow, null, options));
    }

    [Fact]
    public async Task WhenTheServerIsStopping_ShouldEndWithReconnect_AndReleaseTheSubscriptions()
    {
        var signal = new NotificationSignal(Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationSignal>.Instance);
        using var stopping = new CancellationTokenSource();
        var kinds = new List<string>();

        await foreach (var frame in NotificationPushStream.ProduceAsync(signal, 7, TimeSpan.FromMinutes(10), renew: true, TimeProvider.System, stopping.Token, CancellationToken.None))
        {
            kinds.Add(frame.Kind);
            Assert.Equal(2, signal.SubscriberCount);
            if (frame.Kind == PushFrameKinds.Ready)
            {
                await stopping.CancelAsync();
            }
        }

        Assert.Equal(new[] { PushFrameKinds.Ready, PushFrameKinds.Reconnect }, kinds);
        Assert.Equal(0, signal.SubscriberCount);
    }

    private async Task SendNotificationAsync(int userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<INotificationSender>();
        await sender.SendAsync(new NotificationRequest("System", "推播測試", null, null, NotificationTarget.Users(userId)));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "等待逾時：條件一直沒有成立。");
            await Task.Delay(50);
        }
    }

    private async Task<HttpClient> SignedInClientAsync(MyUser user)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/Auth/login", new LoginRequestDto { Account = user.Account, Password = "Passw0rd-1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var tokens = (await login.Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())!.Data!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private async Task<MyUser> AddUserAsync()
    {
        await using var context = await factory.Services.GetRequiredService<IDbContextFactory<BackendDBContext>>().CreateDbContextAsync();
        var role = await context.RoleView.FirstAsync(x => x.Name == MagicObjectHelper.預設角色);
        var account = $"push-{Guid.NewGuid():N}";
        var user = new MyUser
        {
            Account = account,
            Name = "Push " + account[^6..],
            Password = SecurePasswordHasher.HashPassword("Passw0rd-1"),
            Status = true,
            RoleViewId = role.Id,
            PasswordChangedAtUtc = DateTime.UtcNow,
        };
        context.MyUser.Add(user);
        await context.SaveChangesAsync();
        return user;
    }
}

/// <summary>關閉推播（<c>NotificationSettings:PushEnabled=false</c>）的測試主機。</summary>
public sealed class ApiTestApplicationFactoryWithPushDisabled : ApiTestApplicationFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // 註冊順序在 base 之後，因此覆寫得掉 base 加進去的設定。
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NotificationSettings:PushEnabled"] = "false",
            });
        });
    }
}

/// <summary>關閉推播時：外框資訊說「沒開」，串流端點回 404（前端只靠 60 秒輪詢）。</summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class NotificationPushDisabledTests : IClassFixture<ApiTestApplicationFactoryWithPushDisabled>
{
    private readonly ApiTestApplicationFactoryWithPushDisabled factory;

    public NotificationPushDisabledTests(ApiTestApplicationFactoryWithPushDisabled factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task WhenPushIsDisabled_ShellInfoShouldSaySo_AndTheStreamShouldBeRejected()
    {
        using var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/Auth/login", new LoginRequestDto { Account = "support", Password = "support" });
        var tokens = (await login.Content.ReadFromJsonAsync<ApiResult<TokenResponseDto>>())!.Data!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var info = (await client.GetFromJsonAsync<ApiResult<ShellInfoDto>>("/api/v1/shell/info"))!.Data!;
        var stream = await client.PostAsync("/api/v1/notifications/stream", null);

        Assert.False(info.NotificationPushEnabled);
        Assert.Equal(HttpStatusCode.NotFound, stream.StatusCode);
        Assert.False((await stream.Content.ReadFromJsonAsync<ApiResult<object>>())!.Success);
    }
}

/// <summary>測試用的推播連線：背景把每一行讀進佇列，測試以逾時取下一筆（不會讓 StreamReader 卡在被取消的讀取）。</summary>
internal sealed class PushStream : IAsyncDisposable
{
    private readonly CancellationTokenSource cancellation;
    private readonly Channel<string?> frames = Channel.CreateUnbounded<string?>();
    private readonly Task pump;

    private PushStream(HttpResponseMessage response, Stream body, CancellationTokenSource cancellation)
    {
        Response = response;
        this.cancellation = cancellation;
        pump = PumpAsync(body);
    }

    public HttpResponseMessage Response { get; }

    public static async Task<PushStream> OpenAsync(HttpClient client)
    {
        var cancellation = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/notifications/stream");
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new PushStream(response, await response.Content.ReadAsStreamAsync(cancellation.Token), cancellation);
    }

    /// <summary>下一筆的種類；逾時回 null。預設略過心跳。</summary>
    public async Task<string?> NextAsync(TimeSpan timeout, bool skipPings = true)
    {
        using var wait = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var line = await frames.Reader.ReadAsync(wait.Token);
                Assert.NotNull(line); // null＝串流已結束
                var kind = JsonSerializer.Deserialize<PushFrameDto>(line, JsonSerializerOptions.Web)!.Kind;
                if (!skipPings || kind != PushFrameKinds.Ping)
                {
                    return kind;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>伺服器結束串流（之後沒有任何一筆，連心跳也沒有）。</summary>
    public async Task<bool> EndedAsync(TimeSpan timeout)
    {
        using var wait = new CancellationTokenSource(timeout);
        try
        {
            while (true)
            {
                var line = await frames.Reader.ReadAsync(wait.Token);
                if (line is null)
                {
                    return true;
                }

                Assert.Equal(PushFrameKinds.Ping, JsonSerializer.Deserialize<PushFrameDto>(line, JsonSerializerOptions.Web)!.Kind);
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await cancellation.CancelAsync();
        Response.Dispose();
        try
        {
            await pump;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException)
        {
            // 自己中斷連線，讀取端收到取消是預期的。
        }

        cancellation.Dispose();
    }

    private async Task PumpAsync(Stream body)
    {
        using var reader = new StreamReader(body);
        try
        {
            while (await reader.ReadLineAsync(cancellation.Token) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    await frames.Writer.WriteAsync(line);
                }
            }

            await frames.Writer.WriteAsync(null);
        }
        finally
        {
            frames.Writer.TryComplete();
        }
    }
}
