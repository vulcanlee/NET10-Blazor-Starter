using AntDesign;
using Microsoft.AspNetCore.Components.Web;
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
/// WASM 前端的進入點（0.10.0 起；0.10.1 起有第一個頁面「個人資料」）。0.10.10 起（WASM 遷移 M11）是獨立的 Blazor WebAssembly 靜態站台：
/// 入口頁是 <c>wwwroot/index.html</c>，API 主機的網址是 <c>wwwroot/appsettings.json</c> 的 <c>ApiBaseUrl</c>（留空＝同一個網址，見 <see cref="ApiAddress"/>）。
/// ⚠️ 刻意不用 top-level statements：那會產生一個全域的 <c>Program</c> 類別，和主機的 <c>Program</c>（測試的 <c>WebApplicationFactory&lt;Program&gt;</c>）撞名。
/// </summary>
public static class ClientProgram
{
    public static async Task Main(string[] args)
    {
        var builder = WebAssemblyHostBuilder.CreateDefault(args);

        // 根元件（0.10.10 起由 index.html 的 #app 與 <head> 承接；0.10.9 以前由主機頁 App.razor 渲染）。
        builder.RootComponents.Add<ClientRoutes>("#app");
        builder.RootComponents.Add<HeadOutlet>("head::after");

        // API 主機的網址：所有呼叫 API 的 HttpClient、整頁導到伺服器端點與瀏覽器直接送出的上傳下載都以它為準。
        var apiAddress = ApiAddress.Resolve(builder.Configuration[ApiAddress.SettingKey], builder.HostEnvironment.BaseAddress);
        builder.Services.AddSingleton(apiAddress);
        builder.Services.AddScoped(_ => apiAddress.CreateHttpClient());
        builder.Services.AddScoped<TokenStore>();
        builder.Services.AddScoped<ClientSession>();
        // 瀏覽器的連線狀態（0.10.11 起，WASM 遷移 M12）：離線提示列、ApiClient 的離線訊息。
        builder.Services.AddScoped<ClientConnectivity>();
        builder.Services.AddScoped<ApiClient>();
        // 登入相關頁面與啟動頁的公開設定（0.10.8 起，WASM 遷移 M9）。
        builder.Services.AddScoped<LoginPageSettingsSource>();
        builder.Services.AddScoped<ClientErrorReporter>();
        // 前端錯誤回報（0.10.36 起）：例外、API 呼叫、換頁交給 js/client-error-reporter.js（先存在瀏覽器、登入後批次送出）。
        // 單例：靜態的 ViewNotification 與框架日誌接收器也要用得到。錯誤邊界以外的未處理例外由框架日誌接到。
        builder.Services.AddSingleton<ClientErrorCapture>();
        builder.Services.AddSingleton<ILoggerProvider, ClientFrameworkErrorLoggerProvider>();
        // 前端診斷記錄（0.10.37 起）：管理員對這位使用者開啟時，本專案的日誌（Debug 或 Trace 以上）送到伺服器的日誌檔。
        // 最低等級放到 Trace 讓它收得到；瀏覽器主控台照舊只顯示 Information 以上（下面的篩選）。
        builder.Services.AddSingleton<ClientDiagnosticsState>();
        builder.Services.AddSingleton<ClientDiagnosticsLoggerProvider>();
        builder.Services.AddSingleton<ILoggerProvider>(services => services.GetRequiredService<ClientDiagnosticsLoggerProvider>());
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddFilter((provider, _, level) => provider == typeof(ClientDiagnosticsLoggerProvider).FullName || level >= LogLevel.Information);
        builder.Services.AddScoped<PagePermissions, ClientPagePermissions>();
        builder.Services.AddScoped<IAuditHistorySource, ApiAuditHistorySource>();
        // 附件上傳與下載（0.10.3 起）：檔案留在瀏覽器，不經過 WASM 記憶體。
        builder.Services.AddScoped<BrowserFiles>();
        builder.Services.AddScoped<ProjectApi>();
        // 管理員頁（0.10.4 起，WASM 遷移 M5）。
        builder.Services.AddScoped<AuditLogApi>();
        builder.Services.AddScoped<EmailLogApi>();
        builder.Services.AddScoped<RoleApi>();
        builder.Services.AddScoped<UserApi>();
        builder.Services.AddScoped<CategoryApi>();
        builder.Services.AddScoped<TeamApi>();
        // AI 對話紀錄（0.10.5 起，WASM 遷移 M6）。
        builder.Services.AddScoped<AiCallLogApi>();
        // Token 用量、AI 提示詞（0.10.5 起，WASM 遷移 M6）。
        builder.Services.AddScoped<TokenUsageApi>();
        builder.Services.AddScoped<PromptTemplateApi>();
        // 日誌檢視與日誌等級設定（0.10.5 起，WASM 遷移 M6）。
        builder.Services.AddScoped<LogViewerApi>();
        builder.Services.AddScoped<LogLevelApi>();
        // 系統例外紀錄與 AI 例外分析（0.10.5 起，WASM 遷移 M6）。
        builder.Services.AddScoped<ExceptionLogApi>();
        // 系統備份、資料保留與清理、資料庫用量（0.10.6 起，WASM 遷移 M7）。
        builder.Services.AddScoped<BackupApi>();
        builder.Services.AddScoped<DataRetentionApi>();
        builder.Services.AddScoped<DatabaseUsageApi>();
        // 排程作業、系統健康監控、首頁儀表板（0.10.6 起，WASM 遷移 M7）。
        builder.Services.AddScoped<ScheduledJobApi>();
        builder.Services.AddScoped<SystemHealthApi>();
        builder.Services.AddScoped<DashboardApi>();
        builder.Services.AddScoped<ClientNotificationSignal>();
        builder.Services.AddScoped<ClientAnnouncementSignal>();
        // 通知推播串流（0.10.7 起，WASM 遷移 M8）：整個 WASM 只有一條，由外框啟動。
        builder.Services.AddScoped<ClientPushConnection>();
        // 外框的系統資訊與選單（0.10.35 起）：登入頁先讀好，外框直接用。
        builder.Services.AddScoped<ClientShellData>();
        // 系統參數、公告管理（0.10.6 起，WASM 遷移 M7）。
        builder.Services.AddScoped<SystemParameterApi>();
        builder.Services.AddScoped<AnnouncementApi>();
        // 系統管理助理（0.10.7 起，WASM 遷移 M8；Agent 測試台與 Agent 工作流程 0.10.22 起已移除）。
        builder.Services.AddScoped<AdminAssistantApi>();
        builder.Services.AddAntDesign();

        // AntDesign 的繁體中文語系（與 Server 版相同的補充翻譯）。
        LocaleProvider.SetLocale("zh-TW", AntDesignLocaleFactory.Create("zh-TW"));
        LocaleProvider.DefaultLanguage = "zh-TW";

        var host = builder.Build();

        // 語系一律 zh-TW，日期時間格式用伺服器那一組（ClientCulture）；必須在 RunAsync 之前。
        using (var http = apiAddress.CreateHttpClient())
        {
            await ClientCulture.LoadAsync(http, host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ClientCulture).FullName!));
        }

        // 前端程式的指紋（0.10.36 起）：附在每筆錯誤回報上，與伺服器收到時的系統版本對照，看得出使用者是否還在跑舊前端。
        // 不把版本號寫進前端：那樣每次升版（即使只改後端）前端都會變，使用者每次都會看到「有新版本」。
        var errorCapture = host.Services.GetRequiredService<ClientErrorCapture>();
        errorCapture.SetAppBuild(typeof(ClientProgram).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..8]);

        // 右下角通知（ViewNotification）：日誌寫到瀏覽器主控台，追蹤碼取最近一次失敗的 API 回應；
        // UnexpectedError 的例外另外回報到伺服器，通知上附的是那筆回報的追蹤碼（0.10.36 起）。
        ViewNotification.UseLoggerFactory(host.Services.GetRequiredService<ILoggerFactory>());
        ViewNotification.UseTraceCodeProvider(ApiClient.TakeLastFailureTraceId);
        ViewNotification.UseErrorCapture(exception => errorCapture.Capture("caught", exception));

        await host.RunAsync();
    }
}
