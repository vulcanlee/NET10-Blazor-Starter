using Microsoft.AspNetCore.Mvc;
using MyProject.Dtos.Commons;
using MyProject.Models.AdapterModel;
using MyProject.Models.Systems;

namespace MyProject.Web.Controllers;

/// <summary>
/// 把服務的 <see cref="VerifyRecordResult"/> 轉成 API 回應（0.10.2 起，WASM 遷移 M3：分類、團隊的 API 改走與畫面相同的服務）。
/// 狀態碼依 <see cref="VerifyRecordResult.Kind"/>：找不到 404、重複或衝突 409、規則不符 400、未預期錯誤 500；訊息就是畫面上看到的那一句。
/// 500 時例外細節同樣交給 <see cref="ControllerApiResponseExtensions.ApiServerError(ControllerBase, string, Exception)"/> 依政策決定是否回傳。
/// </summary>
public static class ControllerVerifyResultExtensions
{
    public static ActionResult<ApiResult> FromVerify(this ControllerBase controller, VerifyRecordResult result, string successMessage)
        => result.Success ? controller.Ok(ApiResult.SuccessResult(successMessage)) : controller.FromFailure<object>(result);

    public static ActionResult<ApiResult<T>> FromVerify<T>(this ControllerBase controller, VerifyRecordResult result, Func<T> data, string successMessage)
        => result.Success ? controller.Ok(ApiResult<T>.SuccessResult(data(), successMessage)) : controller.FromFailure<T>(result);

    private static ObjectResult FromFailure<T>(this ControllerBase controller, VerifyRecordResult result)
    {
        if (result.Kind == VerifyRecordErrorKind.Error && result.Exception is { } exception)
        {
            return controller.ApiServerError<T>(result.Message, exception);
        }

        var body = result.Kind switch
        {
            VerifyRecordErrorKind.NotFound => ApiResult<T>.NotFoundResult(result.Message),
            VerifyRecordErrorKind.Conflict => ApiResult<T>.ConflictResult(result.Message),
            _ => ApiResult<T>.ValidationError(result.Message),
        };
        body.TraceId = controller.HttpContext.TraceIdentifier;
        return controller.StatusCode(body.StatusCode, body);
    }

    /// <summary>
    /// 端點本身只要求較低的權限、但某些參數需要更高權限時（例如「顯示已刪除」要刪除權限），沒有就回 403 <c>ApiResult</c>；有權限回 null。
    /// 規則與畫面相同（<see cref="MyProject.Business.Helpers.PermissionRules.CanAccessAction"/>，目前使用者由 ApiActorMiddleware 載入）。
    /// </summary>
    public static ObjectResult? ForbidUnless(this ControllerBase controller, bool allowed, string message)
    {
        if (allowed)
        {
            return null;
        }

        var body = ApiResult.ForbiddenResult(message);
        body.TraceId = controller.HttpContext.TraceIdentifier;
        return controller.StatusCode(StatusCodes.Status403Forbidden, body);
    }

    /// <summary>
    /// 搜尋請求的排序欄位（不分大小寫，例如 <c>name</c>、<c>NAME</c>）對應到畫面資料模型的屬性名稱；不認得的欄位回 null（服務退回預設排序）。
    /// </summary>
    public static string? ResolveSortField<TModel>(string? sortBy, params string[] sortable)
        => string.IsNullOrWhiteSpace(sortBy)
            ? null
            : sortable.FirstOrDefault(x => string.Equals(x, sortBy.Trim(), StringComparison.OrdinalIgnoreCase)
                && typeof(TModel).GetProperty(x) is not null);
}
