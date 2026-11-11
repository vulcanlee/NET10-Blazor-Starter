using System.Globalization;
using AntDesign;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using MyProject.Dtos.Ai;
using MyProject.Models.AdapterModel;
using MyProject.Web.Ai;
using MyProject.Web.Client.Api;
using MyProject.Web.Components.Commons;

namespace MyProject.Web.Client.Views.Analytics
{
    /// <summary>
    /// AI 對話紀錄的明細視窗（0.9.72 起；0.10.5 起在 WASM 執行）：中繼資料、用量、同一對話，以及五種檢視 ——
    /// 完整對話、送出 Prompt、取得 Response、原始 Request、原始 Response。資料由 <see cref="AiCallLogApi.DetailAsync"/> 取得（伺服器寫查看稽核）。
    ///
    /// ⚠️ AI 產出一律經 <see cref="AiMarkdownRenderer.ToSafeHtml"/>（不可改用 HelpMarkdownRenderer）。
    /// 內容可能數 MB：伺服器每段只送前 <see cref="MaxDisplayCharacters"/> 字元（畫面也只顯示這麼多），只渲染目前的檢視；
    /// 「複製」被截斷的段落時才向伺服器取完整內容。
    /// </summary>
    public partial class AiCallLogDetailModal
    {
        /// <summary>單段畫面顯示上限（完整內容用複製或下載 JSON 取得）。</summary>
        private const int MaxDisplayCharacters = AiCallLogDetailDto.MaxTextCharacters;

        /// <summary>Markdown 渲染後的 HTML 上限；超過就退回純文字（與例外 AI 分析窗相同）。</summary>
        private const int MaxRenderedHtmlLength = 512 * 1024;

        private const int DefaultFontScalePercent = 100;

        private const string ViewConversation = "conversation";
        private const string ViewPrompt = "prompt";
        private const string ViewResponse = "response";
        private const string ViewRawRequest = "raw-request";
        private const string ViewRawResponse = "raw-response";

        private static readonly IReadOnlyList<(string Key, string Label)> Views =
        [
            (ViewConversation, "完整對話"),
            (ViewPrompt, "送出 Prompt"),
            (ViewResponse, "取得 Response"),
            (ViewRawRequest, "原始 Request"),
            (ViewRawResponse, "原始 Response"),
        ];

        /// <summary>與日誌檢視、例外 AI 分析兩個視窗相同的三段字級。</summary>
        private static readonly (int Percent, string Label)[] FontScaleOptions =
        [
            (100, "一般"),
            (125, "中"),
            (150, "大"),
        ];

        [Inject]
        public AiCallLogApi Api { get; set; } = default!;

        [Inject]
        public NotificationService notificationService { get; set; } = default!;

        [Inject]
        public IJSRuntime jsRuntime { get; set; } = default!;

        [Inject]
        public ILogger<AiCallLogDetailModal> logger { get; set; } = default!;

        private readonly Dictionary<string, string> htmlCache = new(StringComparer.Ordinal);

        private bool visible;
        private bool isLoading;
        private bool isExporting;
        private bool renderMarkdown;
        private int fontScalePercent = DefaultFontScalePercent;
        private string activeView = ViewConversation;
        private AiCallLogDetailDto? detail;
        private AiCallLogAdapterModel? currentItem;
        private IReadOnlyList<DisplayBlock> currentBlocks = [];

        private IReadOnlyList<DisplayBlock> CurrentBlocks => currentBlocks;

        private bool IsTextView => activeView is ViewConversation or ViewPrompt or ViewResponse;

        private string FontScaleCss
            => (fontScalePercent / 100.0).ToString("0.##", CultureInfo.InvariantCulture);

        private string ModelText
        {
            get
            {
                var requested = detail?.RequestedModel;
                var actual = detail?.Item.Model ?? string.Empty;
                return string.IsNullOrWhiteSpace(requested) || string.Equals(requested, actual, StringComparison.Ordinal)
                    ? actual
                    : $"{requested} → {actual}";
            }
        }

        /// <summary>「用量與費用」欄（伺服器組好的文字，與 Server 版相同）。</summary>
        private string UsageText => detail?.UsageText ?? string.Empty;

