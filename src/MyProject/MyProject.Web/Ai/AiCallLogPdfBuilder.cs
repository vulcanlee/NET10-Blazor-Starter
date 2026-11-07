using MigraDoc.DocumentObjectModel;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;
using MyProject.Web.Diagnostics;

namespace MyProject.Web.Ai;

/// <summary>
/// AI 對話紀錄 PDF 的輸入（0.9.72 起）。
///
/// ⚠️ 刻意沒有任何原始 JSON 欄位：PDF 只放中繼資料與完整對話，原始內容請下載 JSON。
/// </summary>
public sealed record AiCallLogPdfRequest
{
    public string SystemName { get; init; } = string.Empty;
    public string SystemVersion { get; init; } = string.Empty;
    public string OperatorAccount { get; init; } = string.Empty;
    public DateTime GeneratedAt { get; init; }
    public AiCallLogAdapterModel Item { get; init; } = new();
    public IReadOnlyList<AiCallLogMessage> Messages { get; init; } = [];

    /// <summary>取得的回應文字；失敗時為空字串。</summary>
    public string ResponseText { get; init; } = string.Empty;

    public TokenUsageLogAdapterModel? Usage { get; init; }

    /// <summary>內容檔不存在（0.9.169 起，合併匯出時仍列入這一筆，PDF 上註明）。</summary>
    public bool ContentMissing { get; init; }
}

/// <summary>
/// 多筆合併成一份 PDF 的輸入（0.9.169 起）。每筆的 <see cref="AiCallLogPdfRequest.Item"/>、訊息、回應與用量有效；
/// 系統名稱、匯出者、產生時間以這裡為準。
/// </summary>
public sealed record AiCallLogMergedPdfRequest
{
    public string SystemName { get; init; } = string.Empty;
    public string SystemVersion { get; init; } = string.Empty;
    public string OperatorAccount { get; init; } = string.Empty;
    public DateTime GeneratedAt { get; init; }
    public IReadOnlyList<AiCallLogPdfRequest> Items { get; init; } = [];
}

/// <summary>
/// 把一筆 AI 對話紀錄排成 PDF（0.9.72 起）。版面、樣式與 Markdown 渲染器共用
/// <see cref="AiMarkdownPdfRenderer"/>，與 <see cref="AiExceptionReportPdfBuilder"/> 一致。
///
/// system／user 訊息是送出的原文，以程式碼段落保留空白；assistant 內容是模型產出，經
/// <see cref="AiMarkdownPdfRenderer.RenderMarkdown"/>（已消毒）排版。
/// </summary>
public static class AiCallLogPdfBuilder
{
    /// <summary>單段內容的上限；超過截斷並註明（日誌分析的提示詞可能是整份日誌）。</summary>
    public const int MaxPdfBlockCharacters = 200_000;

    /// <summary>合併匯出一次最多幾筆（0.9.169 起）。</summary>
    public const int MaxMergedRecords = 50;

    /// <summary>合併匯出時每段內容的上限（0.9.169 起）：50 筆都是整份日誌時，單筆的 20 萬字上限會讓 PDF 大到難以開啟。</summary>
    public const int MaxMergedBlockCharacters = 50_000;

    /// <summary>
    /// 合併匯出整份 PDF 的內容總字數上限（0.9.169 起）：Agent 對話每次呼叫都重送整段歷史，
    /// 「整段對話」合併時內容會隨呼叫次數平方成長，只限每段不夠。用完之後的內容段只留一行註明。
    /// </summary>
    public const int MaxMergedTotalCharacters = 1_000_000;

    /// <summary>合併匯出的字數預算（0.9.169 起）：每段最多 <see cref="MaxMergedBlockCharacters"/>，整份最多建構時給的總數。</summary>
    public sealed class MergedContentBudget
    {
        private readonly int totalCharacters;

        public MergedContentBudget(int totalCharacters)
        {
            this.totalCharacters = totalCharacters;
            Remaining = totalCharacters;
        }

        public int Remaining { get; private set; }

        /// <summary>取這一段要放進 PDF 的文字與註明（沒截斷時為 null）；預算用完時文字為空字串。</summary>
        public (string Text, string? Note) Take(string content)
        {
            var wholeNote = $"（已達整份 PDF 的內容上限 {totalCharacters:N0} 字元，以下內容省略；完整內容請開啟單筆紀錄或下載 JSON。）";
            if (Remaining <= 0)
            {
                return (string.Empty, wholeNote);
            }

            if (content.Length > Remaining && Remaining < MaxMergedBlockCharacters)
            {
                var partial = content[..Remaining];
                Remaining = 0;
                return (partial, wholeNote);
            }

            var (text, note) = ClipContent(content, merged: true);
            Remaining -= text.Length;
            return (text, note);
        }
    }

