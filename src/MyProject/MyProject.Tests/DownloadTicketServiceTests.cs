using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using MyProject.Web.Controllers;
using MyProject.Web.Downloads;

namespace MyProject.Tests;

/// <summary>一次性下載票證（0.10.3 起，WASM 遷移 M4）：不可猜、60 秒內有效、只能用一次。</summary>
public sealed class DownloadTicketServiceTests
{
    [Fact]
    public void Ticket_ShouldCarryWhatWasIssued_AndWorkOnlyOnce()
    {
        var service = new DownloadTicketService(new ManualTimeProvider(DateTimeOffset.UtcNow));

        var ticket = service.Issue(DownloadTicketService.ProjectFileKind, 42, 7, "stamp");
        var redeemed = service.TryRedeem(ticket);

        Assert.NotNull(redeemed);
        Assert.Equal((DownloadTicketService.ProjectFileKind, "42", 7, "stamp"), (redeemed.Kind, redeemed.Target, redeemed.UserId, redeemed.SecurityStamp));
        Assert.Null(service.TryRedeem(ticket));
    }

    [Fact]
    public void Ticket_ShouldExpireAfterSixtySeconds()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = new DownloadTicketService(clock);
        var fresh = service.Issue(DownloadTicketService.ProjectFileKind, 1, 1, null);
        var stale = service.Issue(DownloadTicketService.ProjectFileKind, 2, 1, null);

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.NotNull(service.TryRedeem(fresh));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(service.TryRedeem(stale));
    }

    [Fact]
    public void Tickets_ShouldBeUnguessable_AndUrlSafe()
    {
        var service = new DownloadTicketService(new ManualTimeProvider(DateTimeOffset.UtcNow));

        var tickets = Enumerable.Range(0, 200).Select(_ => service.Issue(DownloadTicketService.ProjectFileKind, 1, 1, null)).ToList();

        Assert.Equal(tickets.Count, tickets.Distinct().Count());
        Assert.All(tickets, t =>
        {
            Assert.True(t.Length >= 43, "32 位元組亂數的 Base64 至少 43 個字元");
            Assert.Matches("^[A-Za-z0-9_-]+$", t);
        });
    }

    [Fact]
    public async Task RedeemingTheSameTicketConcurrently_ShouldSucceedOnlyOnce()
    {
        var service = new DownloadTicketService(new ManualTimeProvider(DateTimeOffset.UtcNow));
        var ticket = service.Issue(DownloadTicketService.ProjectFileKind, 1, 1, null);

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => service.TryRedeem(ticket))));

        Assert.Single(results, x => x is not null);
    }

    [Fact]
    public void UnusedExpiredTickets_ShouldBeRemovedWhenTheNextOneIsIssued()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = new DownloadTicketService(clock);
        for (var i = 0; i < 50; i++)
        {
            service.Issue(DownloadTicketService.ProjectFileKind, i, 1, null);
        }

        clock.Advance(TimeSpan.FromMinutes(2));
        service.Issue(DownloadTicketService.ProjectFileKind, 99, 1, null);

        var store = typeof(DownloadTicketService).GetField("tickets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
        Assert.Equal(1, (int)store.GetType().GetProperty("Count")!.GetValue(store)!);
    }

    /// <summary>附件最大 1 GB：帶附件的新增、修改要放寬請求大小（預設約 30 MB，大檔會被伺服器直接擋掉）。</summary>
    [Theory]
    [InlineData(nameof(ProjectController.CreateWithFiles))]
    [InlineData(nameof(ProjectController.UpdateWithFiles))]
    public void MultipartActions_ShouldRaiseTheRequestSizeLimits(string action)
    {
        var method = typeof(ProjectController).GetMethod(action)!;

        Assert.NotNull(method.GetCustomAttribute<RequestSizeLimitAttribute>());
        Assert.Equal(ProjectController.MaxRequestBytes, method.GetCustomAttribute<RequestFormLimitsAttribute>()!.MultipartBodyLengthLimit);
        Assert.Contains("multipart/form-data", method.GetCustomAttribute<ConsumesAttribute>()!.ContentTypes);
    }
}