        /// <summary>開啟（或在同一對話間切換到）指定紀錄。</summary>
        public async Task OpenAsync(int id)
        {
            visible = true;
            isLoading = true;
            htmlCache.Clear();
            StateHasChanged();

            try
            {
                var result = await Api.DetailAsync(id);
                if (result.Success && result.Data is { } loaded)
                {
                    detail = loaded;
                    currentItem = AiCallLogDtoMapper.ToModel(loaded.Item);
                    RebuildBlocks();
                }
                else if (result.StatusCode == 404)
                {
                    visible = false;
                    ViewNotification.Caution(notificationService, "找不到這筆對話紀錄（可能已被刪除）。");
                }
                else
                {
                    visible = false;
                    ViewNotification.Error(notificationService, $"讀取對話紀錄失敗：{result.UserMessage()}");
                }
            }
            finally
            {
                isLoading = false;
                StateHasChanged();
            }
        }

        private void Close()
        {
            visible = false;
            detail = null;
            currentItem = null;
            currentBlocks = [];
            htmlCache.Clear();
            fontScalePercent = DefaultFontScalePercent;
        }

        private void SwitchView(string key)
        {
            activeView = key;
            RebuildBlocks();
        }

        private void RebuildBlocks()
            => currentBlocks = detail is { HasContent: true } && currentItem is not null ? BuildBlocks(activeView, detail, currentItem) : [];

        /// <summary>依檢視組出要顯示的區塊。每段都標示角色與方向（送出／取得）。</summary>
        private static List<DisplayBlock> BuildBlocks(string view, AiCallLogDetailDto content, AiCallLogAdapterModel item)
        {
            var blocks = new List<DisplayBlock>();
            var messages = content.Messages;

            if (view is ViewConversation or ViewPrompt && messages is not null)
            {
                for (var index = 0; index < messages.Count; index++)
                {
                    var message = messages[index];
                    var isHistory = message.Role == "assistant";
                    blocks.Add(new DisplayBlock(
                        $"message-{index}",
                        $"#{index + 1} {AiCallLogText.RoleLabel(message.Role)}",
                        isHistory ? "送出（對話歷史）" : "送出",
                        isHistory ? StatusTone.Muted : StatusTone.Accent,
                        RoleClass(message.Role),
                        message.Content.Text,
                        message.Content.Length,
                        AllowMarkdown: true,
                        Note: null));
                }
            }

            if (view is ViewConversation or ViewResponse)
            {
                var response = content.Response ?? new AiCallLogTextDto();
                blocks.Add(new DisplayBlock(
                    "response",
                    "AI 回應（Assistant）",
                    "取得",
                    StatusTone.Positive,
                    "response",
                    response.Text,
                    response.Length,
                    AllowMarkdown: true,
                    response.Length == 0 ? $"未取得內容（{AiCallLogText.DescribeOutcome(item)}）。" : null));
            }

            if (view == ViewRawRequest)
            {
                var request = content.RawRequest ?? new AiCallLogTextDto();
                blocks.Add(new DisplayBlock(
                    "raw-request",
                    "原始 Request（JSON，已排版；不含 HTTP 標頭與金鑰）",
                    "送出",
                    StatusTone.Accent,
                    "raw",
                    request.Text,
                    request.Length,
                    AllowMarkdown: false,
                    Note: null));
            }

            if (view == ViewRawResponse)
            {
                blocks.Add(new DisplayBlock(
                    "raw-response",
                    "原始 Response（JSON，已排版）",
                    "取得",
                    StatusTone.Positive,
                    "raw",
                    content.RawResponse?.Text ?? string.Empty,
                    content.RawResponse?.Length ?? 0,
                    AllowMarkdown: false,
                    content.RawResponse is null ? "沒有收到回應 body（逾時、連線失敗或取消）。" : null));
            }

            return blocks;
        }

        private static string RoleClass(string role) => role switch
        {
            "system" => "system",
            "user" => "user",
            "assistant" => "assistant",
            _ => "other",
        };

        /// <summary>伺服器送來的已是前 <see cref="MaxDisplayCharacters"/> 字元。</summary>
        private static string DisplayText(DisplayBlock block) => block.Text;

        private string RenderHtml(DisplayBlock block)
        {
            if (htmlCache.TryGetValue(block.Key, out var cached))
            {
                return cached;
            }

            var html = AiMarkdownRenderer.ToSafeHtml(DisplayText(block));
            if (html.Length > MaxRenderedHtmlLength)
            {
                // 渲染結果異常龐大就不注入，退回純文字（仍經同一條安全管線）。
                html = AiMarkdownRenderer.ToSafeHtml("（內容過大，已改以純文字顯示；請切回「純文字」或用「複製」取得原文。）");
            }

            htmlCache[block.Key] = html;
            return html;
        }

