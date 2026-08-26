using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using MyProject.Models.Systems;
using MyProject.Web.Configuration;
using System.Diagnostics;
using System.Net;

namespace MyProject.Web.Extensions;

public static class ApplicationBuilderExtensions
{
    public static WebApplication UseConfiguredSwagger(this WebApplication app, ILogger logger)
    {
        var swaggerSettings = app.Configuration
            .GetSection(SwaggerSettings.SectionName)
            .Get<SwaggerSettings>() ?? new SwaggerSettings();

        if (!app.Environment.IsDevelopment()
            && !(app.Environment.IsProduction() && swaggerSettings.EnabledInProduction))
        {
            return app;
        }

        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "MyProject API v1");
        });
        logger.LogInformation("Swagger UI enabled.");
        return app;
    }

    public static WebApplication UseConfiguredForwardedHeaders(this WebApplication app)
    {
        var settings = app.Configuration
            .GetSection(ForwardedHeadersSettings.SectionName)
            .Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        };

        // 只在確實設定了信任來源時才放寬；沒設定就維持 ASP.NET Core 預設（僅信任 loopback），
        // 避免任何呼叫端都能偽造 X-Forwarded-For 來繞過以 IP 分割的限流。
        foreach (var proxy in settings.KnownProxies)
        {
            if (IPAddress.TryParse(proxy, out var address))
            {
                options.KnownProxies.Add(address);
            }
        }

        foreach (var network in settings.KnownNetworks)
        {
            var parts = network.Split('/', 2);
            if (parts.Length == 2
                && IPAddress.TryParse(parts[0], out var prefix)
                && int.TryParse(parts[1], out var prefixLength))
            {
                options.KnownIPNetworks.Add(new System.Net.IPNetwork(prefix, prefixLength));
            }
        }

        app.UseForwardedHeaders(options);

        return app;
    }

    /// <summary>
    /// 基本安全回應標頭。
    ///
    /// <c>nosniff</c> 尤其重要：專案的附件下載會回吐儲存時記下的 ContentType，
    /// 少了它，瀏覽器可能把附件當成 HTML 解析而造成同源 XSS。
    ///
    /// CSP 刻意不放在這裡：Blazor Server 有自己的 CSP 需求，且
    /// <c>App.razor</c> 會從 fonts.googleapis.com 載入字型、AntDesign 會注入 inline style。
    /// 導入時請先以 <c>Content-Security-Policy-Report-Only</c> 觀察，確認無誤再轉為正式標頭。
    /// </summary>
    public static WebApplication UseSecurityHeaders(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

            await next();
        });

        return app;
    }

    public static WebApplication UseConfiguredCors(this WebApplication app)
    {
        app.UseCors("ConfiguredCors");
        return app;
    }

    /// <summary>
    /// 不值得記在 Information 的路徑前綴：靜態資產、框架端點與健康探針。
    ///
    /// 這些請求量遠大於使用者真正的操作 —— 光是每 10 秒一次的 /health/live 就是一天
    /// 8,640 筆，會把「使用者做了什麼」整個淹掉。它們改記在 Debug，需要時仍查得到。
    /// </summary>
    private static readonly string[] LowValueRequestPrefixes =
    [
        "/_framework", "/_content", "/_blazor", "/css", "/js", "/lib",
        "/health", "/favicon", "/UploadFiles", "/swagger",
    ];

    /// <summary>
    /// 靜態資產的副檔名。單靠路徑前綴不夠 —— 例如 app.css 是掛在網站根目錄
    /// （/app.css）而非 /css 之下，只比對前綴會漏掉。
    /// </summary>
    private static readonly string[] StaticAssetExtensions =
    [
        ".css", ".js", ".map", ".png", ".jpg", ".jpeg", ".gif", ".svg",
        ".ico", ".woff", ".woff2", ".ttf", ".eot",
    ];

    private static bool IsLowValueRequest(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var prefix in LowValueRequestPrefixes)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var extension in StaticAssetExtensions)
        {
            if (value.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static WebApplication UseHttpRequestLogging<TProgram>(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var requestLogger = context.RequestServices.GetRequiredService<ILogger<TProgram>>();
            var stopwatch = Stopwatch.StartNew();

            try
            {
                await next();
                stopwatch.Stop();

                // 失敗的請求一律記在 Information 以上，即使路徑是低價值的 ——
                // 靜態資產 404 往往正是「連結壞掉」的線索。
                var isLowValue = IsLowValueRequest(context.Request.Path)
                    && context.Response.StatusCode < 400;

                requestLogger.Log(
                    isLowValue ? LogLevel.Debug : LogLevel.Information,
                    "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMilliseconds} ms",
                    context.Request.Method,
                    context.Request.Path.Value,
                    context.Response.StatusCode,
                    stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                requestLogger.LogError(
                    ex,
                    "HTTP {Method} {Path} failed after {ElapsedMilliseconds} ms",
                    context.Request.Method,
                    context.Request.Path.Value,
                    stopwatch.ElapsedMilliseconds);
                throw;
            }
        });

        return app;
    }

    public static WebApplication UseConfiguredLocalization(this WebApplication app)
    {
        var localizationOptions = app.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<RequestLocalizationOptions>>()
            .Value;

        app.UseRequestLocalization(localizationOptions);
        return app;
    }

    /// <summary>下載目錄對應的外部路徑前綴。</summary>
    private const string DownloadRequestPath = "/UploadFiles";

    /// <summary>
    /// 把 <c>ExternalFileSystem.DownloadPath</c> 掛到 <c>/UploadFiles</c>。
    ///
    /// ⚠️ 必須在 <c>UseAuthentication()</c> / <c>UseAuthorization()</c> 之後呼叫，
    /// 且**要求已驗證身分**才提供檔案。
    ///
    /// 沿革：0.4.34 之前它掛在驗證中介軟體之前，且 <c>UseStaticFiles</c> 本身不看授權，
    /// 等於整個下載目錄匿名可讀。預設設定下 <c>ProjectFilePath</c> 不在 <c>DownloadPath</c>
    /// 底下所以沒有直接外洩，但交付到客戶端後只要有人把檔案放進該目錄就會裸奔 ——
    /// 與 <c>ProjectFileController</c> 的權限 + 團隊守門 + 稽核軌跡完全相反。
    /// </summary>
    public static WebApplication UseConfiguredDownloadStaticFiles(this WebApplication app, SystemSettings systemSettings)
    {
        if (string.IsNullOrWhiteSpace(systemSettings.ExternalFileSystem.DownloadPath))
        {
            return app;
        }

        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(DownloadRequestPath),
            branch =>
            {
                branch.Use(async (context, next) =>
                {
                    if (context.User?.Identity?.IsAuthenticated != true)
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }

                    await next();
                });

                branch.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(systemSettings.ExternalFileSystem.DownloadPath),
                    RequestPath = DownloadRequestPath
                });
            });

        return app;
    }
}
