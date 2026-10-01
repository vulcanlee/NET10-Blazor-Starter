using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using AntDesign;
using MyProject.Dtos.Commons;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Repositories;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Ai;
using MyProject.Web.Auth;
using MyProject.Web.Components;
using MyProject.Web.Components.Layout;
using MyProject.Web.Configuration;
using MyProject.Web.Diagnostics;
using MyProject.Web.Extensions;
using MyProject.Web.Filters;
using MyProject.Web.Localization;
using NLog;
using NLog.Web;
using System.Text;
using System.Text.Json;

namespace MyProject.Web
{
    public class Program
    {
        public static void Main(string[] args)
        {
            ILogger<Program>? logger = null;

            // 啟動失敗時 catch 區塊要用到的東西，必須宣告在 try 之外（LOG-06）。
            ExceptionContextAccessor? exceptionContextAccessor = null;
            string? exceptionPath = null;
            var nlogReady = false;
            try
            {
                var builder = WebApplication.CreateBuilder(args);
                exceptionPath = builder.Configuration["SystemSettings:ExternalFileSystem:ExceptionPath"];

                // PDF 報告的中文字型解析器。GlobalFontSettings.FontResolver 是 process 全域且
                // write-once，必須在建立第一個 XFont 之前註冊，所以擺在啟動最前段。
                // EnsureRegistered 是冪等的，整合測試重複啟動 host 也不會丟例外。
                EmbeddedFontResolver.EnsureRegistered();

                #region NLog 相關設定
                var nlogBasePrefixPath = builder.Configuration.GetValue<string>("NLog:BasePath");
                var baseNamespace = typeof(Program).Namespace ?? nameof(MyProject.Web);

                string? nlogBasePath = null;
                if (!string.IsNullOrWhiteSpace(nlogBasePrefixPath))
                {
                    nlogBasePath = Path.Combine(nlogBasePrefixPath, baseNamespace);
                    Directory.CreateDirectory(nlogBasePath);

                    // 設置內部日誌記錄器
                    NLog.Common.InternalLogger.LogLevel = NLog.LogLevel.Info;
                    NLog.Common.InternalLogger.LogFile = Path.Combine(nlogBasePath, $"{baseNamespace}-nlog-internal.log");

                    // 設置變量到當前配置
                    if (LogManager.Configuration is not null)
                    {
                        LogManager.Configuration.Variables["BasePath"] = nlogBasePath;
                        LogManager.Configuration.Variables["LogFilenamePrefix"] = $"{baseNamespace}-logfile";
                    }
                }

                builder.Logging.ClearProviders();

                // ⚠️ LoggingConfigurationSectionName 必須清空：appsettings 的 "NLog" 區段只放本檔讀的 BasePath，
                // 不是 NLog 設定。預設值 "NLog" 會讓 NLog.Web 在「目前沒有設定」時把該區段當成 NLog 設定解析，
                // 遇到 BasePath 直接丟 NLogConfigurationException 讓啟動失敗。正式環境一定有 nlog.config，
                // 但同一行程內前一個 host 結束時的 LogManager.Shutdown() 會把設定清空（整合測試會連續啟動多個 host）。
                builder.Host.UseNLog(new NLogAspNetCoreOptions { LoggingConfigurationSectionName = string.Empty });

                // BasePath 設好之後，host 建立前的失敗也能直接經 NLog 寫進日誌檔（見 catch）。
                // 沒設 BasePath 時檔名會落到磁碟根目錄，寧可不寫。
                nlogReady = nlogBasePath is not null && LogManager.Configuration is not null;
                #endregion

                // ⚠️ 必須排在 NLog 設定之後（0.9.77）：先前它是 Main 的第一行，設定不安全時直接丟例外，
                // 那時 NLog 還沒設定 BasePath，日誌檔裡完全沒有「為什麼啟動失敗」。
                StartupSafetyValidator.Validate(builder.Configuration, builder.Environment.EnvironmentName);

                #region 系統使用服務
                // Add services to the container.
                builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents()
                .AddHubOptions(hubOptions =>
                {
                    // Blazor Server SignalR 預設 MaximumReceiveMessageSize 為 32KB；長文字（如大段
                    // 描述/摘要）由 AntDesign TextArea 同步回伺服器時會超過上限 → circuit 中斷 →
                    // 反覆重連（畫面閃爍）。提高至 10MB 以容納長內容。
                    hubOptions.MaximumReceiveMessageSize = 10 * 1024 * 1024;
                });

                builder.Services.AddControllers(options =>
                {
                    options.Filters.Add<ApiExceptionFilterAttribute>();
                });
                builder.Services.Configure<ApiBehaviorOptions>(options =>
                {
                    options.SuppressModelStateInvalidFilter = true;
                });
                //builder.Services.AddOpenApi();
                builder.Services.AddEndpointsApiExplorer();
                builder.Services.AddSwaggerGen(options =>
                {
                    options.SwaggerDoc("v1", new OpenApiInfo
                    {
                        Title = "MyProject API",
                        Version = "v1",
                        Description = "內部管理系統腳手架 API v1"
                    });
                    options.AddSecurityDefinition(JwtBearerDefaults.AuthenticationScheme, new OpenApiSecurityScheme
                    {
                        Name = "Authorization",
                        Type = SecuritySchemeType.Http,
                        Scheme = JwtBearerDefaults.AuthenticationScheme,
                        BearerFormat = "JWT",
                        In = ParameterLocation.Header,
                        Description = "請輸入 JWT Bearer token。"
                    });

                    options.AddSecurityRequirement(_ => new OpenApiSecurityRequirement
                    {
                        {
                            new OpenApiSecuritySchemeReference(JwtBearerDefaults.AuthenticationScheme, null, null),
                            new List<string>()
                        }
                    });
                });
                builder.Services.AddAntDesign();
                builder.Services.AddConfiguredLocalization();
                builder.Services.AddConfiguredOptions(builder.Configuration);
                builder.Services.AddConfiguredCors(builder.Configuration);
                builder.Services.AddConfiguredRateLimiting();
                builder.Services.AddConfiguredHealthChecks();
                builder.Services.AddConfiguredCache(builder.Configuration);
                builder.Services.AddConfiguredEmail(builder.Configuration);

                #region 加入使用 Cookie & JWT 認證需要的宣告
                // 註：此處原本設定了 CookiePolicyOptions（含 MinimumSameSitePolicy = None），
                // 但 pipeline 從未呼叫 UseCookiePolicy()，等於死碼；且 SameSite=None 本身
                // 會削弱 CSRF 姿態，不該啟用。0.4.34 移除。

                var jwtSettings = builder.Configuration
                    .GetSection(JwtSettings.SectionName)
                    .Get<JwtSettings>() ?? new JwtSettings();
                builder.Services
                    .AddOptions<JwtSettings>()
                    .Bind(builder.Configuration.GetSection(JwtSettings.SectionName))
                    .ValidateDataAnnotations()
                    .ValidateOnStart();

                // ⚠️ 必須在下面的 AddCookie 之前取值。比照 JwtSettings 的雙軌寫法：
                // 先取一份即時值給 AddCookie 當場用，再註冊 DI ＋ 啟動驗證。
                var cookieSettings = builder.Configuration
                    .GetSection(CookieSettings.SectionName)
                    .Get<CookieSettings>() ?? new CookieSettings();
                builder.Services
                    .AddOptions<CookieSettings>()
                    .Bind(builder.Configuration.GetSection(CookieSettings.SectionName))
                    .ValidateDataAnnotations()
                    .ValidateOnStart();

                var googleOAuthSettings = builder.Configuration
                    .GetSection(GoogleOAuthSettings.SectionName)
                    .Get<GoogleOAuthSettings>() ?? new GoogleOAuthSettings();
                builder.Services.Configure<GoogleOAuthSettings>(
                    builder.Configuration.GetSection(GoogleOAuthSettings.SectionName));

                var authenticationBuilder = builder.Services.AddAuthentication(MagicObjectHelper.CookieScheme)
                    .AddCookie(MagicObjectHelper.CookieScheme, options =>
                    {
                        // 名稱必須帶專案名（New-StarterProject.ps1 會一起換掉）。Cookie 不分連接埠，
                        // 同一主機名稱部署多個衍生系統時，若沿用框架預設名就會互相覆蓋、互相登出。
                        // ⚠️ 改這個值會讓既有登入（連同記住我）全部失效一次。
                        options.Cookie.Name = ".MyProject.Auth";
                        options.Cookie.IsEssential = true;
                        options.LoginPath = "/Auths/Login";
                        options.LogoutPath = "/Auths/Logout";
                        options.AccessDeniedPath = "/Auths/Login";

                        // 0.9.39 起明寫。之前兩者都沒設定，吃框架隱藏預設（14 天 ＋ 滑動），
                        // 「登入能撐多久」在設定檔裡查不到，也無法依環境調整。
                        // ⚠️ 勾了「記住我」的人走的是另一個效期 —— ExpireTimeSpan 是整個
                        // scheme 共用的，做不出兩種，故在 Login.razor.cs 明寫 ExpiresUtc 覆蓋。
                        options.ExpireTimeSpan = TimeSpan.FromMinutes(cookieSettings.ExpireMinutes);
                        options.SlidingExpiration = cookieSettings.SlidingExpiration;
                    })
                    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
                    {
                        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
                        options.SaveToken = false;
                        options.TokenValidationParameters = new TokenValidationParameters
                        {
                            ValidateIssuer = true,
                            ValidIssuer = jwtSettings.Issuer,
                            ValidateAudience = true,
                            ValidAudience = jwtSettings.Audience,
                            ValidateIssuerSigningKey = true,
                            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SigningKey)),
                            ValidateLifetime = true,
                            ClockSkew = TimeSpan.FromMinutes(jwtSettings.ClockSkewMinutes)
                        };
                        options.Events = new JwtBearerEvents
                        {
                            OnChallenge = async context =>
                            {
                                context.HandleResponse();
                                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                                context.Response.ContentType = "application/json; charset=utf-8";

                                var result = ApiResult.UnauthorizedResult("未提供有效的 Bearer token。");
                                result.TraceId = context.HttpContext.TraceIdentifier;
                                await context.Response.WriteAsync(JsonSerializer.Serialize(result));
                            },
                            OnForbidden = async context =>
                            {
                                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                                context.Response.ContentType = "application/json; charset=utf-8";

                                var result = ApiResult.ForbiddenResult("目前使用者沒有權限存取此 API。");
                                result.TraceId = context.HttpContext.TraceIdentifier;
                                await context.Response.WriteAsync(JsonSerializer.Serialize(result));
                            }
                        };
                    });

