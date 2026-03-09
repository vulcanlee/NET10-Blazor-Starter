using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using MyProject.AccessDatas;
using MyProject.AccessDatas.Models;
using MyProject.Business.Helpers;
using MyProject.Business.Services.DataAccess;
using MyProject.Business.Services.Other;
using MyProject.Models.Systems;
using MyProject.Share.Helpers;
using MyProject.Web.Components;
using NLog;
using NLog.Web;

namespace MyProject.Web
{
    public class Program
    {
        public static void Main(string[] args)
        {
            ILogger<Program> logger = null;
            try
            {
                var builder = WebApplication.CreateBuilder(args);

                #region NLog 相關設定
                var nlogBasePrefixPath = builder.Configuration.GetValue<string>("NLog:BasePath");
                var baseNamespace = typeof(Program).Namespace;

                string nlogBasePath = null;
                if (!string.IsNullOrWhiteSpace(nlogBasePrefixPath))
                {
                    nlogBasePath = Path.Combine(nlogBasePrefixPath, baseNamespace);
                    Directory.CreateDirectory(nlogBasePath);

                    // 設置內部日誌記錄器
                    NLog.Common.InternalLogger.LogLevel = NLog.LogLevel.Info;
                    NLog.Common.InternalLogger.LogFile = Path.Combine(nlogBasePath, $"{baseNamespace}-nlog-internal.log");

                    // 設置變量到當前配置
                    LogManager.Configuration.Variables["BasePath"] = nlogBasePath;
                    LogManager.Configuration.Variables["LogFilenamePrefix"] = $"{baseNamespace}-logfile";

                    //LogManager.GetCurrentClassLogger().Info("NLog configured with BasePath: {BasePath}", nlogBasePath);
                }

                builder.Logging.ClearProviders();
                builder.Host.UseNLog();
                logger = builder.Services.BuildServiceProvider().GetRequiredService<ILogger<Program>>();
                #endregion

                #region 系統使用服務
                // Add services to the container.
                builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

                builder.Services.AddControllers();
                //builder.Services.AddOpenApi();
                builder.Services.AddEndpointsApiExplorer();
                builder.Services.AddSwaggerGen();
                builder.Services.AddAntDesign();

                #region 加入使用 Cookie & JWT 認證需要的宣告
                builder.Services.Configure<CookiePolicyOptions>(options =>
                {
                    options.CheckConsentNeeded = context => true;
                    options.MinimumSameSitePolicy = Microsoft.AspNetCore.Http.SameSiteMode.None;
                });

                builder.Services.AddAuthentication(MagicObjectHelper.CookieScheme)
                    .AddCookie(MagicObjectHelper.CookieScheme, options =>
                    {
                        options.LoginPath = "/Auths/Login";
                        options.LogoutPath = "/Auths/Logout";
                        options.AccessDeniedPath = "/Auths/Login";
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
                builder.Services.Configure<SystemSettings>(builder.Configuration
                    .GetSection(nameof(SystemSettings)));
                #endregion

                #region 系統使用的目錄準備
                // 取得 系統設定物件 SystemSettings
                var systemSettings = builder.Configuration.GetSection(nameof(SystemSettings)).Get<SystemSettings>();
                if (string.IsNullOrEmpty(systemSettings.ExternalFileSystem.DatabasePath) == false)
                {
                    if(!Directory.Exists(systemSettings.ExternalFileSystem.DatabasePath))
                    {
                        Directory.CreateDirectory(systemSettings.ExternalFileSystem.DatabasePath);
                    }
                }
                if(string.IsNullOrEmpty(systemSettings.ExternalFileSystem.DownloadPath) == false)
                {
                    if (!Directory.Exists(systemSettings.ExternalFileSystem.DownloadPath))
                    {
                        Directory.CreateDirectory(systemSettings.ExternalFileSystem.DownloadPath);
                    }
                }
                if(string.IsNullOrEmpty(systemSettings.ExternalFileSystem.UploadPath) == false)
                {
                    if (!Directory.Exists(systemSettings.ExternalFileSystem.UploadPath))
                    {
                        Directory.CreateDirectory(systemSettings.ExternalFileSystem.UploadPath);
                    }
                }
                #endregion

                #region EF Core 宣告
                var ctmsSettings = builder.Configuration
                    .GetSection(nameof(SystemSettings))
                    .Get<SystemSettings>();
                var SQLiteDefaultConnection = MagicObjectHelper.GetSQLiteConnectionString(systemSettings.ExternalFileSystem.DatabasePath);

                builder.Services.AddDbContext<BackendDBContext>(options =>
                    options.UseSqlite(SQLiteDefaultConnection),
                    ServiceLifetime.Scoped);
                #endregion

                #region 客製服務註冊
                builder.Services.AddScoped<MyUserServiceLogin>();
                builder.Services.AddScoped<RolePermissionService>();
                builder.Services.AddScoped<RoleViewService>();
                builder.Services.AddScoped<MyUserService>();
                #endregion

                var app = builder.Build();

                #region 資料庫的 Migration
                //if (!app.Environment.IsDevelopment())
                {
                    using var scope = app.Services.CreateScope();
                    using var dbContext = scope.ServiceProvider.GetRequiredService<BackendDBContext>();
                    if (dbContext.Database.GetMigrations().Any())
                    {
                        dbContext.Database.Migrate();
                    }
                    else
                    {
                        dbContext.Database.EnsureCreated();
                    }

                    RoleView roleViewItemNew = null;

                    #region 是否有存在的角色檢視定義
                    var roleViewItem = dbContext.RoleView
                        .FirstOrDefault(x => x.Name == MagicObjectHelper.預設角色);
                    var allPermissionJson = "[]";
                    if (roleViewItem == null)
                    {
                        roleViewItemNew = new RoleView()
                        {
                            Name = MagicObjectHelper.預設角色,
                            TabViewJson = allPermissionJson
                        };
                        dbContext.RoleView.Add(roleViewItemNew);
                        dbContext.SaveChanges();
                    }
                    else
                    {
                        roleViewItem.TabViewJson = allPermissionJson;
                        dbContext.SaveChanges();
                    }
                    #endregion

                    #region 產生預設帳號
                    var support = dbContext.MyUser
                        .FirstOrDefault(x => x.Account == MagicObjectHelper.開發者帳號);

                    if (support == null)
                    {
                        support = new MyUser()
                        {
                            Account = MagicObjectHelper.開發者帳號,
                            Name = MagicObjectHelper.開發者帳號,
                            Email = MagicObjectHelper.開發者帳號,
                            IsAdmin = true,
                            Salt = Guid.NewGuid().ToString(),
                            Status = true,
                            RoleViewId = roleViewItemNew.Id,
                        };
                        support.Password =
                            PasswordHelper.GetPasswordSHA(support.Salt, MagicObjectHelper.開發者帳號);

                        dbContext.MyUser.Add(support);
                        dbContext.SaveChanges();
                    }
                    else
                    {
                        support.Password =
                            PasswordHelper.GetPasswordSHA(support.Salt, MagicObjectHelper.開發者帳號);
                        support.IsAdmin = true;
                        if (roleViewItemNew != null)
                            support.RoleViewId = roleViewItemNew.Id;
                        else
                            support.RoleViewId = roleViewItem.Id;
                        dbContext.SaveChanges();
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

                if (app.Environment.IsDevelopment())
                {
                    //app.MapOpenApi();
                    app.UseSwagger();
                    app.UseSwaggerUI();
                }

                app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
                app.UseHttpsRedirection();

                app.UseAntiforgery();

                app.MapStaticAssets();

                #region 綁定靜態資源
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(systemSettings.ExternalFileSystem.DownloadPath),
                    RequestPath = "/UploadFiles"
                });
                #endregion

                app.UseAuthentication();
                app.UseAuthorization();

                app.MapControllers();
                app.MapRazorComponents<App>()
                    .AddInteractiveServerRenderMode();
                #endregion

                app.Run();
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
