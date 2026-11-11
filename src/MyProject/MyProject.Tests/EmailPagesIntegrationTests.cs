using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using MyProject.Dtos.Auths;
using MyProject.Dtos.Commons;

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

/// <summary>
/// 寄信啟用時（0.10.8 起登入、忘記、重設密碼頁在 WASM，畫面由瀏覽器依 API 決定；這裡驗 API 與主機回應）：
/// 登入頁設定說寄信已啟用（登入頁才顯示「忘記密碼？」）、重設頁的整頁回應帶防外洩標頭。
/// 忘記與重設密碼的行為見 AccountSelfServiceApiTests。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class EmailEnabledPagesTests : IClassFixture<ApiTestApplicationFactoryWithPickupEmail>
{
    private readonly ApiTestApplicationFactoryWithPickupEmail factory;

    public EmailEnabledPagesTests(ApiTestApplicationFactoryWithPickupEmail factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task LoginSettings_ShouldSayForgotPasswordIsAvailable()
    {
        using var client = factory.CreateClient();

        var settings = (await client.GetFromJsonAsync<ApiResult<LoginPageSettingsDto>>("/api/v1/Auth/login-settings"))!.Data!;

        Assert.True(settings.EmailEnabled);
    }

    /// <summary>
    /// token 在網址裡：no-referrer 讓頁面載入的外部資源拿不到網址，no-store 讓瀏覽器與代理不留副本（0.10.8 起由主機在整頁請求時加）。
    /// </summary>
    [Fact]
    public async Task ResetPasswordPage_ShouldBeServedWithProtectiveHeaders()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Auths/ResetPassword?token=forged");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-referrer", string.Join(",", response.Headers.GetValues("Referrer-Policy")));
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.DoesNotContain("forged", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ResetCheck_WithAForgedToken_ShouldSayTheLinkIsInvalid()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/Auth/password/reset/check", new ResetPasswordCheckRequestDto { Token = "forged" });

        Assert.False((await response.Content.ReadFromJsonAsync<ApiResult<ResetPasswordCheckDto>>())!.Data!.Valid);
        Assert.Equal("no-referrer", string.Join(",", response.Headers.GetValues("Referrer-Policy")));
    }
}

/// <summary>寄信未啟用（出貨預設 None）時：登入頁不顯示「忘記密碼？」，忘記與重設都說未啟用。</summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class EmailDisabledPagesTests : IClassFixture<ApiTestApplicationFactory>
{
    private readonly ApiTestApplicationFactory factory;

    public EmailDisabledPagesTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task LoginSettings_ShouldSayForgotPasswordIsNotAvailable()
    {
        using var client = factory.CreateClient();

        var settings = (await client.GetFromJsonAsync<ApiResult<LoginPageSettingsDto>>("/api/v1/Auth/login-settings"))!.Data!;

        Assert.False(settings.EmailEnabled);
    }

    [Fact]
    public async Task ResetApis_ShouldSayTheFeatureIsDisabled()
    {
        using var client = factory.CreateClient();

        var forgot = await client.PostAsJsonAsync("/api/v1/Auth/password/forgot", new ForgotPasswordRequestDto { Identifier = "anyone" });
        var reset = await client.PostAsJsonAsync("/api/v1/Auth/password/reset", new ResetPasswordRequestDto { Token = "x", NewPassword = "Abcd!2345678", ConfirmPassword = "Abcd!2345678" });

        Assert.Equal(HttpStatusCode.BadRequest, forgot.StatusCode);
        Assert.Contains("未啟用寄信功能", (await forgot.Content.ReadFromJsonAsync<ApiResult<object>>())!.ErrorMessage);
        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);
        Assert.Contains("未啟用寄信功能", (await reset.Content.ReadFromJsonAsync<ApiResult<object>>())!.ErrorMessage);
    }
}
