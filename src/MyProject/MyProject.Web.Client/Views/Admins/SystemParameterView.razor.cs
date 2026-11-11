using System.Globalization;
using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MyProject.Dtos.Admins;
using MyProject.Share.Helpers;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Components.Commons;
using MyProject.Web.Configuration.Parameters;

namespace MyProject.Web.Client.Views.Admins
{
    /// <summary>
    /// 系統參數（0.9.98 起；0.10.6 起在 WASM 執行，WASM 遷移 M7）：在畫面上修改保留天數、監控門檻、系統名稱等非機密參數，存檔後不需重啟即生效。
    /// 管理員專屬：權限鍵刻意不進角色矩陣，以 IsAdmin 判斷。資料改走 <see cref="SystemParameterApi"/>：
    /// 參數定義由伺服器的目錄帶來，驗證（型別、範圍、長度、空白、所有設定驗證器）、套用與稽核都在伺服器；
    /// 值的顯示與「會不會縮短保留期限」與伺服器共用 <see cref="SystemParameterValueCodec"/>。
    /// </summary>
    public partial class SystemParameterView
    {
        [Inject]
        public SystemParameterApi Api { get; set; } = default!;

        [Inject]
        public PagePermissions Permissions { get; set; } = default!;

        [Inject]
        public ModalService modalService { get; set; } = default!;

        [Inject]
        public NotificationService notificationService { get; set; } = default!;

        [Inject]
        public ILogger<SystemParameterView> logger { get; set; } = default!;

        private SystemParameterOverviewDto? overview;
        private bool isLoading;

        /// <summary>要捲到的分類（例如「資料保留」）；「資料保留與清理」頁的「修改」帶 <c>?group=</c> 進來（0.9.133 起）。</summary>
        [Parameter]
        public string? FocusGroup { get; set; }

        private ElementReference focusGroupHeading;
        private bool focusDone;
        private string RoleMessage = string.Empty;
        private bool isAccessChecked;

        private bool modalVisible;
        private SystemParameterItemDto? editing;
        private ParameterEditModel editForm = new();
        private ParameterEditModel openedForm = new();
        private string editErrorMessage = string.Empty;

        private IEnumerable<IGrouping<string, SystemParameterItemDto>> Groups
            => (overview?.Items ?? []).GroupBy(x => x.Definition.Group);

        private int NotAppliedCount => overview?.Items.Count(x => x.Source == SystemParameterSource.NotApplied) ?? 0;

        protected override async Task OnInitializedAsync()
        {
            isAccessChecked = true;

            // 沒有權限的開頁稽核由伺服器在開頁紀錄時寫（shell/page-view）；權限未通過前不呼叫任何 API。
            if (!Permissions.IsAdmin)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                logger.LogWarning("System parameter view denied because the current user is not an administrator.");
                return;
            }

            await ReloadAsync();
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            // 資料載入後分類標題才出現：等它渲染出來再聚焦一次（聚焦會把它捲進畫面），之後的重新整理不再搶捲動位置。
            if (focusDone || string.IsNullOrEmpty(FocusGroup) || focusGroupHeading.Context is null)
            {
                return;
            }

            focusDone = true;
            try
            {
                await focusGroupHeading.FocusAsync();
            }
            catch (Exception ex) when (ex is Microsoft.JSInterop.JSException or TaskCanceledException)
            {
                logger.LogDebug(ex, "Failed to focus the system parameter group. Group={Group}", FocusGroup);
            }
        }

        /// <summary>重新讀取；回傳是否成功（失敗時已顯示通知）。</summary>
        private async Task<bool> ReloadAsync()
        {
            isLoading = true;
            try
            {
                var result = await Api.GetOverviewAsync();
                if (result.Success && result.Data is { } data)
                {
                    overview = data;
                    return true;
                }

                logger.LogWarning("Failed to load system parameters. Message={Message}", result.UserMessage());
                ViewNotification.Error(notificationService, $"讀取系統參數失敗：{result.UserMessage()}");
                return false;
            }
            finally
            {
                isLoading = false;
            }
        }

        private async Task OnRefreshAsync()
        {
            if (await ReloadAsync())
            {
                ViewNotification.Info(notificationService, "已更新最新資料");
            }
        }