    private const string Title = "AI 對話紀錄";
    private const string Disclaimer = "本文件包含實際送出給 AI 的內容與 AI 的回應，可能含日誌、例外堆疊或使用者帳號，請依內部規範保管。";

    /// <summary>角色的顯示名稱（畫面與 PDF 共用）。</summary>
    public static string RoleLabel(string role) => role switch
    {
        AiChatRoles.System => "系統提示（System）",
        AiChatRoles.User => "使用者（User）",
        AiChatRoles.Assistant => "AI 助理（Assistant）",

        // Agent 的工具執行結果（0.9.149 起 Responses 的 function_call_output 解析成 tool）。
        "tool" => "工具結果（Tool）",
        _ => string.IsNullOrWhiteSpace(role) ? "（未標示角色）" : role,
    };

    /// <summary>產生 PDF 位元組。</summary>
    /// <exception cref="InvalidOperationException">內嵌中文字型資源缺失。</exception>
    public static byte[] Build(AiCallLogPdfRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var systemName = string.IsNullOrWhiteSpace(request.SystemName) ? "MyProject" : request.SystemName;
        var document = AiMarkdownPdfRenderer.CreateDocument(
            title: Title,
            subject: request.Item.Operation,
            author: systemName,
            headerText: $"{systemName}｜{Title}｜{request.Item.Operation}",
            generatedAt: request.GeneratedAt,
            operatorAccount: request.OperatorAccount);
        var section = document.LastSection;

        AiMarkdownPdfRenderer.AddTitle(section, Title);
        AiMarkdownPdfRenderer.AddMetadata(section, BuildMetadataLines(request), Disclaimer);
        AddConversation(section, request, budget: null);

        return AiMarkdownPdfRenderer.Render(document);
    }

    /// <summary>
    /// 多筆合併成一份 PDF（0.9.169 起）：開頭是索引表，之後每筆一節（換頁），依送出時間由舊到新。
    /// </summary>
    /// <exception cref="ArgumentException">沒有任何一筆，或超過 <see cref="MaxMergedRecords"/> 筆（畫面應先擋下）。</exception>
    /// <exception cref="InvalidOperationException">內嵌中文字型資源缺失。</exception>
    public static byte[] BuildMerged(AiCallLogMergedPdfRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Items.Count == 0 || request.Items.Count > MaxMergedRecords)
        {
            throw new ArgumentException($"合併匯出需要 1～{MaxMergedRecords} 筆，目前是 {request.Items.Count} 筆。", nameof(request));
        }

        var systemName = string.IsNullOrWhiteSpace(request.SystemName) ? "MyProject" : request.SystemName;
        var title = $"{Title}（共 {request.Items.Count} 筆）";
        var document = AiMarkdownPdfRenderer.CreateDocument(
            title: title,
            subject: Title,
            author: systemName,
            headerText: $"{systemName}｜{title}",
            generatedAt: request.GeneratedAt,
            operatorAccount: request.OperatorAccount);
        var section = document.LastSection;

        AiMarkdownPdfRenderer.AddTitle(section, title);
        AiMarkdownPdfRenderer.AddMetadata(section, BuildMergedMetadataLines(request), Disclaimer);
        AddSectionHeading(section, "索引");
        foreach (var row in BuildIndexRows(request))
        {
            var paragraph = section.AddParagraph();
            paragraph.Style = AiMarkdownPdfRenderer.MetaStyleName;
            CjkLineBreak.AddTo(paragraph.Elements, $"{row[0]}．{row[1]}　{row[2]}　{row[3]}　{row[4]}");
        }

        var budget = new MergedContentBudget(MaxMergedTotalCharacters);
        var number = 0;
        foreach (var item in Ordered(request))
        {
            number++;
            section.AddPageBreak();
            AddSectionHeading(section, $"第 {number} 筆");

            // 系統、產生時間、匯出者已在開頭，每筆只列這筆自己的資料。
            foreach (var line in BuildMetadataLines(item).Where(x => x.Key is not ("系統" or "產生時間" or "匯出者")))
            {
                var paragraph = section.AddParagraph();
                paragraph.Style = AiMarkdownPdfRenderer.MetaStyleName;
                CjkLineBreak.AddTo(paragraph.Elements, $"{line.Key}：{line.Value}");
            }

            AddConversation(section, item, budget);
        }

