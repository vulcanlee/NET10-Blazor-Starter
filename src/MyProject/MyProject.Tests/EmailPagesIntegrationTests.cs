using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MyProject.Tests;

/// <summary>與 <see cref="ApiTestApplicationFactory"/> 相同，但開啟 Pickup 寄信（信寫到測試自己的暫存資料夾）。</summary>
public sealed class ApiTestApplicationFactoryWithPickupEmail : ApiTestApplicationFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // 註冊順序在 base 之後，覆寫得掉 base 的 Provider=None；PickupDirectory 沿用 base 指到的暫存目錄。
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EmailSettings:Provider"] = "Pickup",
            });
        });
    }
}

/// <summary>寄信啟用時：登入頁有「忘記密碼？」、兩個頁面可用、重設頁帶上防外洩的回應標頭。</summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class EmailEnabledPagesTests : IClassFixture<ApiTestApplicationFactoryWithPickupEmail>
{
    private readonly ApiTestApplicationFactoryWithPickupEmail factory;

    public EmailEnabledPagesTests(ApiTestApplicationFactoryWithPickupEmail factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task LoginPage_ShouldLinkToForgotPassword()
    {
        using var client = factory.CreateClient();

        var body = WebUtility.HtmlDecode(await client.GetStringAsync("/Auths/Login"));

        Assert.Contains("href=\"/Auths/ForgotPassword\"", body);
    }

    [Fact]
    public async Task ForgotPasswordPage_ShouldRenderTheForm()
    {
        using var client = factory.CreateClient();

        var body = WebUtility.HtmlDecode(await client.GetStringAsync("/Auths/ForgotPassword"));

        Assert.Contains("name=\"Input.Identifier\"", body);
        Assert.DoesNotContain("未啟用寄信功能", body);
    }

    /// <summary>
    /// token 在網址裡：no-referrer 讓頁面載入的外部資源拿不到網址，no-store 讓瀏覽器與代理不留副本。
    /// 無效 token 只顯示「連結無效」，不給表單。
    /// </summary>
    [Fact]
    public async Task ResetPasswordPage_WithBadToken_ShouldShowInvalidLinkWithProtectiveHeaders()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Auths/ResetPassword?token=forged");
        var body = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-referrer", string.Join(",", response.Headers.GetValues("Referrer-Policy")));
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("重設連結無效或已過期", body);
        Assert.DoesNotContain("name=\"Input.NewPassword\"", body);
    }
}

/// <summary>寄信未啟用（出貨預設 None）時：沒有連結，兩頁只顯示「未啟用」。</summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class EmailDisabledPagesTests : IClassFixture<ApiTestApplicationFactory>
{
    private readonly ApiTestApplicationFactory factory;

    public EmailDisabledPagesTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task LoginPage_ShouldNotLinkToForgotPassword()
    {
        using var client = factory.CreateClient();

        var body = await client.GetStringAsync("/Auths/Login");

        Assert.DoesNotContain("/Auths/ForgotPassword", body);
    }

    [Theory]
    [InlineData("/Auths/ForgotPassword")]
    [InlineData("/Auths/ResetPassword?token=anything")]
    public async Task ResetPages_ShouldSayTheFeatureIsDisabled(string route)
    {
        using var client = factory.CreateClient();

        var body = WebUtility.HtmlDecode(await client.GetStringAsync(route));

        Assert.Contains("未啟用寄信功能", body);
        Assert.DoesNotContain("<form", body);
    }
}
