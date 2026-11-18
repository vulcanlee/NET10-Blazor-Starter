using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MyProject.Dtos.Admins;
using MyProject.Share.Helpers;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Components.Commons;
using MyProject.Web.Diagnostics;

namespace MyProject.Web.Client.Views.Analytics
{
    /// <summary>
    /// 日誌等級設定（0.10.5 起在 WASM 執行，WASM 遷移 M6）。畫面、確認窗與提示訊息照 Server 版，
    /// 讀取、套用、還原改走 <see cref="LogLevelApi"/>（只限管理員）；稽核由伺服器寫（與 Server 版相同的動作與內容）。
    /// </summary>
    public partial class LogLevelSettingView
    {
        private static readonly (LogLevelRank Level, string Description)[] LevelOptions =
        [
            (LogLevelRank.Trace, "最詳細，含框架層級細節"),
            (LogLevelRank.Debug, "含查詢條件、換頁等排錯資訊"),
            (LogLevelRank.Info, "含使用者操作軌跡，日常建議值"),
            (LogLevelRank.Warn, "僅警告以上"),
            (LogLevelRank.Error, "僅錯誤以上"),
            (LogLevelRank.Fatal, "僅嚴重錯誤"),
        ];

        [Inject]
        public LogLevelApi Api { get; set; } = default!;

        [Inject]
        public PagePermissions Permissions { get; set; } = default!;

        [Inject]
        public ModalService ModalService { get; set; } = default!;

        [Inject]
        public MessageService MessageService { get; set; } = default!;

        [Inject]
        public ILogger<LogLevelSettingView> Logger { get; set; } = default!;

        private LogLevelRank selectedLevel = LogLevelRank.Info;
        private LogLevelRank effectiveLevel = LogLevelRank.Info;
        private LogLevelRank systemDefaultLevel = LogLevelRank.Info;
        private bool hasOverride;
        private bool isAvailable;

        private string RoleMessage = string.Empty;
        private bool isAccessChecked;

        /// <summary>前端診斷記錄可以選的時間（分鐘），與伺服器 <c>ClientDiagnosticsRegistry.DurationMinutes</c> 相同。</summary>
        private static readonly int[] DiagnosticsDurations = [15, 30, 60, 120, 240, 480];

        private string diagnosticsAccount = string.Empty;
        private string diagnosticsLevel = "DEBUG";
        private int diagnosticsMinutes = 60;
        private List<ClientDiagnosticsSessionDto> diagnosticsSessions = [];

        protected override async Task OnInitializedAsync()
        {
            // 此頁為管理員專屬：權限鍵刻意未上架角色矩陣，以 IsAdmin 直接判斷。權限未通過前不讀取任何設定資訊；
            // 沒有權限的開頁稽核由伺服器在開頁紀錄時寫（shell/page-view）。
            if (!Permissions.IsAdmin)
            {
                isAccessChecked = true;
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                Logger.LogWarning("Log level setting denied because the current user is not an administrator.");
                return;
            }

            // 每次進頁與每次操作後都重讀真實狀態，不快取（伺服器的 Singleton 才是唯一來源）。
            var result = await Api.GetAsync();
            isAccessChecked = true;
            if (!result.Success || result.Data is not { } state)
            {
                RoleMessage = result.UserMessage();
                Logger.LogWarning("Failed to load the log level state. Message={Message}", result.UserMessage());
                return;
            }

            ApplyState(state);
            selectedLevel = effectiveLevel;
            await LoadDiagnosticsAsync();
        }

        private static string DurationText(int minutes) => minutes < 60 ? $"{minutes} 分鐘" : $"{minutes / 60} 小時";

        private async Task LoadDiagnosticsAsync()
        {
            var result = await Api.ClientDiagnosticsAsync();
            if (result.Success && result.Data is { } sessions)
            {
                diagnosticsSessions = sessions;
                return;
            }

            Logger.LogWarning("Failed to load the client diagnostics. Message={Message}", result.UserMessage());
        }

