namespace MyProject.Dtos.Ai;

/// <summary>
/// AI 串流回應的一行（0.10.5 起，WASM 遷移 M6）。回應是 NDJSON（<c>application/x-ndjson</c>）：每行一個 JSON，依序為
/// 一筆 <see cref="AiStreamFrameKinds.Start"/>（範圍說明）、零到多筆 <see cref="AiStreamFrameKinds.Delta"/>（一段段的文字），
/// 最後一定是一筆 <see cref="AiStreamFrameKinds.Result"/>（成功、失敗、取消都是）。使用者中途中斷連線時伺服器照樣做完收尾（稽核、記帳），只是不再送出。
/// </summary>
public class AiStreamFrameDto
{
    /// <summary><see cref="AiStreamFrameKinds"/> 的其中一個。</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary><see cref="AiStreamFrameKinds.Delta"/>：這一段新收到的文字。</summary>
    public string? Text { get; set; }

    /// <summary><see cref="AiStreamFrameKinds.Start"/>：這次送了什麼範圍（日誌分析才有）。</summary>
    public AiStreamScopeDto? Scope { get; set; }

    /// <summary><see cref="AiStreamFrameKinds.Result"/>：最後結果。</summary>
    public AiResultDto? Result { get; set; }
}

public static class AiStreamFrameKinds
{
    public const string Start = "start";
    public const string Delta = "delta";
    public const string Result = "result";
}

/// <summary>日誌 AI 分析送出的範圍（開始時就告訴畫面，提示「正在將 N 筆日誌送給 AI 分析」）。時間是伺服器的本地時間。</summary>
public class AiStreamScopeDto
{
    public int TotalEntryCount { get; set; }

    public int IncludedEntryCount { get; set; }

    public bool DroppedByEntryLimit { get; set; }

    public DateTime? StartLocal { get; set; }

    public DateTime? EndLocal { get; set; }
}

/// <summary>一次 AI 呼叫的最後結果。失敗與取消也用這個型別表達。</summary>
public class AiResultDto
{
    public bool Success { get; set; }

    /// <summary>失敗原因（伺服器 <c>AiAnalysisFailureReason</c> 的名稱，例如 <c>Canceled</c>、<c>QuotaExceeded</c>）；成功為 <c>None</c>。</summary>
    public string Reason { get; set; } = "None";

    /// <summary>模型回傳的原始 Markdown（畫面自己消毒成 HTML）。</summary>
    public string Markdown { get; set; } = string.Empty;

    /// <summary>給使用者看的中文訊息。</summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>失敗的技術細節（只在錯誤通知的「複製」帶上；本系統只有管理員能呼叫 AI 分析端點）。</summary>
    public string? ErrorDetail { get; set; }

    public AiUsageDto? Usage { get; set; }

    public string ModelName { get; set; } = string.Empty;

    public bool IsTruncatedByLength { get; set; }

    /// <summary>產生途中被使用者停止（已收到部分文字）。</summary>
    public bool IsInterrupted { get; set; }

    public long ElapsedMilliseconds { get; set; }

    /// <summary>送出的筆數（日誌分析用；例外分析為 0）。</summary>
    public int IncludedEntryCount { get; set; }

    public int TotalEntryCount { get; set; }

    public bool DroppedByEntryLimit { get; set; }

    public int CharacterCount { get; set; }
}

/// <summary>API 回報的 Token 用量；沒回的欄位是 null。</summary>
public class AiUsageDto
{
    public int? InputCount { get; set; }

    public int? OutputCount { get; set; }

    public int? TotalCount { get; set; }

    public int? CachedInputCount { get; set; }

    public int? ReasoningCount { get; set; }
}

/// <summary>對話中的一則訊息（例外分析追問與匯出 PDF 時由畫面送回）。</summary>
public class AiChatMessageDto
{
    /// <summary><c>user</c> 或 <c>assistant</c>（不收 <c>system</c>）。</summary>
    public string Role { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    /// <summary>這則 AI 回覆是產生途中被停止的（匯出 PDF 時加註「已中止」）。</summary>
    public bool Interrupted { get; set; }
}
