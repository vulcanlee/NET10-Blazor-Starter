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
                #endregion

                #region 系統使用服務
                // Add services to the container.
                builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

                builder.Services.AddAntDesign();
                #endregion

                #region 加入設定強型別注入宣告
                builder.Services.Configure<SystemSettings>(builder.Configuration
                    .GetSection(MagicObjectHelper.SystemSettings));
                #endregion

                #region 系統使用的目錄準備
                // 取得 系統設定物件 SystemSettings
                var systemSettings = builder.Configuration.GetSection(MagicObjectHelper.SystemSettings).Get<SystemSettings>();
                if (string.IsNullOrEmpty(systemSettings.ExternalFileSystem.DownloadPath) == false)
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
                var sqliteConnectionString = MagicObjectHelper.GetSQLiteConnectionString(systemSettings.ExternalFileSystem.DatabasePath);
                #endregion

                #region 客製服務註冊

                #endregion

                var app = builder.Build();

                #region 註冊中介軟體
                // Configure the HTTP request pipeline.
                if (!app.Environment.IsDevelopment())
                {
                    app.UseExceptionHandler("/Error");
                    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                    app.UseHsts();
                }

                app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
                app.UseHttpsRedirection();

                app.UseAntiforgery();

                app.MapStaticAssets();
                app.MapRazorComponents<App>()
                    .AddInteractiveServerRenderMode();
                #endregion

                app.Run();
            }
            catch (Exception ex)
            {
                LogManager.GetCurrentClassLogger().Error(ex, "Stopped program because of an exception");
                throw;
            }
            finally
            {
                LogManager.Shutdown();
            }
        }
    }
}