        private async Task OnCopyAsync(DisplayBlock block)
        {
            try
            {
                var text = block.Text;
                if (block.Length > block.Text.Length && currentItem is not null)
                {
                    // 畫面只拿到前 20 萬字元：複製時才向伺服器取這一段的完整內容（與 Server 版相同，複製的是整段）。
                    var full = await Api.TextAsync(currentItem.Id, block.Key);
                    if (!full.Success || full.Data is null)
                    {
                        ViewNotification.Error(notificationService, $"複製失敗：{full.UserMessage()}");
                        return;
                    }

                    text = full.Data.Text;
                }

                var copied = await jsRuntime.InvokeAsync<bool>("appClipboard.copyText", text);
                if (copied)
                {
                    ViewNotification.Info(notificationService, $"已複製「{block.Heading}」（{block.Length:N0} 字元）。");
                }
                else
                {
                    ViewNotification.Caution(notificationService, "瀏覽器拒絕寫入剪貼簿，請改用下載 JSON。");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to copy AI call log block.");
                ViewNotification.UnexpectedError(notificationService, $"複製失敗：{ex.GetType().Name}。", ex);
            }
        }

        private async Task OnDownloadJsonAsync()
        {
            if (currentItem is null)
            {
                return;
            }

            isExporting = true;
            try
            {
                // 伺服器送內容檔原文（UTF-8 不加 BOM）並寫匯出稽核。
                var outcome = await Api.RawAsync(currentItem.Id);
                if (!outcome.Success)
                {
                    if (outcome.StatusCode == 404)
                    {
                        ViewNotification.Caution(notificationService, outcome.Message);
                    }
                    else
                    {
                        ViewNotification.Error(notificationService, $"下載失敗：{outcome.Message}");
                    }

                    return;
                }

                await jsRuntime.DownloadFileAsync(outcome.FileName!, outcome.Content!, "application/json");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AI call log download failed. CallLogId={CallLogId}", currentItem.Id);
                ViewNotification.UnexpectedError(notificationService, $"下載失敗：{ex.GetType().Name}。", ex);
            }
            finally
            {
                isExporting = false;
            }
        }

        private async Task OnExportPdfAsync()
        {
            if (detail is not { HasContent: true } || currentItem is null)
            {
                return;
            }

            isExporting = true;
            StateHasChanged();

            try
            {
                // 伺服器排版 PDF（產生時間、匯出者由伺服器決定）並寫匯出稽核。
                var outcome = await Api.PdfAsync(currentItem.Id);
                if (!outcome.Success)
                {
                    ViewNotification.Error(notificationService, outcome.Message);
                    return;
                }

                await jsRuntime.DownloadFileAsync(outcome.FileName!, outcome.Content!, "application/pdf");
                logger.LogInformation("AI call log PDF exported. CallLogId={CallLogId}, Bytes={Bytes}", currentItem.Id, outcome.Content!.Length);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AI call log PDF export failed.");
                ViewNotification.UnexpectedError(notificationService, $"PDF 匯出失敗：{ex.GetType().Name}。", ex);
            }
            finally
            {
                isExporting = false;
                StateHasChanged();
            }
        }

        /// <summary>同一段對話的所有呼叫合併成一份 PDF（0.9.169 起），超過 50 筆時提示（伺服器檢查）。</summary>
        private async Task OnExportConversationPdfAsync()
        {
            if (detail is null || detail.Conversation.Count == 0)
            {
                return;
            }

            isExporting = true;
            StateHasChanged();
            try
            {
                await AiCallLogView.ExportMergedPdfAsync(Api, jsRuntime, notificationService, logger,
                    new AiCallLogMergedPdfRequestDto { Source = "conversation", Ids = detail.Conversation.Select(x => x.Id).ToList() });
            }
            finally
            {
                isExporting = false;
                StateHasChanged();
            }
        }

        private static string AccountText(AiCallLogAdapterModel item)
        {
            var account = string.IsNullOrWhiteSpace(item.Account) ? "（系統自動）" : item.Account;
            return item.UserId is null ? account : $"{account}（Id={item.UserId}）";
        }

        private static StatusTone ResolveTone(AiCallLogAdapterModel item)
            => item.Success ? StatusTone.Positive : item.IsCanceled ? StatusTone.Muted : StatusTone.Warning;

        /// <summary>畫面上的一段內容：<see cref="Text"/> 最多 <see cref="MaxDisplayCharacters"/> 字元，<see cref="Length"/> 是完整長度。</summary>
        private sealed record DisplayBlock(
            string Key,
            string Heading,
            string Direction,
            StatusTone DirectionTone,
            string RoleClass,
            string Text,
            int Length,
            bool AllowMarkdown,
            string? Note);
    }
}