        return AiMarkdownPdfRenderer.Render(document);
    }

    /// <summary>合併 PDF 開頭的索引：序號、送出時間、作業、發起者、結果，依送出時間由舊到新。純函式，讓測試直接斷言。</summary>
    public static List<IReadOnlyList<string>> BuildIndexRows(AiCallLogMergedPdfRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Ordered(request)
            .Select((item, index) => (IReadOnlyList<string>)
            [
                (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                $"{item.Item.OccurredAt:yyyy-MM-dd HH:mm:ss}",
                item.Item.Operation,
                string.IsNullOrWhiteSpace(item.Item.Account) ? "（系統自動）" : item.Item.Account,
                DescribeOutcome(item.Item),
            ])
            .ToList();
    }

    /// <summary>
    /// 一段內容依上限截斷：回傳要放進 PDF 的文字，以及截斷時的註明（沒截斷時為 null）。
    /// 單筆 PDF 上限 <see cref="MaxPdfBlockCharacters"/>，合併匯出 <see cref="MaxMergedBlockCharacters"/>。
    /// </summary>
    public static (string Text, string? Note) ClipContent(string content, bool merged)
    {
        var limit = merged ? MaxMergedBlockCharacters : MaxPdfBlockCharacters;
        if (content.Length <= limit)
        {
            return (content, null);
        }

        var where = merged ? "完整內容請開啟單筆紀錄或下載 JSON" : "完整內容請於畫面下載 JSON";
        return (content[..limit], $"（本段共 {content.Length:N0} 字元，PDF 只收錄前 {limit:N0} 字元；{where}。）");
    }

    private static IEnumerable<AiCallLogPdfRequest> Ordered(AiCallLogMergedPdfRequest request)
        => request.Items.OrderBy(x => x.Item.OccurredAt).ThenBy(x => x.Item.Id);

    private static List<KeyValuePair<string, string>> BuildMergedMetadataLines(AiCallLogMergedPdfRequest request)
    {
        var lines = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrWhiteSpace(request.SystemName) == false)
        {
            var system = string.IsNullOrWhiteSpace(request.SystemVersion)
                ? request.SystemName
                : $"{request.SystemName}　版本 {request.SystemVersion}";
            lines.Add(new KeyValuePair<string, string>("系統", system));
        }

        lines.Add(new KeyValuePair<string, string>("產生時間", $"{request.GeneratedAt:yyyy-MM-dd HH:mm:ss}"));
        if (string.IsNullOrWhiteSpace(request.OperatorAccount) == false)
        {
            lines.Add(new KeyValuePair<string, string>("匯出者", request.OperatorAccount));
        }

        var ordered = Ordered(request).ToList();
        lines.Add(new KeyValuePair<string, string>("筆數", request.Items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        lines.Add(new KeyValuePair<string, string>("送出時間範圍", $"{ordered[0].Item.OccurredAt:yyyy-MM-dd HH:mm:ss} ～ {ordered[^1].Item.OccurredAt:yyyy-MM-dd HH:mm:ss}"));
        return lines;
    }

    /// <summary>中介資訊行。抽成純函式讓測試直接斷言（方案內沒有 PDF 文字抽取相依）。</summary>
    public static List<KeyValuePair<string, string>> BuildMetadataLines(AiCallLogPdfRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var item = request.Item;
        var lines = new List<KeyValuePair<string, string>>();

        if (string.IsNullOrWhiteSpace(request.SystemName) == false)
        {
            var system = string.IsNullOrWhiteSpace(request.SystemVersion)
                ? request.SystemName
                : $"{request.SystemName}　版本 {request.SystemVersion}";
            lines.Add(new KeyValuePair<string, string>("系統", system));
        }

        lines.Add(new KeyValuePair<string, string>("產生時間", $"{request.GeneratedAt:yyyy-MM-dd HH:mm:ss}"));

        if (string.IsNullOrWhiteSpace(request.OperatorAccount) == false)
        {
            lines.Add(new KeyValuePair<string, string>("匯出者", request.OperatorAccount));
        }

        lines.Add(new KeyValuePair<string, string>("送出時間", $"{item.OccurredAt:yyyy-MM-dd HH:mm:ss}"));
        lines.Add(new KeyValuePair<string, string>("作業", item.Operation));
        lines.Add(new KeyValuePair<string, string>("發起者", string.IsNullOrWhiteSpace(item.Account) ? "（系統自動）" : item.Account));
        lines.Add(new KeyValuePair<string, string>("供應商／模型", $"{item.Provider}／{item.Model}"));
        lines.Add(new KeyValuePair<string, string>("結果", DescribeOutcome(item)));
        lines.Add(new KeyValuePair<string, string>("耗時", $"{item.ElapsedMilliseconds:N0} ms"));

        if (string.IsNullOrWhiteSpace(item.RelatedInfo) == false)
        {
            lines.Add(new KeyValuePair<string, string>("關聯說明", item.RelatedInfo));
        }

        if (request.Usage is { } usage)
        {
            var cost = usage.IsUnpriced ? "，未定價" : $"，約 NT$ {TokenUsageFormat.CostTwdCell(usage.CostTwd)}";
            lines.Add(new KeyValuePair<string, string>(
                "用量",
                $"輸入 {usage.InputCount?.ToString("N0") ?? "—"}／輸出 {usage.OutputCount?.ToString("N0") ?? "—"}{cost}"));
        }

        lines.Add(new KeyValuePair<string, string>("呼叫識別碼", item.CallId.ToString()));
        return lines;
    }

    /// <summary>結果文字（畫面與 PDF 共用）。</summary>
    public static string DescribeOutcome(AiCallLogAdapterModel item)
    {
        if (item.Success)
        {
            return "成功";
        }

        if (item.IsCanceled)
        {
            return "已取消";
        }

        return string.IsNullOrWhiteSpace(item.FailureReason) ? "失敗" : $"失敗（{item.FailureReason}）";
    }

    private static void AddConversation(Section section, AiCallLogPdfRequest request, MergedContentBudget? budget)
    {
        if (request.ContentMissing)
        {
            var missing = section.AddParagraph();
            missing.Style = AiMarkdownPdfRenderer.MetaStyleName;
            CjkLineBreak.AddTo(missing.Elements, "內容檔不存在或無法讀取（可能已清除、還原的備份沒有包含 AI 對話內容，或檔案損毀），只有上方的中繼資料。");
            return;
        }

        var number = 0;
        foreach (var message in request.Messages)
        {
            number++;
            AddSectionHeading(section, $"#{number} {RoleLabel(message.Role)}．送出");
            AddContent(section, message.Content, asMarkdown: message.Role == AiChatRoles.Assistant, budget);
        }

        AddSectionHeading(section, "AI 回應（Assistant）．取得");

        if (string.IsNullOrWhiteSpace(request.ResponseText))
        {
            var empty = section.AddParagraph();
            empty.Style = AiMarkdownPdfRenderer.MetaStyleName;
            CjkLineBreak.AddTo(empty.Elements, $"未取得內容（{DescribeOutcome(request.Item)}）。");
            return;
        }

        AddContent(section, request.ResponseText, asMarkdown: true, budget);
    }

    /// <summary>
    /// 報告的大章節用 Heading1：模型輸出的章節是 ## 與 ###（Heading2／3），
    /// 用 Heading1 才分得出「這是報告的結構」還是「模型寫的內容」（同 <see cref="AiExceptionReportPdfBuilder"/>）。
    /// </summary>
    private static void AddSectionHeading(Section section, string text)
    {
        var heading = section.AddParagraph(text);
        heading.Style = StyleNames.Heading1;
    }

    private static void AddContent(Section section, string content, bool asMarkdown, MergedContentBudget? budget)
    {
        var (text, truncatedNote) = budget is null ? ClipContent(content, merged: false) : budget.Take(content);

        if (text.Length == 0)
        {
            // 合併匯出的總字數用完：只留註明。
        }
        else if (asMarkdown)
        {
            AiMarkdownPdfRenderer.RenderMarkdown(section, text);
        }
        else
        {
            AiMarkdownPdfRenderer.AddCodeParagraph(section, text);
        }

        if (truncatedNote is not null)
        {
            var note = section.AddParagraph();
            note.Style = AiMarkdownPdfRenderer.MetaStyleName;
            CjkLineBreak.AddTo(note.Elements, truncatedNote);
        }
    }
}
