using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyProject.AccessDatas;
using MyProject.Business.Services.Other;

namespace MyProject.Tests;

/// <summary>
/// 與 <see cref="ApiTestApplicationFactory"/> 相同，但 <see cref="PasswordResetService"/> 一解析就拋例外，
/// 讓匿名的「忘記密碼」API 失敗（0.10.8 起頁面在 WASM，伺服器端只剩 API 會用到這個服務）。
/// </summary>
public sealed class ApiTestApplicationFactoryWithThrowingPage : ApiTestApplicationFactory
{
    public const string ProbeMessage = "Integration error page probe.";

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
            services.AddScoped<PasswordResetService>(_ => throw new InvalidOperationException(ProbeMessage)));
    }
}

/// <summary>
/// 迴歸測試（0.9.115 起）：出錯時使用者要拿到錯誤追蹤碼。0.10.8 起頁面全部在 WASM，伺服器只剩 API 與少數非頁面端點：
/// API 出錯回 500 <c>ApiResult</c> 帶追蹤碼；非 API 請求出錯導到 <c>/Error?trace=…</c>（ErrorPageRedirect，見 ApiIntegrationTests）；
/// 錯誤頁本身匿名可開。追蹤碼與這次請求的日誌、系統例外紀錄是同一個。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class ErrorPageRenderTests : IClassFixture<ApiTestApplicationFactoryWithThrowingPage>
{
    private readonly ApiTestApplicationFactoryWithThrowingPage factory;

    public ErrorPageRenderTests(ApiTestApplicationFactoryWithThrowingPage factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task ApiThrowing_ShouldReturn500WithATraceCode_AndTheErrorPageShouldBeServed()
    {
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.PostAsJsonAsync("/api/v1/Auth/password/forgot", new MyProject.Dtos.Auths.ForgotPasswordRequestDto { Identifier = "anyone" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<MyProject.Dtos.Commons.ApiResult<object>>();
        Assert.Matches("^[0-9A-Z]{8}$", body!.TraceId);

        // 錯誤頁本身（WASM 頁面）匿名可開，追蹤碼由網址帶入。
        var page = await client.GetAsync($"/Error?trace={body.TraceId}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }
}

/// <summary>
/// 迴歸測試（0.9.115）：Host 空白（HTTP/1.0 可以不帶）或組不成網址的請求由 <c>UseRejectInvalidHost</c> 直接回 400，
/// 不進 Blazor —— 否則組 BaseUri 得到 <c>https:///</c> 而拋 UriFormatException，連 /Error 都失敗，一次記 5 筆 ERROR。
/// </summary>
[Collection(nameof(IntegrationHostCollection))]
public sealed class InvalidHostRequestTests : IClassFixture<ApiTestApplicationFactory>
{
    private readonly ApiTestApplicationFactory factory;

    public InvalidHostRequestTests(ApiTestApplicationFactory factory)
    {
        this.factory = factory;
    }

    [Theory]
    [InlineData("")]
    [InlineData("exa mple.com")]
    public async Task RequestWithInvalidHost_ShouldReturn400WithoutRecordingException(string host)
    {
        var context = await factory.Server.SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Get;
            c.Request.Path = "/";
            c.Request.Host = new HostString(host);
        });

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);

        // 例外紀錄由背景寫入器寫入，留一點時間再數。
        await Task.Delay(TimeSpan.FromSeconds(1));
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<BackendDBContext>>();
        await using var db = await contextFactory.CreateDbContextAsync();
        Assert.Equal(0, await db.ExceptionLog.CountAsync(x => x.Message.Contains("hostname could not be parsed")));
    }
}
