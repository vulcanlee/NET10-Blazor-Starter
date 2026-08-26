using AntDesign;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MyProject.AccessDatas;
using MyProject.Business.Repositories;
using MyProject.Dtos.Commons;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Auth;
using MyProject.Web.Caching;
using MyProject.Web.Components.Layout;
using MyProject.Web.Configuration;
using MyProject.Web.Health;
using MyProject.Web.Diagnostics;
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
        services.AddSingleton<SystemStartupState>();
        services.AddSingleton<LogLevelRuntimeState>();
        services.AddScoped<INLogFilePathResolver, NLogFilePathResolver>();
        services.AddScoped<ILogQueryService, LogQueryService>();
        services.AddScoped<IDatabaseUsageService, DatabaseUsageService>();
        services.AddScoped<IHealthLogReader, HealthLogReader>();
        services.AddScoped<ISystemHealthService, SystemHealthService>();
        services.AddScoped<AuthenticationStateHelper>();
        services.AddScoped<CurrentUserService>();
        services.AddScoped<IAuditLogService, AuditLogService>();
        services.AddScoped<ITotpService, TotpService>();
        services.AddScoped<IRbacBackfillService, RbacBackfillService>();
        services.AddScoped<IPermissionChecker, PermissionChecker>();
        services.AddScoped<IRbacWriteService, RbacWriteService>();
        services.AddScoped<IEffectiveTeamResolver, EffectiveTeamResolver>();
        services.AddScoped<MyUserServiceLogin>();
        services.AddScoped<ExternalLoginService>();
        services.AddScoped<SidebarMenuService>();
        services.AddScoped<RolePermissionService>();
        services.AddScoped<RoleViewService>();
        services.AddScoped<MyUserService>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddScoped<ProjectService>();
        services.AddScoped<ProjectRepository>();
        services.AddScoped<CategoryService>();
        services.AddScoped<CategoryRepository>();
        services.AddScoped<TeamService>();
        services.AddScoped<TeamRepository>();
        services.AddHttpContextAccessor();
        services.AddScoped<IRecordAccessScopeProvider, RecordAccessScopeProvider>();
        services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, MyProject.Web.Components.ApplicationCircuitHandler>();

        return services;
    }

    public static IServiceCollection AddConfiguredOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SystemSettings>(configuration.GetSection(nameof(SystemSettings)));
        services.Configure<SecuritySettings>(configuration.GetSection(SecuritySettings.SectionName));
        services.Configure<CorsSettings>(configuration.GetSection(CorsSettings.SectionName));
        services.Configure<SwaggerSettings>(configuration.GetSection(SwaggerSettings.SectionName));
        services.Configure<CacheSettings>(configuration.GetSection(CacheSettings.SectionName));
        services.Configure<RateLimitSettings>(configuration.GetSection(RateLimitSettings.SectionName));

        return services;
    }

    /// <summary>
    /// 資料庫註冊。
    ///
    /// ⚠️ **以 <c>IDbContextFactory</c> 為主**，不要改回單純的 <c>AddDbContext</c>：
    /// 在 Blazor Server，DI scope ＝ SignalR circuit，存活時間等同使用者整個連線
    /// （數分鐘到數小時），不是一次 HTTP 請求。scoped 的 DbContext 會導致
    /// 追蹤實體只增不減（記憶體成長、讀到過期資料），兩個元件事件重疊時還會炸
    /// 「A second operation was started on this context」。
    ///
    /// Blazor 路徑的 <c>Services/DataAccess/*</c> 一律注入工廠、每個方法用完即棄。
    /// 仍保留一個 scoped 的 <see cref="BackendDBContext"/> 供 Repository（API 路徑，
    /// scope ＝ 單次 HTTP 請求，本來就正確）與健康檢查／診斷服務使用，
    /// 但它改由工廠產生，避免 options 生命週期與工廠衝突。
    /// </summary>
    public static IServiceCollection AddConfiguredDatabase(this IServiceCollection services, SystemSettings systemSettings)
    {
        services.AddDbContextFactory<BackendDBContext>(options =>
        {
            var sqliteConnectionString = MagicObjectHelper.GetSQLiteConnectionString(systemSettings.ExternalFileSystem.DatabasePath);
            options.UseSqlite(sqliteConnectionString);
        });

        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<BackendDBContext>>().CreateDbContext());

        return services;
    }

    public static IServiceCollection AddConfiguredCache(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(CacheSettings.SectionName).Get<CacheSettings>() ?? new CacheSettings();

        switch (settings.GetProvider())
        {
            case CacheProvider.Redis:
                if (string.IsNullOrWhiteSpace(settings.RedisConnection))
                {
                    throw new InvalidOperationException("CacheSettings:RedisConnection 不可為空白。");
                }

                services.AddStackExchangeRedisCache(options =>
                {
                    options.Configuration = settings.RedisConnection;
                    options.InstanceName = settings.InstanceName;
                });
                break;

            case CacheProvider.Memory:
                services.AddDistributedMemoryCache();
                break;

            default:
                throw new InvalidOperationException($"不支援的快取 provider：{settings.Provider}");
        }

        services.AddSingleton<ICacheService, DistributedCacheService>();

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

            // 被限流時也要維持 ApiResult 信封（專案不變量：Web API 回應一律包信封）。
            // 另一個作用是「先把 body 寫掉」—— 否則空 body 的 429 會被
            // UseStatusCodePagesWithReExecute 拿原始的 POST + JSON 去重跑 /not-found，
            // 那是 Blazor 頁面、會被 antiforgery 擋下，最後回給呼叫端的是 400 HTML。
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.ContentType = "application/json; charset=utf-8";

                var payload = ApiResult.FailureResult("請求過於頻繁，請稍後再試。", StatusCodes.Status429TooManyRequests);
                await context.HttpContext.Response.WriteAsJsonAsync(payload, cancellationToken);
            };

            // ⚠️ 一定要用 AddPolicy + PartitionedRateLimiter，不要用 AddFixedWindowLimiter。
            // 後者是**全站共用的單一計數器**：任一呼叫端每分鐘打滿配額，
            // 其他所有使用者都會拿到 429（0.4.34 之前正是如此）。
            //
            // ⚠️ 登入的較嚴格配額**寫在同一個 policy 裡**，而不是用
            // [EnableRateLimiting("login")] 屬性 —— 端點慣例（MapControllers().RequireRateLimiting("api")）
            // 套用時機晚於屬性，會把屬性指定的 policy 蓋掉，導致登入配額靜默失效。
            // 這一點單靠測試看不出來（兩者都回 401），是實跑才發現的。
            options.AddPolicy("api", context =>
            {
                // ⚠️ 在**請求時**才讀設定，不要在註冊時急切讀取：
                // 那樣不但無法支援設定重載，連 WebApplicationFactory 的測試覆寫都會失效
                // （它套用組態的時機晚於服務註冊）。
                var settings = context.RequestServices
                    .GetRequiredService<IOptionsMonitor<RateLimitSettings>>()
                    .CurrentValue;

                var isLogin = IsLoginRequest(context);
                var permitLimit = isLogin ? settings.LoginRequestsPerMinute : settings.ApiRequestsPerMinute;

                // 前綴讓登入與一般 API 各自計數，不會互相消耗配額。
                var partitionKey = (isLogin ? "login:" : "api:") + ResolvePartitionKey(context);

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitLimit,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    });
            });
        });

        return services;
    }

    /// <summary>
    /// 登入端點（含 /api/v1 平行路由）。登入是暴力破解的主要標的，配額比一般 API 嚴格得多；
    /// 帳號鎖定是第二道防線，但它擋不住「橫向」猜測多個帳號。
    /// </summary>
    private static bool IsLoginRequest(HttpContext context)
    {
        var path = context.Request.Path;
        return path.StartsWithSegments("/api/Auth/login", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/api/v1/Auth/login", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 限流的分割鍵：已驗證身分優先（同一人換 IP 也受同一份配額），
    /// 未登入則退回來源 IP，取不到 IP 時才落到共用的 "anonymous" 分割。
    ///
    /// ⚠️ 取 IP 會受 <c>X-Forwarded-For</c> 影響，因此
    /// <c>UseForwardedHeaders</c> 必須設定 KnownProxies/KnownNetworks，
    /// 否則呼叫端可自行偽造標頭來繞過配額。見 ApplicationBuilderExtensions。
    /// </summary>
    internal static string ResolvePartitionKey(HttpContext context)
    {
        var user = context.User?.Identity;
        if (user is { IsAuthenticated: true } && !string.IsNullOrWhiteSpace(context.User!.Identity!.Name))
        {
            return $"user:{context.User.Identity.Name}";
        }

        var ip = context.Connection.RemoteIpAddress;
        return ip is null ? "anonymous" : $"ip:{ip}";
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
