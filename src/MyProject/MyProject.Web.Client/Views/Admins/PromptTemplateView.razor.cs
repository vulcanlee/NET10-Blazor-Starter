using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using MyProject.Dtos.Admins;
using MyProject.Share.Helpers;
using MyProject.Web.Client.Api;
using MyProject.Web.Client.Auth;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Client.Views.Admins
{
    /// <summary>
    /// AI 提示詞（0.9.108 起；0.10.5 起在 WASM 執行，WASM 遷移 M6）：每種範本修改分析指示存成新版本、檢視與切換歷史版本。
    /// 管理員專屬：權限鍵刻意不進角色矩陣，以 IsAdmin 判斷。資料改走 <see cref="PromptTemplateApi"/>：
    /// 種類、標題、內建預設與固定附加的規則都由伺服器帶來（固定規則只顯示、不能改），存檔與切換的稽核由伺服器寫。
    /// </summary>
    public partial class PromptTemplateView
    {
        /// <summary>第一個頁籤（日誌分析）的鍵；種類清單由伺服器帶來。</summary>
        private const string FirstKey = "LogAnalysis";

        [Inject]
        public PromptTemplateApi Api { get; set; } = default!;

        [Inject]
        public PagePermissions Permissions { get; set; } = default!;

        [Inject]
        public ModalService modalService { get; set; } = default!;

        [Inject]
        public NotificationService notificationService { get; set; } = default!;

        [Inject]
        public ILogger<PromptTemplateView> logger { get; set; } = default!;

        /// <summary>全部種類（依頁籤順序），每種含版本紀錄、內建預設與固定規則。</summary>
        private List<PromptTemplateDto> templates = [];
        private string activeKey = FirstKey;
        private bool isLoading;
        private string RoleMessage = string.Empty;
        private bool isAccessChecked;

        private bool modalVisible;
        private string editingKey = FirstKey;
        private int? editingBaseVersion;
        private PromptEditModel editing = new();
        private string openedSnapshot = string.Empty;
        private string errorMessage = string.Empty;

        private bool viewVisible;
        private string viewTitle = string.Empty;
        private string viewContent = string.Empty;

        protected override async Task OnInitializedAsync()
        {
            isAccessChecked = true;

            // 沒有權限的開頁稽核由伺服器在開頁紀錄時寫（shell/page-view）；權限未通過前不呼叫任何 API。
            if (!Permissions.IsAdmin)
            {
                RoleMessage = MagicObjectHelper.你沒有權限存取此頁面;
                logger.LogWarning("Prompt template view denied because the current user is not an administrator.");
                return;
            }

            await ReloadAsync();
        }

        private PromptTemplateDto? TemplateOf(string key) => templates.FirstOrDefault(x => x.Key == key);

        private string TitleOf(string key) => TemplateOf(key)?.Title ?? key;

        /// <summary>重新讀取全部種類；回傳是否成功（失敗時已顯示通知）。</summary>
        private async Task<bool> ReloadAsync()
        {
            isLoading = true;
            try
            {
                var result = await Api.GetAllAsync();
                if (result.Success && result.Data is { } data)
                {
                    templates = data;
                    return true;
                }

                logger.LogWarning("Failed to load prompt templates. Message={Message}", result.UserMessage());
                ViewNotification.Error(notificationService, $"讀取 AI 提示詞失敗：{result.UserMessage()}");
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

        /// <summary>版本紀錄：所有版本（新到舊），最後一列是內建預設。</summary>
        private static List<PromptHistoryRow> RowsOf(PromptTemplateDto template)
        {
            var rows = template.Versions.Select(x => new PromptHistoryRow(x.Version, x.Content, x.IsActive, x.Note, x.CreatedByAccount, x.CreatedAt)).ToList();
            rows.Add(new PromptHistoryRow(null, template.DefaultInstructions, !template.Versions.Any(x => x.IsActive), "程式內建", null, null));
            return rows;
        }

        /// <summary>「第 N 版 · 建立者 · 時間 · 備註」，沒有的部分省略（升級匯入的版本沒有建立者）。</summary>
        private static string ActiveSummary(PromptTemplateVersionDto version)
            => string.Join(" · ", new[] { $"第 {version.Version} 版", version.CreatedByAccount, version.CreatedAt.ToString("yyyy-MM-dd HH:mm"), version.Note }
                .Where(x => !string.IsNullOrEmpty(x)));

        private void OnEdit(PromptTemplateDto template)
        {
            var active = template.Versions.FirstOrDefault(x => x.IsActive);
            editingKey = template.Key;
            editingBaseVersion = active?.Version;
            editing = new PromptEditModel { Content = active?.Content ?? template.DefaultInstructions.ReplaceLineEndings("\n") };
            openedSnapshot = Snapshot(editing);
            errorMessage = string.Empty;
            modalVisible = true;
        }

        private async Task OnModalOkAsync()
        {
            // 第一行先把 Visible 搶回來（見 FormModalFlow）。
            modalVisible = true;
            modalVisible = await FormModalFlow.RunOkAsync(SaveAsync, logger, notificationService, "儲存 AI 提示詞時發生未預期的錯誤，請稍後再試或聯絡系統管理員。");
        }

        private async Task OnModalCancelAsync()
        {
            modalVisible = true;
            modalVisible = await FormModalFlow.ConfirmCloseAsync(modalService, Snapshot(editing) != openedSnapshot);
        }

        /// <summary>存成新版本（建立者由伺服器取目前登入的帳號；伺服器寫 <c>Prompt.Update</c> 稽核）。</summary>
        private async Task<bool> SaveAsync()
        {
            errorMessage = string.Empty;
            var result = await Api.SaveAsync(editingKey, new PromptTemplateSaveRequestDto
            {
                Content = editing.Content ?? string.Empty,
                Note = editing.Note,
                ExpectedActiveVersion = editingBaseVersion,
            });
            if (!result.Success || result.Data is not { } saved)
            {
                errorMessage = result.UserMessage();
                return false;
            }

            ViewNotification.Warning(notificationService, $"已存成第 {saved.Version} 版，下一次 AI 分析就會使用。");
            await ReloadAsync();
            return true;
        }

        private void OnView(string key, PromptHistoryRow row)
        {
            viewTitle = $"{TitleOf(key)}：{(row.Version is { } v ? $"第 {v} 版" : "內建預設")}";
            viewContent = row.Content;
            viewVisible = true;
        }

        private async Task OnActivateAsync(string key, PromptHistoryRow row)
        {
            try
            {
                var label = row.Version is { } v ? $"第 {v} 版" : "內建預設";
                var ok = await ConfirmDialog.AskAsync(modalService, "切換提示詞", $"要讓「{TitleOf(key)}」改用{label}嗎？下一次 AI 分析就會使用。", "切換");
                if (!ok)
                {
                    return;
                }

                var result = await Api.ActivateAsync(key, row.Version);
                if (!result.Success)
                {
                    ViewNotification.Caution(notificationService, result.Message);
                    await ReloadAsync();
                    return;
                }

                ViewNotification.Warning(notificationService, $"「{TitleOf(key)}」已改用{label}。");
                await ReloadAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled exception while activating a prompt template.");
                ViewNotification.UnexpectedError(notificationService, "切換 AI 提示詞時發生未預期的錯誤，請稍後再試或聯絡系統管理員。", ex);
            }
        }

        private static string Snapshot(PromptEditModel x) => string.Join('|', x.Content, x.Note);
    }

    /// <summary>編輯窗的欄位。</summary>
    public sealed class PromptEditModel
    {
        public string? Content { get; set; }

        public string? Note { get; set; }
    }

    /// <summary>版本紀錄的一列；<see cref="Version"/> 為 null 代表程式內建的預設。</summary>
    public sealed record PromptHistoryRow(int? Version, string Content, bool IsActive, string? Note, string? CreatedByAccount, DateTime? CreatedAt);
}
