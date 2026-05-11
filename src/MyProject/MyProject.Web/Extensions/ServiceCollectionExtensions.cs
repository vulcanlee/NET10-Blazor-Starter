using AntDesign;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MyProject.AccessDatas;
using MyProject.Business.Repositories;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Auth;
using MyProject.Web.Components.Layout;
using MyProject.Web.Configuration;
using MyProject.Web.Health;
using MyProject.Web.Localization;
using System.Globalization;
using System.Threading.RateLimiting;

namespace MyProject.Web.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddConfiguredLocalization(this IServiceCollection services)
    {
        services.AddLocalization();

        var supportedCultures = new[]
        {
            new CultureInfo("zh-TW"),
            new CultureInfo("en-US")
        };

        var defaultCulture = supportedCultures[0];

        services.Configure<RequestLocalizationOptions>(options =>
        {
            options.DefaultRequestCulture = new RequestCulture(defaultCulture);
            options.SupportedCultures = supportedCultures;
            options.SupportedUICultures = supportedCultures;

            options.RequestCultureProviders = new List<IRequestCultureProvider>
            {
                new AcceptLanguageHeaderRequestCultureProvider()
            };
        });

        LocaleProvider.SetLocale("zh-TW", AntDesignLocaleFactory.Create("zh-TW"));
        LocaleProvider.DefaultLanguage = defaultCulture.Name;

        return services;
    }

    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<AuthenticationStateHelper>();
        services.AddScoped<CurrentUserService>();
        services.AddScoped<MyUserServiceLogin>();
        services.AddScoped<SidebarMenuService>();
        services.AddScoped<RolePermissionService>();
        services.AddScoped<RoleViewService>();
        services.AddScoped<MyUserService>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddScoped<ProjectService>();
        services.AddScoped<ProjectRepository>();
        services.AddScoped<MyTaskRepository>();
        services.AddScoped<MeetingRepository>();
        services.AddScoped<MyTasService>();
        services.AddScoped<MeetingService>();

        return services;
    }

    public static IServiceCollection AddConfiguredOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SystemSettings>(configuration.GetSection(nameof(SystemSettings)));
        services.Configure<SecuritySettings>(configuration.GetSection(SecuritySettings.SectionName));
        services.Configure<CorsSettings>(configuration.GetSection(CorsSettings.SectionName));
        services.Configure<SwaggerSettings>(configuration.GetSection(SwaggerSettings.SectionName));

        return services;
    }

    public static IServiceCollection AddConfiguredDatabase(this IServiceCollection services, SystemSettings systemSettings)
    {
        services.AddDbContext<BackendDBContext>(options =>
        {
            switch (systemSettings.GetDatabaseProvider())
            {
                case DatabaseProvider.SqlServer:
                    if (string.IsNullOrWhiteSpace(systemSettings.ConnectionStrings.DefaultConnection))
                    {
                        throw new InvalidOperationException("SystemSettings:ConnectionStrings:DefaultConnection 不可為空白。");
                    }

                    options.UseSqlServer(
                        systemSettings.ConnectionStrings.DefaultConnection,
                        sqlServer => sqlServer.MigrationsAssembly(
                            typeof(MyProject.AccessDatas.SqlServerMigrations.SqlServerMigrationAssemblyMarker)
                                .Assembly
                                .GetName()
                                .Name));
                    break;

                case DatabaseProvider.Sqlite:
                    var sqliteConnectionString = MagicObjectHelper.GetSQLiteConnectionString(systemSettings.ExternalFileSystem.DatabasePath);
                    options.UseSqlite(sqliteConnectionString);
                    break;

                default:
                    throw new InvalidOperationException($"不支援的資料庫 provider：{systemSettings.DatabaseProvider}");
            }
        }, ServiceLifetime.Scoped);

        return services;
    }

    public static IServiceCollection AddConfiguredCors(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(CorsSettings.SectionName).Get<CorsSettings>() ?? new CorsSettings();
        services.AddCors(options =>
        {
            options.AddPolicy("ConfiguredCors", policy =>
            {
                if (settings.AllowedOrigins.Length == 0)
                {
                    policy.SetIsOriginAllowed(_ => false);
                    return;
                }

                policy
                    .WithOrigins(settings.AllowedOrigins)
                    .AllowAnyHeader()
                    .AllowAnyMethod();
            });
        });

        return services;
    }

    public static IServiceCollection AddConfiguredRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddFixedWindowLimiter("api", limiterOptions =>
            {
                limiterOptions.PermitLimit = 120;
                limiterOptions.Window = TimeSpan.FromMinutes(1);
                limiterOptions.QueueLimit = 0;
                limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            });
        });

        return services;
    }

    public static IServiceCollection AddConfiguredHealthChecks(this IServiceCollection services)
    {
        services
            .AddHealthChecks()
            .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), tags: ["live"])
            .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

        return services;
    }
}
