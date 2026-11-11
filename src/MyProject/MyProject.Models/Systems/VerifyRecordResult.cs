namespace MyProject.Models.Systems;

public struct VerifyRecordResult
{
    public bool Success { get; set; }
    public Exception? Exception { get; set; }
    public string Message { get; set; }

    /// <summary>
    /// 失敗的種類（0.10.2 起，WASM 遷移 M3）：Web API 改走與畫面相同的服務後，用它決定 HTTP 狀態碼
    /// （<c>ControllerVerifyResultExtensions.FromVerify</c>）。畫面只看 <see cref="Message"/>，不受影響。
    /// </summary>
    public VerifyRecordErrorKind Kind { get; set; }
}

/// <summary><see cref="VerifyRecordResult"/> 失敗的種類與對應的 HTTP 狀態碼。</summary>
public enum VerifyRecordErrorKind
{
    /// <summary>沒有標示（成功，或一般的規則不符 → 400）。</summary>
    None = 0,

    /// <summary>找不到，或不在自己的團隊範圍內（一律當作不存在）→ 404。</summary>
    NotFound,

    /// <summary>名稱或代號重複、版本衝突、目前狀態不允許（例如還有下屬部門）→ 409。</summary>
    Conflict,

    /// <summary>輸入不符規則（例如指定範圍外的團隊、上層選到自己的下屬）→ 400。</summary>
    Invalid,

    /// <summary>未預期的錯誤（已記錯誤日誌）→ 500。</summary>
    Error,
}
