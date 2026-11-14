using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyProject.Web.Configuration;
using MyProject.Web.Filters;

namespace MyProject.Tests;

/// <summary>
/// 用戶端中斷的請求（0.10.16 起，.NET 11 全面實機探索發現）：使用者在查詢還沒回來時就換頁或關掉分頁，
/// 伺服器讀寫請求時得到 <c>IOException: The client reset the request stream.</c>。這不是系統錯誤——
/// 以前 <see cref="ApiExceptionFilterAttribute"/> 一律記成 Error，系統例外紀錄頁就多出一筆「錯誤」（.NET 10 也一樣）。
/// 請求已被用戶端中止時（IO 例外或取消）只記 Information；請求還在時的同一種例外照常記成 Error。
/// </summary>
public sealed class ApiClientAbortTests
{
    private static (ApiExceptionFilterAttribute Filter, CapturingLogger<ApiExceptionFilterAttribute> Logger) CreateFilter()
    {
        var logger = new CapturingLogger<ApiExceptionFilterAttribute>();
        return (new ApiExceptionFilterAttribute(logger, Options.Create(new SecuritySettings()), new TestWebHostEnvironment()), logger);
    }

    private static ExceptionContext CreateContext(Exception exception, bool aborted)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/v1/Project/search";
        if (aborted)
        {
            var source = new CancellationTokenSource();
            source.Cancel();
            http.RequestAborted = source.Token;
        }

        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
        return new ExceptionContext(actionContext, []) { Exception = exception };
    }

    [Fact]
    public void ClientReset_AfterTheClientAborted_ShouldNotBeLoggedAsAnError()
    {
        var (filter, logger) = CreateFilter();
        var context = CreateContext(new IOException("The client reset the request stream."), aborted: true);

        filter.OnException(context);

        Assert.DoesNotContain(logger.Entries, x => x.Level >= LogLevel.Warning);
        Assert.Contains(logger.Entries, x => x.Level == LogLevel.Information);
        Assert.True(context.ExceptionHandled);
        Assert.Equal(StatusCodes.Status499ClientClosedRequest, (context.Result as StatusCodeResult)?.StatusCode);
    }

    [Fact]
    public void Cancellation_AfterTheClientAborted_ShouldNotBeLoggedAsAnError()
    {
        // 系統健康頁讀警告日誌時使用者換頁：讀檔以 RequestAborted 取消，得到 TaskCanceledException（實機發現）。
        var (filter, logger) = CreateFilter();
        var context = CreateContext(new TaskCanceledException("A task was canceled."), aborted: true);

        filter.OnException(context);

        Assert.DoesNotContain(logger.Entries, x => x.Level >= LogLevel.Warning);
        Assert.True(context.ExceptionHandled);
    }

    [Fact]
    public void IoError_WhileTheClientIsStillConnected_ShouldStillBeLoggedAsAnError()
    {
        var (filter, logger) = CreateFilter();
        var context = CreateContext(new IOException("Disk full."), aborted: false);

        filter.OnException(context);

        Assert.Contains(logger.Entries, x => x.Level == LogLevel.Error);
        Assert.Equal(StatusCodes.Status500InternalServerError, (context.Result as ObjectResult)?.StatusCode);
    }

    [Fact]
    public void RealIoErrors_AfterTheClientAborted_ShouldStillBeLoggedAsAnError()
    {
        // 只放過「中斷造成的」IO 例外（獨立審查提醒）：找不到檔案、磁碟錯誤這類真正的 IO 錯誤剛好碰上使用者離開，仍要記成 Error。
        var (filter, logger) = CreateFilter();
        var context = CreateContext(new FileNotFoundException("備份檔不見了", "backup.zip"), aborted: true);

        filter.OnException(context);

        Assert.Contains(logger.Entries, x => x.Level == LogLevel.Error);
    }

    [Fact]
    public void OtherErrors_AfterTheClientAborted_ShouldStillBeLoggedAsAnError()
    {
        // 只放過「中斷造成的」IO 例外：程式本身的錯誤剛好碰上使用者離開，仍要記下來。
        var (filter, logger) = CreateFilter();
        var context = CreateContext(new InvalidOperationException("真正的錯誤"), aborted: true);

        filter.OnException(context);

        Assert.Contains(logger.Entries, x => x.Level == LogLevel.Error);
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "MyProject.Web";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