                #region Google OAuth2 第三方登入（僅在已設定時註冊）
                if (googleOAuthSettings.IsConfigured)
                {
                    authenticationBuilder
                        .AddCookie(MagicObjectHelper.ExternalCookieScheme, options =>
                        {
                            options.Cookie.Name = ".MyProject.External";
                            options.Cookie.IsEssential = true;
                            options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
                        })
                        .AddGoogle(GoogleDefaults.AuthenticationScheme, options =>
                        {
                            options.ClientId = googleOAuthSettings.ClientId;
                            options.ClientSecret = googleOAuthSettings.ClientSecret;
                            options.SignInScheme = MagicObjectHelper.ExternalCookieScheme;
                            options.CallbackPath = "/signin-google";
                            options.SaveTokens = false;
                        });
                }
                #endregion

                builder.Services.AddAuthorization();

                // Routes.razor 的 AuthorizeRouteView 需要它提供 Task<AuthenticationState>。
                builder.Services.AddCascadingAuthenticationState();
                #endregion

                #region AutoMapper 使用的宣告
                builder.Services.AddAutoMapper(c =>
                {
                    var autoMapperLicenseKey = builder.Configuration["AutoMapper:LicenseKey"];
                    if (string.IsNullOrWhiteSpace(autoMapperLicenseKey) == false)
                    {
                        c.LicenseKey = autoMapperLicenseKey;
                    }

                    c.AddProfile<AutoMapping>();
                });
                #endregion

