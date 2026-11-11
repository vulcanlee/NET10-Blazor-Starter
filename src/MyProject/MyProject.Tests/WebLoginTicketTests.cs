using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using MyProject.Models.Systems;
using MyProject.Web.Auth;

namespace MyProject.Tests;

/// <summary>
/// 網頁以 Token 登入用的三種一次性憑證（0.10.8 起，WASM 遷移 M9a）：兩步驟驗證待驗票證與記住裝置 Token、設定票證、Google 一次性代碼。
/// 以可推進的時鐘驗證過期，其餘（成功一次後作廢、綁定使用者與版本、綁定瀏覽器）也在這裡釘住。
/// </summary>
public sealed class WebLoginTicketTests
{
    private readonly ManualTimeProvider time = new(new DateTimeOffset(2026, 11, 11, 1, 0, 0, TimeSpan.Zero));
    private readonly EphemeralDataProtectionProvider protection = new();

    [Fact]
    public void TwoFactorTicket_ShouldCarryTheLogin_UntilItIsConsumedOrExpires()
    {
        var tickets = TwoFactorTickets();
        var ticket = tickets.Issue(7, "stamp-1", rememberMe: true, "/projects?x=1", "Google");

        var read = tickets.Read(ticket)!;
        Assert.Equal(7, read.UserId);
        Assert.Equal("stamp-1", read.SecurityStamp);
        Assert.True(read.RememberMe);
        Assert.Equal("/projects?x=1", read.ReturnUrl);
        Assert.Equal("Google", read.Provider);

        // 讀不會作廢（驗證碼錯了可以再試）；作廢之後就讀不到。
        Assert.NotNull(tickets.Read(ticket));
        Assert.True(tickets.TryConsume(read));
        Assert.False(tickets.TryConsume(read));
        Assert.Null(tickets.Read(ticket));

        var expiring = tickets.Issue(7, "stamp-1", rememberMe: false, "/App", null);
        time.Advance(TwoFactorLoginTickets.PendingLifetime + TimeSpan.FromSeconds(1));
        Assert.Null(tickets.Read(expiring));
        Assert.Null(tickets.Read("not-a-ticket"));
    }

    [Fact]
    public void RememberDeviceToken_ShouldBeBoundToTheUserAndTheSessionVersion_AndExpire()
    {
        var tickets = TwoFactorTickets(rememberDeviceDays: 30);
        var token = tickets.IssueDeviceToken(7, "stamp-1");

        Assert.True(tickets.IsDeviceRemembered(token, 7, "stamp-1"));
        Assert.False(tickets.IsDeviceRemembered(token, 8, "stamp-1"));
        Assert.False(tickets.IsDeviceRemembered(token, 7, "stamp-2"));

        time.Advance(TimeSpan.FromDays(31));
        Assert.False(tickets.IsDeviceRemembered(token, 7, "stamp-1"));
    }

    [Fact]
    public void RememberDevice_WhenDisabled_ShouldIssueNothing()
    {
        var tickets = TwoFactorTickets(rememberDeviceDays: 0);

        Assert.Null(tickets.IssueDeviceToken(7, "stamp-1"));
    }

    [Fact]
    public void SetupTicket_ShouldBeBoundToItsUser_SingleUse_AndExpire()
    {
        var tickets = new TwoFactorSetupTickets(protection, time, NullLogger<TwoFactorSetupTickets>.Instance);
        var ticket = tickets.Issue(7, "SECRET");

        Assert.Null(tickets.Read(ticket, 8));
        var read = tickets.Read(ticket, 7)!.Value;
        Assert.Equal("SECRET", read.Secret);
        Assert.True(tickets.TryConsume(read.Id));
        Assert.Null(tickets.Read(ticket, 7));

        var expiring = tickets.Issue(7, "SECRET");
        time.Advance(TwoFactorSetupTickets.Lifetime + TimeSpan.FromSeconds(1));
        Assert.Null(tickets.Read(expiring, 7));
    }

    [Fact]
    public void GoogleCode_ShouldOnlyBeRedeemedOnce_ByTheBrowserThatStartedTheLogin()
    {
        var codes = new GoogleLoginCodes(time, NullLogger<GoogleLoginCodes>.Instance);
        var grant = new GoogleLoginGrant(7, "stamp-1", "/App", RequiresSecondFactor: false, GoogleLoginCodes.HashVerifier("v-1"));

        var code = codes.Issue(grant);
        Assert.Equal(grant, codes.Redeem(code, "v-1"));
        Assert.Null(codes.Redeem(code, "v-1"));

        // 別的瀏覽器出示：拒絕，而且代碼就此作廢。
        var other = codes.Issue(grant);
        Assert.Null(codes.Redeem(other, "v-2"));
        Assert.Null(codes.Redeem(other, "v-1"));

        var expiring = codes.Issue(grant);
        time.Advance(GoogleLoginCodes.Lifetime + TimeSpan.FromSeconds(1));
        Assert.Null(codes.Redeem(expiring, "v-1"));
    }

    [Theory]
    [InlineData("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ", true)]
    [InlineData("abc", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNO=Q", false)]
    [InlineData(null, false)]
    public void BindingHash_ShouldBeABase64UrlSha256(string? value, bool expected)
        => Assert.Equal(expected, GoogleLoginCodes.IsValidBindingHash(value));

    private TwoFactorLoginTickets TwoFactorTickets(int rememberDeviceDays = 30)
        => new(protection, new StaticOptionsMonitor<TwoFactorSettings>(new TwoFactorSettings { RememberDeviceDays = rememberDeviceDays }), time, NullLogger<TwoFactorLoginTickets>.Instance);
}
