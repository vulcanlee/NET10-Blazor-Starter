namespace MyProject.Web.Ai;

/// <summary>
/// 一次 AI 作業的呼叫情境（0.9.140 起）。建立 agent 時交給 <see cref="IAiAgentFactory"/>，
/// 記帳中介層 <see cref="AiAccountingChatClient"/> 對這個 agent 的<b>每一次</b>模型呼叫都套用它。
///
/// 後三個文字屬性是「給使用者看的建議」：同一種上游錯誤，日誌分析要說「縮小時間區間」，
/// 例外分析則沒有範圍可縮，所以由呼叫端決定措辭，共用核心不寫死任何一方的畫面用語。
/// </summary>
public sealed record AiCallContext
{
    /// <summary>Token 用量的作業名稱（<c>TokenUsageOperations</c> 的常數）。</summary>
    public required string Operation { get; init; }

    /// <summary>單次模型呼叫的逾時（不是整個 agent 執行的總時限）。</summary>
    public required TimeSpan Timeout { get; init; }

    /// <summary>
    /// 是否在每次模型呼叫前檢查 AI 用量上限。
    /// 只有系統自發、成本固定極低的呼叫（健康探測）設為 false。
    /// </summary>
    public bool EnforceQuota { get; init; } = true;

    /// <summary>記進 AI 對話紀錄的可讀關聯說明（例如「例外紀錄 #42（…）」）。</summary>
    public string? RelatedInfo { get; init; }

    /// <summary>同一段對話的識別碼；沒有對話概念的作業為 null。</summary>
    public Guid? ConversationId { get; init; }

    /// <summary>
    /// 同一次執行的識別碼（0.9.142 起）：一次按鈕、一輪追問或 Agent 的一次執行。
    /// 這次執行裡每一次模型呼叫（含工具呼叫迴圈）的 Token 用量與 AI 對話紀錄都帶同一個值。
    /// </summary>
    public Guid? RunId { get; init; }

    /// <summary>
    /// 要用哪個功能的模型設定（0.9.142 起）：<c>nameof(AiModelSettings.屬性)</c>。
    /// null 或該功能在系統參數沒設定時用 <c>AiSettings.Model</c>。
    /// </summary>
    public string? ModelKey { get; init; }

    /// <summary>上游回 <c>context_length_exceeded</c> 時給使用者看的完整訊息。</summary>
    public string ContextLengthExceededMessage { get; init; } = string.Empty;

    /// <summary>逾時訊息的後半句建議，例如「請縮小查詢範圍後再試。」</summary>
    public string TimeoutHint { get; init; } = string.Empty;

    /// <summary>通用 400 訊息裡指稱送出內容的詞，例如「送出的日誌量」。</summary>
    public string SubmittedContentLabel { get; init; } = string.Empty;
}

/// <summary>
/// 一次模型呼叫失敗（0.9.140 起）。由 <see cref="AiAccountingChatClient"/> 在<b>已記帳之後</b>丟出，
/// 穿過 MAF 的 agent 管線回到呼叫端，呼叫端再轉成自己的結果型別。
///
/// ⚠️ <see cref="Exception.Message"/> 就是給使用者看的中文訊息，規則同 <see cref="AiAnalysisResult.ErrorMessage"/>：
/// 只由固定字串與上游 error code／param 組成，絕不夾帶上游回應 body。
/// </summary>
public sealed class AiCallException : Exception
{
    public AiCallException(
        AiAnalysisFailureReason reason,
        string message,
        TimeSpan elapsed,
        string? detail = null,
        AiTokenUsage? usage = null,
        int? httpStatus = null,
        AiUpstreamError? upstream = null,
        string? exceptionType = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
        Elapsed = elapsed;
        Detail = detail;
        Usage = usage;
        HttpStatus = httpStatus;
        Upstream = upstream ?? new AiUpstreamError();
        ExceptionType = exceptionType;
    }

    public AiAnalysisFailureReason Reason { get; }

    public TimeSpan Elapsed { get; }

    /// <summary>給管理員複製用的技術細節（見 <see cref="AiAnalysisResult.ErrorDetail"/>）。</summary>
    public string? Detail { get; }

    public AiTokenUsage? Usage { get; }

    /// <summary>上游 HTTP 狀態；沒有收到回應（逾時、連線失敗、用量上限）時為 null。</summary>
    public int? HttpStatus { get; }

    public AiUpstreamError Upstream { get; }

    /// <summary>傳輸層或非預期失敗時的例外型別名稱。</summary>
    public string? ExceptionType { get; }

    public AiAnalysisResult ToResult()
        => AiAnalysisResult.Failure(Reason, Message, elapsed: Elapsed, usage: Usage, detail: Detail);
}

/// <summary>一次成功的模型呼叫（由記帳中介層從回應原文解析而來）。</summary>
public sealed record AiCallOutcome(
    string Content,
    string ModelName,
    string FinishReason,
    AiTokenUsage? Usage,
    TimeSpan Elapsed);

/// <summary>
/// 同一個 agent 的呼叫狀態（0.9.140 起），由 <see cref="IAiAgentFactory"/> 每建一個 agent 就配一份。
///
/// <para>
/// 記帳中介層與 SDK 管線上的 <see cref="AiHttpCapturePolicy"/> 透過 <see cref="Current"/> 交接
/// 「目前這一次模型呼叫」的擷取物件。同一個 agent 的模型呼叫是循序的（工具呼叫迴圈也是），
/// 而每個作業都建自己的 agent，所以不需要 AsyncLocal。
/// </para>
/// </summary>
public sealed class AiCallState
{
    private readonly List<AiCallOutcome> outcomes = [];

    internal AiCallCapture? Current { get; set; }

    /// <summary>
    /// 目前這次執行的識別碼（0.9.142 起）。同一個 agent 跑很多次（例如 Agent 對話的每一則訊息）時，
    /// 由執行器在每次執行前換一個新值；為 null 時用 <see cref="AiCallContext.RunId"/>。
    /// </summary>
    public Guid? RunId { get; set; }

    /// <summary>這個 agent 所有成功的模型呼叫，依呼叫順序。</summary>
    public IReadOnlyList<AiCallOutcome> Outcomes => outcomes;

    public AiCallOutcome? LastOutcome => outcomes.Count == 0 ? null : outcomes[^1];

    internal void Add(AiCallOutcome outcome) => outcomes.Add(outcome);
}
