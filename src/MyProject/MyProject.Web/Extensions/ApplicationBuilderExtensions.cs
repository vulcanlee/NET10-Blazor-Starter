using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.FileProviders;
using MyProject.Models.Systems;
using System.Diagnostics;

namespace MyProject.Web.Extensions;

public static class ApplicationBuilderExtensions
{
    public static WebApplication UseDevelopmentSwagger(this WebApplication app, ILogger logger)
    {
        if (!app.Environment.IsDevelopment())
        {
            return app;
        }

        app.UseSwagger();
        app.UseSwaggerUI();
        logger.LogInformation("Swagger UI enabled for development environment.");
        return app;
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

                requestLogger.LogInformation(
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