                #endregion

                #region 加入設定強型別注入宣告
                #endregion

                #region 系統使用的目錄準備
                // 取得 系統設定物件 SystemSettings
                var systemSettings = builder.Configuration.GetSection(nameof(SystemSettings)).Get<SystemSettings>()
                    ?? new SystemSettings();
                EnsureDirectoryExists(systemSettings.ExternalFileSystem.DatabasePath, "database");
                EnsureDirectoryExists(systemSettings.ExternalFileSystem.DownloadPath, "download");
                EnsureDirectoryExists(systemSettings.ExternalFileSystem.UploadPath, "upload");
                EnsureDirectoryExists(systemSettings.ExternalFileSystem.ProjectFilePath, "project file");
                EnsureDirectoryExists(systemSettings.ExternalFileSystem.ExceptionPath, "exception stack trace");
                EnsureDirectoryExists(systemSettings.ExternalFileSystem.TokenUsagePath, "LLM usage raw payload");
                EnsureDirectoryExists(systemSettings.ExternalFileSystem.AiCallLogPath, "AI call log");
                EnsureDirectoryExists(systemSettings.ExternalFileSystem.DataProtectionKeyPath, "data protection key");
                #endregion

                #region Data Protection 金鑰環
                // ⚠️ 註冊順序不影響：Cookie handler 在執行期才解析 IDataProtectionProvider，
                // 所以放在這裡（systemSettings 已讀出之後）即可，不必搬到 AddAuthentication 之前。
                builder.Services.AddConfiguredDataProtection(systemSettings);
                #endregion