        private async Task OnStartDiagnosticsAsync()
        {
            // 伺服器開啟並寫稽核（LogLevel.ClientDiagnosticsStart）；同一位使用者已經開著就以這次的設定取代。
            Logger.LogInformation("Client diagnostics start requested. Level={Level}, Minutes={Minutes}", diagnosticsLevel, diagnosticsMinutes);
            var result = await Api.StartClientDiagnosticsAsync(new ClientDiagnosticsStartRequestDto
            {
                Account = diagnosticsAccount.Trim(),
                Level = diagnosticsLevel,
                DurationMinutes = diagnosticsMinutes,
            });
            if (!result.Success || result.Data is not { } session)
            {
                _ = MessageService.ErrorAsync(result.UserMessage());
                return;
            }

            diagnosticsAccount = string.Empty;
            await LoadDiagnosticsAsync();
            _ = MessageService.SuccessAsync($"已對 {session.Account} 開啟前端診斷記錄（{session.Level}，至 {session.ExpiresAtLocal:HH:mm}）");
        }

        private async Task OnStopDiagnosticsAsync(ClientDiagnosticsSessionDto session)
        {
            // 伺服器關閉並寫稽核（LogLevel.ClientDiagnosticsStop）；已經到期的回 409，照樣重讀清單。
            Logger.LogInformation("Client diagnostics stop requested. UserId={UserId}", session.UserId);
            var result = await Api.StopClientDiagnosticsAsync(session.UserId);
            await LoadDiagnosticsAsync();
            if (!result.Success)
            {
                _ = MessageService.ErrorAsync(result.UserMessage());
                return;
            }

            _ = MessageService.SuccessAsync($"已關閉 {session.Account} 的前端診斷記錄");
        }

        private void ApplyState(LogLevelStateDto state)
        {
            isAvailable = state.IsAvailable;
            effectiveLevel = LogLevelRankHelper.FromLevelText(state.EffectiveLevel, LogLevelRank.Info);
            systemDefaultLevel = LogLevelRankHelper.FromLevelText(state.SystemDefaultLevel, LogLevelRank.Info);
            hasOverride = state.HasOverride;
        }

        private async Task OnApplyAsync()
        {
            var target = selectedLevel;

            // 只有調低到會暴增日誌量的等級才需要確認；調高不會有這個風險。
            if (target is LogLevelRank.Trace or LogLevelRank.Debug)
            {
                var confirmed = await ConfirmDialog.AskAsync(
                    ModalService,
                    "確認調整日誌等級",
                    $"調整為 {LogLevelRankHelper.ToLevelText(target)} 會大幅增加日誌量，"
                        + "請在排查完問題後記得調回來。確定要套用嗎？",
                    "套用");

                if (confirmed == false)
                {
                    Logger.LogDebug("Log level change cancelled by user. Target={Target}", target);
                    return;
                }
            }

            // 伺服器套用並寫稽核（LogLevel.Apply）。
            var result = await Api.ApplyAsync(LogLevelRankHelper.ToLevelText(target));
            if (!result.Success || result.Data is not { } state)
            {
                _ = MessageService.ErrorAsync(result.UserMessage());
                return;
            }

            ApplyState(state);
            selectedLevel = effectiveLevel;
            _ = MessageService.SuccessAsync($"已套用日誌等級：{LogLevelRankHelper.ToLevelText(target)}");
        }

        private async Task OnRestoreAsync()
        {
            // 伺服器還原並寫稽核（LogLevel.Restore）。
            var result = await Api.RestoreAsync();
            if (!result.Success || result.Data is not { } state)
            {
                _ = MessageService.ErrorAsync(result.UserMessage());
                return;
            }

            ApplyState(state);
            selectedLevel = effectiveLevel;
            _ = MessageService.SuccessAsync(
                $"已還原為系統預設等級：{LogLevelRankHelper.ToLevelText(effectiveLevel)}");
        }
    }
}
