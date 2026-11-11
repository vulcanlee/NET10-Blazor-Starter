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
        // 登入相關頁面與啟動頁的公開設定（0.10.8 起，WASM 遷移 M9）。
        builder.Services.AddScoped<LoginPageSettingsSource>();
        builder.Services.AddScoped<ClientErrorReporter>();
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
        // 系統參數、公告管理（0.10.6 起，WASM 遷移 M7）。
        builder.Services.AddScoped<SystemParameterApi>();
        builder.Services.AddScoped<AnnouncementApi>();
        // Agent 工作流程（0.10.7 起，WASM 遷移 M8）。
        builder.Services.AddScoped<AgentWorkflowApi>();
        // Agent 測試台、系統管理助理（0.10.7 起，WASM 遷移 M8）。
        builder.Services.AddScoped<AgentPlaygroundApi>();
        builder.Services.AddScoped<AdminAssistantApi>();
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
