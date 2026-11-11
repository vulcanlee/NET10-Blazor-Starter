using System.ComponentModel.DataAnnotations;
using MyProject.Dtos.Commons;

namespace MyProject.Dtos.Ai;

/// <summary>
/// AI 對話紀錄頁的查詢條件（0.10.5 起，WASM 遷移 M6；<c>POST api/v1/ai-call-logs/search</c>、<c>export</c>，以及合併 PDF 的「目前查詢結果」）。
/// 日期是伺服器的本地日期（不帶時區），結束日含當天整天；<see cref="SearchRequestBaseDto.Keyword"/> 比對關聯說明。
/// 排序支援 <c>OccurredAt</c>、<c>ElapsedMilliseconds</c>、<c>RequestCharacters</c>，未指定時依送出時間由新到舊。
/// </summary>
public class AiCallLogSearchRequestDto : SearchRequestBaseDto
{
    /// <summary>內文搜尋一次最多掃描幾筆（與伺服器的 <c>AiCallLogService.ContentScanLimit</c> 相同），也是 <see cref="ContentIds"/> 的筆數上限。</summary>
    public const int MaxContentIds = 1000;

    public DateTime? StartDate { get; set; }

    public DateTime? EndDate { get; set; }

    /// <summary>作業（完全相同）。</summary>
    public string? Operation { get; set; }

    /// <summary>true 只看成功、false 只看失敗／取消、null 不限。</summary>
    public bool? Success { get; set; }

    /// <summary>模型（完全相同）。</summary>
    public string? Model { get; set; }

    /// <summary>發起者帳號（部分符合）。</summary>
    public string? Account { get; set; }

    /// <summary>
    /// 內文關鍵字：有填時伺服器先在其他條件的結果裡逐檔掃描（最新的 <see cref="MaxContentIds"/> 筆、不分大小寫），
    /// 結果回在 <see cref="AiCallLogPageDto.ContentSearch"/>；之後換頁、排序與匯出改帶 <see cref="ContentIds"/>，不再重掃。
    /// 掃描可能很久，畫面以長時間請求送出、離開頁面時取消。
    /// </summary>
    public string? ContentKeyword { get; set; }

    /// <summary>上次內文搜尋符合的紀錄 Id；null 不限、空集合代表一筆都沒有。填了 <see cref="ContentKeyword"/> 時不看這個欄位。</summary>
    [MaxLength(MaxContentIds, ErrorMessage = "內文搜尋的結果最多 1000 筆。")]
    public List<int>? ContentIds { get; set; }

    /// <summary>true：使用者按下「查詢」，伺服器寫一筆搜尋稽核；換頁、排序、重新整理不帶。</summary>
    public bool RecordSearch { get; set; }
}

/// <summary>清單的一頁，加上這次內文搜尋的結果（有填內文關鍵字時才有）。</summary>
public class AiCallLogPageDto : PagedResult<AiCallLogItemDto>
{
    public AiCallLogContentSearchDto? ContentSearch { get; set; }
}

/// <summary>內文搜尋的結果。</summary>
public class AiCallLogContentSearchDto
{
    /// <summary>內文符合的紀錄 Id，依送出時間由新到舊。</summary>
    public List<int> Ids { get; set; } = [];

    /// <summary>掃描了幾筆。</summary>
    public int Scanned { get; set; }

    /// <summary>篩選結果超過掃描上限，較舊的沒有掃到。</summary>
    public bool Truncated { get; set; }
}

/// <summary>AI 對話紀錄的一列（只有中繼資料，內文在明細）。送出時間是伺服器的本地時間、不帶時差。</summary>
public class AiCallLogItemDto
{
    public int Id { get; set; }

    public Guid CallId { get; set; }

    public DateTime OccurredAt { get; set; }

    public string Operation { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string? Account { get; set; }

    public int? UserId { get; set; }

    public bool Success { get; set; }

    public string? FailureReason { get; set; }

    public int? HttpStatus { get; set; }

    public string? FinishReason { get; set; }

    public long ElapsedMilliseconds { get; set; }

    public int RequestCharacters { get; set; }

    public int ResponseCharacters { get; set; }

    public string? RelatedInfo { get; set; }

    public Guid? ConversationId { get; set; }

    public Guid? RunId { get; set; }

    public bool HasContent { get; set; }
}

/// <summary>AI 對話紀錄頁的選項（<c>GET api/v1/ai-call-logs/options</c>）。</summary>
public class AiCallLogOptionsDto
{
    /// <summary>資料庫裡出現過的作業。</summary>
    public List<string> Operations { get; set; } = [];

