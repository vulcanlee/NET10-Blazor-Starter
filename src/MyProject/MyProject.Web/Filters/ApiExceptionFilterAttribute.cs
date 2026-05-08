using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using MyProject.Dtos.Commons;

namespace MyProject.Web.Filters;

public class ApiExceptionFilterAttribute : ExceptionFilterAttribute
{
    private readonly ILogger<ApiExceptionFilterAttribute> logger;

    public ApiExceptionFilterAttribute(ILogger<ApiExceptionFilterAttribute> logger)
    {
        this.logger = logger;
    }

    public override void OnException(ExceptionContext context)
    {
        if (!context.HttpContext.Request.Path.StartsWithSegments("/api"))
        {
            return;
        }

        logger.LogError(
            context.Exception,
            "Unhandled API exception. TraceId={TraceId}, Path={Path}",
            context.HttpContext.TraceIdentifier,
            context.HttpContext.Request.Path.Value);

        var result = ApiResult.FromException(
            context.Exception,
            "API 呼叫過程發生例外。",
            StatusCodes.Status500InternalServerError,
            context.HttpContext.TraceIdentifier);

        context.Result = new ObjectResult(result)
        {
            StatusCode = StatusCodes.Status500InternalServerError
        };
        context.ExceptionHandled = true;
    }
}
