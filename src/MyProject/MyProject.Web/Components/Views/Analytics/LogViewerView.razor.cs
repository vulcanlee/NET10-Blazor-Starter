using System.Text;
using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using MyProject.Business.Services.Other;
using MyProject.Share.Helpers;
using MyProject.Web.Diagnostics;

namespace MyProject.Web.Components.Views.Analytics
{
    public partial class LogViewerView
    {
        private static readonly (string Key, string Label)[] LevelOptions =
        [
            ("", "不限"),
            ("TRACE", "TRACE"),
            ("DEBUG", "DEBUG"),
            ("INFO", "INFO"),
            ("WARN", "WARN"),
            ("ERROR", "ERROR"),
            ("FATAL", "FATAL"),
        ];

        private readonly ILogger<LogViewerView> logger;
        private readonly ILogQueryService logQueryService;
        private readonly MessageService messageService;

        private DateTime? startTime;
        private DateTime? endTime;
        private string minimumLevel = string.Empty;
        private int takeCount = LogQueryRequest.DefaultTake;
        private string keyword = string.Empty;

        private bool isLoading;
        private string statusMessage = string.Empty;
        private List<string> warnings = new();

        /// <summary>查詢結果，時間正序。匯出直接使用此順序。</summary>
        private List<LogEntry> entriesAscending = new();

        /// <summary>畫面顯示用，最新在上。</summary>
        private List<LogEntry> entriesDisplay = new();

        private string RoleMessage = string.Empty;

        [Inject]
        public AuthenticationStateHelper AuthenticationStateHelper { get; set; } = default!;
        [Inject]
        public AuthenticationStateProvider authStateProvider { get; set; } = default!;
        [Inject]
        public NavigationManager NavigationManager { get; set; } = default!;
        [Inject]
        public IJSRuntime JSRuntime { get; set; } = default!;

        public LogViewerView(
            ILogger<LogViewerView> logger,
            ILogQueryService logQueryService,
            MessageService messageService)
        {
            this.logger = logger;
            this.logQueryService = logQueryService;
            this.messageService = messageService;
        }

        protected override async Task OnInitializedAsync()
        {
            var checkResult = await AuthenticationStateHelper.Check(authStateProvider, NavigationManager);
            if (checkResult != AuthenticationCheckResult.Succeeded)
            {
                return;
            }

            // 此頁為管理員專屬：權限鍵刻意未上架角色矩陣，因此以 CheckIsAdmin 直接判斷，
            // 與既有的系統健康監控頁一致。權限未通過前不讀取任何日誌內容。
            if (AuthenticationStateHelper.CheckIsAdmin() == false)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                logger.LogWarning("Log viewer access denied because the current user is not an administrator.");
                return;
            }

            endTime = DateTime.Now;
            startTime = endTime.Value.AddHours(-1);

            await OnQueryAsync();
        }

        private async Task OnQueryAsync()
        {
            isLoading = true;
            statusMessage = string.Empty;
            warnings = new();
            StateHasChanged();

            try
            {
                var request = new LogQueryRequest
                {
                    StartTime = startTime ?? DateTime.Now.AddHours(-1),
                    EndTime = endTime ?? DateTime.Now,
                    Take = takeCount,
                    MinimumLevel = ToRank(minimumLevel),
                    Keyword = keyword,
                };

                var result = await logQueryService.QueryAsync(request);

                entriesAscending = result.Entries.ToList();
                entriesDisplay = Enumerable.Reverse(entriesAscending).ToList();
                statusMessage = result.Message;
                warnings = result.Warnings;

                // 條件被夾住時把實際查詢的區間寫回選擇器，讓畫面與結果一致。
                startTime = result.AppliedStartTime;
                endTime = result.AppliedEndTime;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Log query failed.");
                statusMessage = $"查詢日誌失敗：{ex.GetType().Name}。";
                entriesAscending = new();
                entriesDisplay = new();
            }
            finally
            {
                isLoading = false;
                StateHasChanged();
            }
        }

        private async Task OnExportAsync()
        {
            if (entriesAscending.Count == 0)
            {
                _ = messageService.WarningAsync("目前沒有可匯出的日誌。");
                return;
            }

            try
            {
                // 服務回傳的即為時間正序，直接沿用，不依賴可能解析失敗的 Timestamp 重新排序。
                var text = string.Join(Environment.NewLine, entriesAscending.Select(entry => entry.Raw));

                // 加 BOM，避免記事本／Excel 開啟時繁體中文亂碼。
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(text);

                using var stream = new MemoryStream(bytes);
                using var streamReference = new DotNetStreamReference(stream);

                var fileName = $"MyProject.Web-logs-{DateTime.Now:yyyyMMdd-HHmmss}.log";
                await JSRuntime.InvokeVoidAsync("appFileDownload.downloadFromStream", fileName, streamReference);

                logger.LogInformation(
                    "Log export downloaded. Rows={Rows}, Bytes={Bytes}", entriesAscending.Count, bytes.Length);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Log export failed.");
                _ = messageService.ErrorAsync($"匯出失敗：{ex.GetType().Name}。");
            }
        }

        // 解析使用者的篩選輸入時，空字串代表「不限」，因此 fallback 為 Any。
        private static LogLevelRank ToRank(string level)
            => LogLevelRankHelper.FromLevelText(level, LogLevelRank.Any);

        private static string GetLevelColor(LogLevelRank rank) => rank switch
        {
            LogLevelRank.Fatal => "red",
            LogLevelRank.Error => "red",
            LogLevelRank.Warn => "orange",
            LogLevelRank.Info => "blue",
            LogLevelRank.Debug => "cyan",
            _ => "default",
        };
    }
}