        private void OnEdit(SystemParameterItemDto item)
        {
            editing = item;
            editErrorMessage = string.Empty;
            var current = SystemParameterValueCodec.Canonical(item.Definition, item.EffectiveValue) ?? string.Empty;
            editForm = new ParameterEditModel
            {
                Text = current,
                Number = int.TryParse(current, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : 0,
                Flag = current == "true",
            };
            openedForm = editForm with { };
            modalVisible = true;
        }

        private async Task OnModalOkAsync()
        {
            // 第一行先把 Visible 搶回來（見 FormModalFlow）。
            modalVisible = true;
            modalVisible = await FormModalFlow.RunOkAsync(SaveAsync, logger, notificationService, "儲存系統參數時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
        }

        private async Task OnModalCancelAsync()
        {
            modalVisible = true;
            modalVisible = await FormModalFlow.ConfirmCloseAsync(modalService, editForm != openedForm);
        }

        /// <summary>存檔（伺服器驗證、套用並寫 <c>SystemParameter.Update</c>／<c>Reset</c> 稽核）；驗證失敗的訊息顯示在窗內。</summary>
        private async Task<bool> SaveAsync()
        {
            if (editing is not { } item)
            {
                return true;
            }

            editErrorMessage = string.Empty;
            var definition = item.Definition;
            var raw = definition.Kind switch
            {
                SystemParameterKind.Int => editForm.Number.ToString(CultureInfo.InvariantCulture),
                SystemParameterKind.Bool => editForm.Flag ? "true" : "false",
                _ => editForm.Text,
            };

            if (definition.IsRetention && SystemParameterValueCodec.ShortensRetention(item.EffectiveValue, raw))
            {
                var ok = await ConfirmDialog.AskDestructiveAsync(
                    modalService,
                    "縮短保留期限",
                    $"「{definition.Label}」將從 {SystemParameterValueCodec.Display(definition, item.EffectiveValue)} 改為 {SystemParameterValueCodec.Display(definition, raw)}。" +
                    $"下一次執行時（{definition.EffectNote}）會永久刪除超出新設定的資料，無法復原。確定要儲存嗎？",
                    "確定縮短");
                if (!ok)
                {
                    return false;
                }
            }

            var result = await Api.SaveAsync(definition.Key, raw, item.Override?.ConcurrencyStamp);
            if (!result.Success)
            {
                editErrorMessage = result.UserMessage();
                return false;
            }

            ViewNotification.Warning(notificationService, result.Message);
            await ReloadAsync();
            return true;
        }

        private async Task OnResetAsync(SystemParameterItemDto item)
        {
            if (item.Override is not { } row)
            {
                return;
            }

            try
            {
                var definition = item.Definition;
                var content = $"「{definition.Label}」將改回設定檔的值 {SystemParameterValueCodec.Display(definition, item.BaseValue)}。";
                var ok = definition.IsRetention && SystemParameterValueCodec.ShortensRetention(item.EffectiveValue, item.BaseValue)
                    ? await ConfirmDialog.AskDestructiveAsync(modalService, "還原為設定檔值",
                        content + "這會縮短保留期限，下一次執行時會永久刪除超出設定的資料，無法復原。", "確定還原")
                    : await ConfirmDialog.AskAsync(modalService, "還原為設定檔值", content, "還原");
                if (!ok)
                {
                    return;
                }

                var result = await Api.ResetAsync(definition.Key, row.ConcurrencyStamp);
                if (!result.Success)
                {
                    ViewNotification.Caution(notificationService, result.UserMessage());
                    await ReloadAsync();
                    return;
                }

                ViewNotification.Warning(notificationService, result.Message);
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while resetting a system parameter.");
                ViewNotification.UnexpectedError(notificationService, "還原系統參數時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        private async Task OnRemoveOrphanAsync(SystemParameterOrphanDto orphan)
        {
            try
            {
                var ok = await ConfirmDialog.AskDestructiveAsync(modalService, "移除不認得的參數",
                    $"要從資料庫移除「{orphan.ParameterKey}」嗎？它目前沒有作用。", "移除");
                if (!ok)
                {
                    return;
                }

                var result = await Api.ResetAsync(orphan.ParameterKey, orphan.ConcurrencyStamp);
                if (result.Success)
                {
                    ViewNotification.Warning(notificationService, result.Message);
                }
                else
                {
                    ViewNotification.Caution(notificationService, result.UserMessage());
                }

                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while removing an unknown system parameter.");
                ViewNotification.UnexpectedError(notificationService, "移除參數時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        private static string? RangeText(ISystemParameterFormat definition) => SystemParameterValueCodec.RangeText(definition);

        private static string SourceText(SystemParameterSource source) => source switch
        {
            SystemParameterSource.Override => "系統參數",
            SystemParameterSource.NotApplied => "未套用",
            _ => "設定檔",
        };

        private static StatusTone SourceTone(SystemParameterSource source) => source switch
        {
            SystemParameterSource.Override => StatusTone.Accent,
            SystemParameterSource.NotApplied => StatusTone.Warning,
            _ => StatusTone.Muted,
        };

        /// <summary>伺服器已換成本地時間（不帶時差）送來，這裡只格式化。</summary>
        private static string ToLocalText(DateTime local) => local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        /// <summary>修改窗的輸入；依參數型別只用其中一個欄位。record 讓「有沒有改過」可以直接比較。</summary>
        private sealed record ParameterEditModel
        {
            public string Text { get; set; } = string.Empty;

            public int Number { get; set; }

            public bool Flag { get; set; }
        }
    }
}
