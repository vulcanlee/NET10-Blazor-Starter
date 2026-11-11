using AntDesign;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Logging;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Client.Commons;
using MyProject.Web.Client.Layout;
using MyProject.Web.Components.Commons;
using MyProject.Web.Localization;

namespace MyProject.Web.Client;

/// <summary>
/// WASM 前端的進入點（0.10.0 起；0.10.1 起有第一個頁面「個人資料」）。遷移期間由 MyProject.Web 一併提供，同一個網域，API 走相對路徑。
/// ⚠️ 刻意不用 top-level statements：那會產生一個全域的 <c>Program</c> 類別，和主機的 <c>Program</c>（測試的 <c>WebApplicationFactory&lt;Program&gt;</c>）撞名。
/// </summary>
public static class ClientProgram
{
    public static async Task Main(string[] args)
    {
        var builder = WebAssemblyHostBuilder.CreateDefault(args);

        builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
        builder.Services.AddScoped<TokenStore>();
        builder.Services.AddScoped<ClientSession>();
        builder.Services.AddScoped<ApiClient>();
        builder.Services.AddScoped<ClientErrorReporter>();
        builder.Services.AddScoped<PagePermissions, ClientPagePermissions>();
        builder.Services.AddScoped<IAuditHistorySource, ApiAuditHistorySource>();
        builder.Services.AddScoped<CategoryApi>();
        builder.Services.AddScoped<TeamApi>();
        builder.Services.AddAntDesign();

        // AntDesign 的繁體中文語系（與 Server 版相同的補充翻譯）。
        LocaleProvider.SetLocale("zh-TW", AntDesignLocaleFactory.Create("zh-TW"));
        LocaleProvider.DefaultLanguage = "zh-TW";

        var host = builder.Build();

        // 語系一律 zh-TW，日期時間格式用伺服器那一組（ClientCulture）；必須在 RunAsync 之前。
        using (var http = new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) })
        {
            await ClientCulture.LoadAsync(http, host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ClientCulture).FullName!));
        }

        // 右下角通知（與 Server 版共用 ViewNotification）：日誌寫到瀏覽器主控台，追蹤碼取最近一次失敗的 API 回應。
        ViewNotification.UseLoggerFactory(host.Services.GetRequiredService<ILoggerFactory>());
        ViewNotification.UseTraceCodeProvider(ApiClient.TakeLastFailureTraceId);

        await host.RunAsync();
    }
}
