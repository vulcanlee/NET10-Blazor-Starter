using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using MyProject.Models.Systems;
using MyProject.Web.Configuration;
using System.Diagnostics;

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
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
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

    public static WebApplication UseConfiguredDownloadStaticFiles(this WebApplication app, SystemSettings systemSettings)
    {
        if (string.IsNullOrWhiteSpace(systemSettings.ExternalFileSystem.DownloadPath))
        {
            return app;
        }

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(systemSettings.ExternalFileSystem.DownloadPath),
            RequestPath = "/UploadFiles"
        });

        return app;
    }
}