                #region EF Core 宣告
                builder.Services.AddConfiguredDatabase(systemSettings);
                #endregion

                #region 客製服務註冊
                builder.Services.AddApplicationServices();
                #endregion

                var app = builder.Build();
                logger = app.Services.GetRequiredService<ILogger<Program>>();
                logger.LogInformation("Application host built successfully.");
                exceptionContextAccessor = app.Services.GetRequiredService<ExceptionContextAccessor>();

                // 射後不理的 Task 與背景執行緒的未處理例外（LOG-04）。
                app.Services.GetRequiredService<ProcessExceptionHooks>().Register(app.Lifetime);

                // 必須在啟動時初始化，不能等到有人開啟「日誌等級設定」頁面才懶載入 ——
                // 它同時負責訂閱 NLog 的 ConfigurationChanged，在 autoReload 重載後把
                // BasePath / LogFilenamePrefix 變數補回去，否則日誌會改寫到磁碟根目錄。
                app.Services.GetRequiredService<LogLevelRuntimeState>().Initialize();

                var bootstrapSettings = app.Configuration
                    .GetSection(nameof(BootstrapSettings))
                    .Get<BootstrapSettings>() ?? new BootstrapSettings();

                #region 資料庫的 Migration
                //if (!app.Environment.IsDevelopment())
                {
                    // 啟動流程中拋出的例外（migration、seed、RBAC 回填）都歸類為「系統啟動」。
                    // 這時還沒有任何使用者，所以帳號與頁面留空。
                    app.Services.GetRequiredService<ExceptionContextAccessor>()
                        .Set(new ExceptionContext(ExceptionSources.Startup, null, null, null));

                    using var scope = app.Services.CreateScope();
                    using var dbContext = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
                    logger.LogInformation("Ensuring database is ready.");
                    if (dbContext.Database.GetMigrations().Any())
                    {
                        dbContext.Database.Migrate();
                        logger.LogInformation("Database migrations applied successfully.");
                    }
                    else
                    {
                        dbContext.Database.EnsureCreated();
                        logger.LogInformation("Database created because no migrations were found.");
                    }

                    #region 補登上次來不及寫進資料庫的例外（啟動失敗、程序層級例外；LOG-06）
                    var crashMarkers = CrashMarkerStore.ReadAll(exceptionPath);
                    if (crashMarkers.Count > 0)
                    {
                        var exceptionLogService = scope.ServiceProvider.GetRequiredService<ExceptionLogService>();
                        var exceptionAlertService = app.Services.GetRequiredService<ExceptionAlertService>();
                        foreach (var (markerPath, entry) in crashMarkers)
                        {
                            // RecordAsync 絕不拋出；失敗也刪檔，避免每次啟動都重複補登同一筆。
                            var outcome = exceptionLogService.RecordAsync(entry).GetAwaiter().GetResult();
                            CrashMarkerStore.Delete(markerPath);

                            // 補登的都是 Critical（程序結束、啟動失敗），立即告警（LOG-12）。
                            // 寄信佇列此時已可入列，app.Run() 後由寄信背景作業送出。
                            exceptionAlertService.Evaluate(outcome);
                        }

                        logger.LogInformation("Imported {Count} pending crash records.", crashMarkers.Count);
                    }
                    #endregion

                    RoleView? roleViewItemNew = null;

                    #region 是否有存在的角色檢視定義
                    var roleViewItem = dbContext.RoleView
                        .FirstOrDefault(x => x.Name == MagicObjectHelper.預設角色);
                    RolePermissionService RolePermissionService = scope
                        .ServiceProvider
                        .GetRequiredService<RolePermissionService>();
                    var allPermissionJson = RolePermissionService
                        .GetRolePermissionAllNameToJson();
                    if (roleViewItem == null)
                    {
                        roleViewItemNew = new RoleView()
                        {
                            Name = MagicObjectHelper.預設角色,
                            TabViewJson = allPermissionJson
                        };
                        dbContext.RoleView.Add(roleViewItemNew);
                        dbContext.SaveChanges();
                        logger.LogInformation("Seeded default role view.");
                    }
                    else
                    {
                        roleViewItem.TabViewJson = allPermissionJson;
                        dbContext.SaveChanges();
                        logger.LogDebug("Updated existing default role view.");
                    }
                    #endregion

                    #region 產生預設帳號
                    var support = dbContext.MyUser
                        .FirstOrDefault(x => x.Account == bootstrapSettings.SupportAccount);

                    if (support == null)
                    {
                        support = new MyUser()
                        {
                            Account = bootstrapSettings.SupportAccount,
                            Name = bootstrapSettings.SupportName,
                            Email = bootstrapSettings.SupportEmail,
                            IsAdmin = true,
                            Salt = Guid.NewGuid().ToString(),
                            Status = true,
                            RoleViewId = (roleViewItemNew ?? roleViewItem)!.Id,
                        };
                        support.Password =
                            SecurePasswordHasher.HashPassword(bootstrapSettings.SupportPassword);

                        dbContext.MyUser.Add(support);
                        dbContext.SaveChanges();
                        logger.LogInformation("Seeded default support user.");
                    }
                    else
                    {
                        if (SecurePasswordHasher.VerifyPassword(bootstrapSettings.SupportPassword, support.Password, support.Salt)
                            != PasswordVerificationOutcome.Success)
                        {
                            support.Password =
                                SecurePasswordHasher.HashPassword(bootstrapSettings.SupportPassword);
                        }
                        support.IsAdmin = true;
                        if (roleViewItemNew != null)
                            support.RoleViewId = roleViewItemNew.Id;
                        else
                            support.RoleViewId = roleViewItem!.Id;
                        dbContext.SaveChanges();
                        logger.LogDebug("Updated existing support user seed data.");
                    }
                    #endregion

                    #region RBAC 回填（將既有權限資料填入新關聯表，冪等；失敗不中止啟動）
                    try
                    {
                        var rbacBackfill = scope.ServiceProvider.GetRequiredService<IRbacBackfillService>();
                        rbacBackfill.RunAsync().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "RBAC backfill failed at startup.");
                    }
                    #endregion
                }
                #endregion

                // 啟動流程結束，清掉情境；之後每個請求／circuit 互動會自己設定。
                app.Services.GetRequiredService<ExceptionContextAccessor>().Clear();

                #region 註冊中介軟體
                // Configure the HTTP request pipeline.
                if (!app.Environment.IsDevelopment())
                {
                    app.UseExceptionHandler("/Error");
                    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                    app.UseHsts();
                }

                app.UseConfiguredForwardedHeaders();
                app.UseSecurityHeaders();
                app.UseConfiguredSwagger(logger);
                app.UseHttpRequestLogging<Program>();

                // ⚠️ 這個中介軟體只會對「沒有 body」的錯誤狀態碼重跑 /not-found。
                // 對 API 而言那是災難：它會拿原始的 POST + JSON 去執行 Blazor 頁面，
                // 被 antiforgery 擋下後回給呼叫端 400 HTML，真正的狀態碼就此消失。
                // 因此**每一條 API 錯誤路徑都必須自己寫入 ApiResult body**
                // （JWT 的 401/403 事件、ApiExceptionFilter、ApiValidationFilter、
                // HasPermissionAttribute、限流的 OnRejected 都已如此）。
                app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
                app.UseHttpsRedirection();

                app.UseConfiguredLocalization();
                app.UseConfiguredCors();
                app.UseRateLimiter();

                app.UseAntiforgery();

                app.MapStaticAssets();

                app.UseAuthentication();
                app.UseAuthorization();

                // 把已驗證的使用者補進例外情境，讓內層的 LogError 帶得出帳號（LOG-01）。
                // ⚠️ 必須在 UseAuthorization 之後：JWT 端點的 User 要到授權中介軟體才驗證出來。
                app.UseExceptionContextUser();

                #region 綁定靜態資源
                // ⚠️ 一定要放在 UseAuthentication/UseAuthorization 之後。
                // 0.4.34 之前它掛在兩者之前，等於 DownloadPath 目錄匿名可讀 ——
                // 與 ProjectFileController 費心做的權限 + 團隊守門 + 稽核軌跡方向相反。
                app.UseConfiguredDownloadStaticFiles(systemSettings);
                #endregion

                // 預設拒絕：Controller 未明確標註授權就不給進。
                // 刻意「只」套在 Controller 上，不用 AuthorizationOptions.FallbackPolicy。
                // Blazor 頁面另有自己的「預設需登入」機制（0.9.41 起）：
                // Components/Pages/_Imports.razor 對整個 Pages/ 標 [Authorize]，
                // 登入頁等匿名頁面各自標 [AllowAnonymous]，白名單由 PageAuthorizationConventionTests 守門。
                // 未登入的頁面請求因此在上面的 UseAuthorization 就被 302 到登入頁，
                // 瀏覽器拿不到任何頁面內容。
                app.MapControllers()
                    .RequireRateLimiting("api")
                    .RequireAuthorization();
                app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
                {
                    Predicate = check => check.Tags.Contains("live")
                });
                app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
                {
                    Predicate = check => check.Tags.Contains("ready")
                });
                app.MapRazorComponents<App>()
                    .AddInteractiveServerRenderMode();
                #endregion

                logger.LogInformation("Application startup completed. Listening for requests.");
                app.Run();

                void EnsureDirectoryExists(string? directoryPath, string directoryName)
                {
                    if (string.IsNullOrWhiteSpace(directoryPath))
                    {
                        return;
                    }

                    if (Directory.Exists(directoryPath))
                    {
                        return;
                    }

                    Directory.CreateDirectory(directoryPath);
                    logger?.LogInformation("Created {DirectoryName} directory at {DirectoryPath}", directoryName, directoryPath);
                }
            }
            // ⚠️ HostAbortedException 不是失敗：dotnet ef（migration 工具）執行 Main 取得 host 後，
            // 會在 Build() 丟出它來中止程式。不排除的話，每次產生 migration 都會留下一筆假的「啟動失敗」。
            catch (Exception ex) when (ex is not HostAbortedException)
            {
                // 例外紀錄的背景寫入器要到 app.Run() 才啟動，程序隨即結束，入列的紀錄寫不進資料庫。
                // 先寫補登檔，下次成功啟動時補進系統例外紀錄（LOG-06）。
                CrashMarkerStore.Write(
                    exceptionPath,
                    ex,
                    ExceptionSources.Startup,
                    "Application is stopping because of an unhandled exception.",
                    typeof(Program).FullName!);

                if (logger != null)
                {
                    // 抑制例外紀錄的收錄：已有補登檔，若剛好寫入器在跑，會變成兩列。日誌檔照常寫。
                    using var suppression = exceptionContextAccessor?.Suppress();
                    logger.LogCritical(ex, "Application is stopping because of an unhandled exception.");
                }
                else if (nlogReady)
                {
                    // host 尚未建立（設定驗證、服務註冊、Build 失敗），ILogger 還不存在，直接經 NLog 寫檔。
                    LogManager.GetLogger(typeof(Program).FullName!)
                        .Fatal(ex, "Application failed to start before the host was built.");
                }

                throw;
            }
            finally
            {
                if (logger != null)
                    logger.LogInformation("Application is shutting down.");
                LogManager.Shutdown();
            }
        }
    }
}
