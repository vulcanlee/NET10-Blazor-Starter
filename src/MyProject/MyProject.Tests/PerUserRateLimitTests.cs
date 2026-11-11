using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;
using MyProject.Share.Helpers;

namespace MyProject.Tests;

/// <summary>一般 API 配額壓到 3 的測試主機（登入另有自己的配額，不受影響）。</summary>
public sealed class ApiTestApplicationFactoryWithTightApiLimit : ApiTestApplicationFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RateLimit:ApiRequestsPerMinute"] = "3",
            });
        });
    }
}

/// <summary>
/// 已登入的請求依「每人」分區計算配額（0.10.8 獨立審查）：限流以前排在驗證之前，分區鍵讀不到使用者，
/// 全部依 IP 分區 —— 同一個出口 IP（NAT、反向代理）的所有人共用一份配額，一個人用完別人也被擋。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class PerUserRateLimitTests : IClassFixture<ApiTestApplicationFactoryWithTightApiLimit>
{
    private readonly ApiTestApplicationFactoryWithTightApiLimit factory;

    public PerUserRateLimitTests(ApiTestApplicationFactoryWithTightApiLimit factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task OneUserUsingUpTheQuota_ShouldNotThrottleAnotherUserFromTheSameAddress()
    {
        using var alice = await SignedInClientAsync(await AddUserAsync());
        using var bob = await SignedInClientAsync(await AddUserAsync());

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add((await alice.GetAsync("/api/v1/Auth/me")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/api/v1/Auth/me")).StatusCode);
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
        var account = $"quota-{Guid.NewGuid():N}";
        var user = new MyUser
        {
            Account = account,
            Name = "Quota " + account[^6..],
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
