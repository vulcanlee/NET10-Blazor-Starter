using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using MyProject.Dtos.Commons;
using MyProject.Web.Configuration;

namespace MyProject.Web.Filters;

public class ApiExceptionFilterAttribute : ExceptionFilterAttribute
{
    private readonly ILogger<ApiExceptionFilterAttribute> logger;
    private readonly SecuritySettings securitySettings;
    private readonly IWebHostEnvironment environment;

    public ApiExceptionFilterAttribute(
        ILogger<ApiExceptionFilterAttribute> logger,
        IOptions<SecuritySettings> securityOptions,
        IWebHostEnvironment environment)
    {
        this.logger = logger;
        securitySettings = securityOptions.Value;
        this.environment = environment;
    }

    public override void OnException(ExceptionContext context)
    {
        if (!context.HttpContext.Request.Path.StartsWithSegments("/api"))
        {
            return;
        }

        // 用戶端已中斷（換頁、關分頁、取消下載）時讀寫請求得到的「連線中斷」IO 例外或取消不是系統錯誤：只記 Information，
        // 不回 500、不進系統例外紀錄（0.10.16 起）。只放過這幾種：程式本身的錯誤或真正的 IO 錯誤（找不到檔案、磁碟錯誤）
        // 剛好碰上使用者離開，仍記成 Error。
        if (context.HttpContext.RequestAborted.IsCancellationRequested && IsCausedByClientDisconnect(context.Exception))
        {
            logger.LogInformation(
                "API request aborted by the client. TraceId={TraceId}, Path={Path}, Reason={Reason}",
                context.HttpContext.TraceIdentifier,
                context.HttpContext.Request.Path.Value,
                context.Exception.Message);
            context.Result = new StatusCodeResult(StatusCodes.Status499ClientClosedRequest); // 用戶端收不到；請求日誌記 499 而不是 200 或 500
            context.ExceptionHandled = true;
            return;
        }

        logger.LogError(
            context.Exception,
            "Unhandled API exception. TraceId={TraceId}, Path={Path}",
            context.HttpContext.TraceIdentifier,
            context.HttpContext.Request.Path.Value);

        var result = ShouldReturnExceptionDetails()
            ? ApiResult.FromException(
                context.Exception,
                "API 呼叫過程發生例外。",
                StatusCodes.Status500InternalServerError,
                context.HttpContext.TraceIdentifier)
            : new ApiResult
            {
                Success = false,
                StatusCode = StatusCodes.Status500InternalServerError,
                Message = "伺服器錯誤",
                ErrorMessage = "API 呼叫過程發生例外。",
                TraceId = context.HttpContext.TraceIdentifier
            };

        context.Result = new ObjectResult(result)
        {
            StatusCode = StatusCodes.Status500InternalServerError
        };
        context.ExceptionHandled = true;
    }

    /// <summary>
    /// 用戶端中斷造成的例外：取消、Kestrel 讀寫請求時的 <see cref="IOException"/> 本身（「The client reset the request stream.」）、
    /// 連線重設、請求逾時或資料速率過低（<see cref="BadHttpRequestException"/>）。<see cref="IOException"/> 的其他子類別（找不到檔案、磁碟錯誤）不算。
    /// </summary>
    private static bool IsCausedByClientDisconnect(Exception exception)
        => exception is OperationCanceledException
            or Microsoft.AspNetCore.Connections.ConnectionResetException
            or BadHttpRequestException
            || exception.GetType() == typeof(IOException);

    private bool ShouldReturnExceptionDetails()
    {
        return ExceptionDetailPolicy.ShouldReturnDetails(securitySettings, environment);
    }
}
