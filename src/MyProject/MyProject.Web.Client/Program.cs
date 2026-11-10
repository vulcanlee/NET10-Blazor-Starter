using AntDesign;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Client.Layout;
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
        builder.Services.AddAntDesign();

        // AntDesign 的繁體中文語系（與 Server 版相同的補充翻譯）。
        LocaleProvider.SetLocale("zh-TW", AntDesignLocaleFactory.Create("zh-TW"));
        LocaleProvider.DefaultLanguage = "zh-TW";

        await builder.Build().RunAsync();
    }
}
