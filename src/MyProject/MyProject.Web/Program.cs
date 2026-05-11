using Microsoft.AspNetCore.Authentication.Cookies;
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
using MyProject.Web.Auth;
using MyProject.Web.Components;
using MyProject.Web.Components.Layout;
using MyProject.Web.Configuration;
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
            try
            {
                var builder = WebApplication.CreateBuilder(args);
                StartupSafetyValidator.Validate(builder.Configuration, builder.Environment.EnvironmentName);

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
                builder.Host.UseNLog();
                #endregion

                #region 系統使用服務
                // Add services to the container.
                builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

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

                #region 加入使用 Cookie & JWT 認證需要的宣告
                builder.Services.Configure<CookiePolicyOptions>(options =>
                {
                    options.CheckConsentNeeded = context => true;
                    options.MinimumSameSitePolicy = Microsoft.AspNetCore.Http.SameSiteMode.None;
                });

                var jwtSettings = builder.Configuration
                    .GetSection(JwtSettings.SectionName)
                    .Get<JwtSettings>() ?? new JwtSettings();
                builder.Services
                    .AddOptions<JwtSettings>()
                    .Bind(builder.Configuration.GetSection(JwtSettings.SectionName))
                    .ValidateDataAnnotations()
                    .ValidateOnStart();

                builder.Services.AddAuthentication(MagicObjectHelper.CookieScheme)
                    .AddCookie(MagicObjectHelper.CookieScheme, options =>
                    {
                        options.Cookie.IsEssential = true;
                        options.LoginPath = "/Auths/Login";
                        options.LogoutPath = "/Auths/Logout";
                        options.AccessDeniedPath = "/Auths/Login";
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
                builder.Services.AddAuthorization();
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
                EnsureDirectoryExists(systemSettings.ExternalFileSystem.TaskFilePath, "task file");
                EnsureDirectoryExists(systemSettings.ExternalFileSystem.MeetingFilePath, "meeting file");
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
                var bootstrapSettings = app.Configuration
                    .GetSection(nameof(BootstrapSettings))
                    .Get<BootstrapSettings>() ?? new BootstrapSettings();

                #region 資料庫的 Migration
                //if (!app.Environment.IsDevelopment())
                {
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
                            PasswordHelper.GetPasswordSHA(support.Salt ?? string.Empty, bootstrapSettings.SupportPassword);

                        dbContext.MyUser.Add(support);
                        dbContext.SaveChanges();
                        logger.LogInformation("Seeded default support user.");
                    }
                    else
                    {
                        support.Password =
                            PasswordHelper.GetPasswordSHA(support.Salt ?? string.Empty, bootstrapSettings.SupportPassword);
                        support.IsAdmin = true;
                        if (roleViewItemNew != null)
                            support.RoleViewId = roleViewItemNew.Id;
                        else
                            support.RoleViewId = roleViewItem!.Id;
                        dbContext.SaveChanges();
                        logger.LogDebug("Updated existing support user seed data.");
                    }
                    #endregion
                }
                #endregion

                #region 註冊中介軟體
                // Configure the HTTP request pipeline.
                if (!app.Environment.IsDevelopment())
                {
                    app.UseExceptionHandler("/Error");
                    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                    app.UseHsts();
                }

                app.UseConfiguredForwardedHeaders();
                app.UseConfiguredSwagger(logger);
                app.UseHttpRequestLogging<Program>();

                app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
                app.UseHttpsRedirection();

                app.UseConfiguredLocalization();
                app.UseConfiguredCors();
                app.UseRateLimiter();

                app.UseAntiforgery();

                app.MapStaticAssets();

                #region 綁定靜態資源
                app.UseConfiguredDownloadStaticFiles(systemSettings);
                #endregion

                app.UseAuthentication();
                app.UseAuthorization();

                app.MapControllers()
                    .RequireRateLimiting("api");
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
            catch (Exception ex)
            {
                if (logger != null)
                    logger.LogError(ex, "Stopped program because of an exception");
                throw;
            }
            finally
            {
                if(logger!=null)
                    logger.LogInformation("Application is shutting down.");
                LogManager.Shutdown();
            }
        }
    }
}