    /// <summary>資料庫裡出現過的模型。</summary>
    public List<string> Models { get; set; } = [];

    /// <summary>目前是否記錄 AI 對話內容（系統參數「記錄 AI 對話內容」）。</summary>
    public bool Enabled { get; set; }

    /// <summary>保留天數。</summary>
    public int RetentionDays { get; set; }

    /// <summary>內文搜尋一次最多掃描幾筆。</summary>
    public int ContentScanLimit { get; set; }
}

/// <summary>
/// 明細窗（<c>GET api/v1/ai-call-logs/{id}</c>）：中繼資料、用量、同一次執行與同一段對話，以及五種檢視的內容。
/// 每段內容只送前 <see cref="MaxTextCharacters"/> 字元（畫面也只顯示這麼多），完整內容用 <c>{id}/text</c> 取得或下載 JSON。
/// ⚠️ 原始 Request 本來就不含 HTTP 標頭與金鑰（內容檔不存），這裡也不多送任何東西。
/// </summary>
public class AiCallLogDetailDto
{
    /// <summary>每段內容送出的字元上限。</summary>
    public const int MaxTextCharacters = 200_000;

    public AiCallLogItemDto Item { get; set; } = new();

    /// <summary>內容檔存在且讀得出來；false 時只有中繼資料（下列內容欄位都是 null）。</summary>
    public bool HasContent { get; set; }

    /// <summary>例外型別名稱（逾時、連線失敗、非預期錯誤時）。</summary>
    public string? ExceptionType { get; set; }

    public string? Endpoint { get; set; }

    /// <summary>設定上要求的模型（與實際模型不同時畫面顯示「要求 → 實際」）。</summary>
    public string? RequestedModel { get; set; }

    /// <summary>由原始請求解析出的訊息；無法解析時為 null。</summary>
    public List<AiCallLogMessageDto>? Messages { get; set; }

    /// <summary>AI 回應文字（沒取得時是空字串）。</summary>
    public AiCallLogTextDto? Response { get; set; }

    /// <summary>原始請求（JSON 已排版）。</summary>
    public AiCallLogTextDto? RawRequest { get; set; }

    /// <summary>原始回應（JSON 已排版）；沒有收到回應 body 時為 null。</summary>
    public AiCallLogTextDto? RawResponse { get; set; }

    /// <summary>「用量與費用」欄的文字（由伺服器組好）。</summary>
    public string UsageText { get; set; } = string.Empty;

    /// <summary>同一段對話的所有紀錄（依時間由舊到新）；不屬於任何對話時為空。</summary>
    public List<AiCallLogItemDto> Conversation { get; set; } = [];

    /// <summary>同一次執行的所有紀錄（依時間由舊到新）；沒有執行編號時為空。</summary>
    public List<AiCallLogItemDto> Run { get; set; } = [];
}

/// <summary>請求中的一則訊息。</summary>
public class AiCallLogMessageDto
{
    public string Role { get; set; } = string.Empty;

    public AiCallLogTextDto Content { get; set; } = new();
}

/// <summary>一段內容：<see cref="Text"/> 最多 <see cref="AiCallLogDetailDto.MaxTextCharacters"/> 字元，<see cref="Length"/> 是完整長度。</summary>
public class AiCallLogTextDto
{
    public string Text { get; set; } = string.Empty;

    public int Length { get; set; }
}

/// <summary>批次清除（<c>POST api/v1/ai-call-logs/purge</c>）：刪除這一天（不含）之前的紀錄與內容檔。</summary>
public class AiCallLogPurgeRequestDto
{
    [Required(ErrorMessage = "請選擇日期。")]
    public DateTime? BeforeDate { get; set; }
}

/// <summary>
/// 合併匯出 PDF（<c>POST api/v1/ai-call-logs/merged-pdf</c>）。<see cref="Source"/>：
/// <c>selected</c>（勾選的紀錄）、<c>conversation</c>（明細窗的整段對話）帶 <see cref="Ids"/>；<c>query</c>（目前查詢結果）帶 <see cref="Query"/>。
/// 一次最多 50 筆，超過回 400。
/// </summary>
public class AiCallLogMergedPdfRequestDto
{
    public string Source { get; set; } = string.Empty;

    public List<int>? Ids { get; set; }

    public AiCallLogSearchRequestDto? Query { get; set; }
}
