using MyProject.Models.Systems;

namespace MyProject.Business.Factories;

public static class VerifyRecordResultFactory
{
    public static VerifyRecordResult Build(bool success)
    {
        VerifyRecordResult verifyRecordResult = new VerifyRecordResult()
        {
            Success = success,
            Message = "",
            Exception = null,
        };
        return verifyRecordResult;
    }

    /// <summary>
    /// 使用文字內容來回應
    /// </summary>
    /// <param name="success"></param>
    /// <param name="message"></param>
    /// <returns></returns>
    public static VerifyRecordResult Build(bool success, string message, Exception? exception = null)
    {
        VerifyRecordResult verifyRecordResult = new VerifyRecordResult()
        {
            Success = success,
            Message = message,
            Exception = exception,
        };
        return verifyRecordResult;
    }

    /// <summary>找不到（或不在團隊範圍內）：API 回 404（0.10.2 起）。</summary>
    public static VerifyRecordResult NotFound(string message) => Build(false, message, VerifyRecordErrorKind.NotFound);

    /// <summary>重複、版本衝突或目前狀態不允許：API 回 409（0.10.2 起）。</summary>
    public static VerifyRecordResult Conflict(string message, Exception? exception = null) => Build(false, message, VerifyRecordErrorKind.Conflict, exception);

    /// <summary>輸入不符規則：API 回 400（0.10.2 起）。</summary>
    public static VerifyRecordResult Invalid(string message) => Build(false, message, VerifyRecordErrorKind.Invalid);

    /// <summary>未預期的錯誤（呼叫端已記 LogError）：API 回 500（0.10.2 起）。</summary>
    public static VerifyRecordResult Error(string message, Exception exception) => Build(false, message, VerifyRecordErrorKind.Error, exception);

    private static VerifyRecordResult Build(bool success, string message, VerifyRecordErrorKind kind, Exception? exception = null)
    {
        var result = Build(success, message, exception);
        result.Kind = kind;
        return result;
    }
}
